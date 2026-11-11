"""Recovery executes original data authentication and conservative stale deletion."""
from __future__ import annotations

import copy
import base64
from contextlib import contextmanager, redirect_stderr
from datetime import datetime, timedelta, timezone
import http.server
import io
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import threading
import unittest
import urllib.request
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_complete as complete
import release_draft as draft
import release_publish as publisher
import oci_publish
from release_state import NoRedirect, ReleaseError
from test_release_draft import ARTIFACT, RUN, VERSION, REPOSITORY, History, artifact, digest, git_command
from test_release_publish import PublishFixture, draft_files
from actions_expressions import run_composite

WORKFLOW_REF = REPOSITORY + "/.github/workflows/release.yml@refs/heads/main"


class CompleteFixture(PublishFixture):
    def __init__(self, sha, payload, git):
        super().__init__(sha, payload, git)
        self.additional, self.deletes = [], []
        self.deleted = False
        self.delete_status, self.release_reads = 204, 0
        self.before_release = lambda _control: None

    def get(self, path, deadline=None):
        if path in self.errors:
            raise draft.GitHubHttpError(path, self.errors[path])
        if "/releases/" in path:
            if self.deleted:
                raise draft.GitHubHttpError(path, 404)
            self.release_reads += 1
            self.before_release(self)
        return copy.deepcopy(super().get(path, deadline=deadline))

    def pages(self, path, deadline=None):
        self.events.append("LIST")
        yield from copy.deepcopy([*([self.release] if not self.deleted else []), *self.additional])

    def delete_draft(self, identity):
        self.events.append("DELETE")
        if self.delete_status != 204:
            raise draft.GitHubHttpError("/releases/" + str(identity), self.delete_status)
        self.deletes.append(identity)
        self.deleted = True


class CompleteTests(History):
    def control(self, payload=None):
        return CompleteFixture(self.sha, payload or self.payload, self.git)

    def invoke(self, control, **changes):
        values = {"sha": self.sha, "version": VERSION, "draft_id": "789", "last_tag": "v2.3.4",
                  "workflow_ref": WORKFLOW_REF}
        values.update(changes)
        self.emitted = []
        return complete.complete_draft(self.git, control, emit=self.emitted.append, **values)

    def gone(self, control):
        control.errors[f"/repos/{REPOSITORY}/actions/runs/{RUN}"] = 404

    def test_original_handoff_survives_dispatch_workflow_config_and_current_run_changes(self):
        control = self.control()
        (self.root / ".github/workflows/release.yml").write_text("new caller version\n")
        (self.root / ".github/hexalith-ci.json").write_text("invalid current source config")
        self.commit("ci: advance dispatch workflow and settings")
        actual = self.git.run
        def read(*args, **kwargs):
            self.assertNotIn(args[0], {"fetch", "checkout", "show"})
            self.assertFalse(args[:2] == ("cat-file", "blob"))
            return actual(*args, **kwargs)
        with patch.object(self.git, "run", side_effect=read), patch.object(
                draft, "strict_successor", side_effect=AssertionError("create version rule ran")):
            result = self.invoke(control)
        self.assertEqual(result, {**control.values, "noop-reason": ""})
        self.assertEqual(self.emitted, [result])
        self.assertEqual(control.writes + control.deletes, [])
        self.assertNotIn("/ci.yml/", " ".join(control.events))
        self.assertEqual(control.events.count("download"), 1)

    def test_original_oci_artifact_handoff_and_container_trailer_disagreement(self):
        import oci_archive as oci
        import test_release_oci as fixtures
        sources = []
        with patch.object(fixtures, "SHA", self.sha), patch.object(fixtures, "REPOSITORY", REPOSITORY):
            for architecture in ("amd64", "arm64"):
                source = self.root / (architecture + ".tar")
                fixtures.sdk_archive(source, architecture, version=VERSION)
                sources.append((source, {"os": "linux", "architecture": architecture}))
        combined = self.root / "original-image.tar"
        oci.combine_archives(sources, combined, revision=self.sha, version=VERSION,
                             repository=REPOSITORY, name="product/image")
        manifest = {"sha": self.sha, "version": VERSION, "packages": [],
                    "containers": [{"name": "product/image", "path": "containers/image.tar"}]}
        payload = artifact(self.sha, packages=False, manifest=manifest,
                           extra=[("containers/image.tar", combined.read_bytes())])
        control = self.control(payload)
        result = self.invoke(control, registry=lambda *_args: self.fail("recovery attempted registry publication preflight"))
        self.assertEqual(result, {**control.values, "noop-reason": ""})
        self.assertEqual(result["artifact-digest"], digest(payload))
        self.assertEqual(control.events.count("download"), 1)
        self.assertEqual(control.deletes + control.writes, [])
        control = self.control(payload)
        control.release["body"] = control.release["body"].replace(
            "hexalith-release-containers: product/image", "hexalith-release-containers: other/image")
        with self.assertRaisesRegex(ReleaseError, "inventory differs from draft release trailers"):
            self.invoke(control)
        self.assertEqual(control.deletes + control.writes, [])
        self.assertEqual(self.emitted, [])

    def test_draft_changed_during_download_never_emits_a_publication_handoff(self):
        for mutation in ({"body": "changed notes"}, {"draft": False}):
            control = self.control()
            expected = {**mutation}
            if "body" in mutation:
                expected["body"] = control.release["body"].replace("Original notes", mutation["body"])
            download = control.artifact_bytes
            def changed(identity):
                payload = download(identity)
                control.release.update(expected)
                return payload
            control.artifact_bytes = changed
            with self.subTest(mutation=mutation), self.assertRaises(ReleaseError):
                self.invoke(control)
            self.assertEqual(control.deletes + control.writes, [])
            self.assertEqual(self.emitted, [])
            self.assertEqual({key: control.release[key] for key in expected}, expected)

    def test_existing_non_successor_version_and_captured_base_are_preserved(self):
        with patch("test_release_publish.VERSION", "2.3.9"):
            control = self.control(artifact(self.sha, version="2.3.9"))
        git_command(self.root, "tag", "v2.3.8", self.sha)
        result = self.invoke(control, version="2.3.9")
        self.assertEqual((result["version"], result["last-tag"]), ("2.3.9", "v2.3.4"))
        for last in ("v9.9.9", "v2.3.9"):
            with self.subTest(last=last), self.assertRaises(ReleaseError):
                self.invoke(control, version="2.3.9", last_tag=last)
        self.assertEqual(control.deletes, [])

    def test_every_marker_missing_duplicated_or_malformed_fails_without_any_write(self):
        for marker in draft.MARKERS:
            for mutation in ("missing", "duplicate", "invalid", "case", "indent"):
                control = self.control()
                lines = control.release["body"].splitlines(keepends=True)
                line = next(line for line in lines if line.startswith(marker + ":"))
                replacement = {"missing": "", "duplicate": line + line,
                               "invalid": marker + ": ../unsafe\n", "case": line.upper(),
                               "indent": "  " + line}[mutation]
                control.release["body"] = control.release["body"].replace(line, replacement)
                with self.subTest(marker=marker, mutation=mutation), self.assertRaises(ReleaseError):
                    self.invoke(control)
                self.assertEqual(control.deletes + control.writes, [])
                self.assertEqual(self.emitted, [])
        for text in ("Fixture.Package,fixture.package", "Fixture.Package,,Other", "../../unsafe", "Fixture.Package, Other"):
            control = self.control()
            control.release["body"] = control.release["body"].replace("Fixture.Package\n", text + "\n")
            with self.subTest(inventory=text), self.assertRaises(ReleaseError):
                self.invoke(control)

    def test_source_release_artifact_and_inventory_disagreement_never_deletes(self):
        mutations = [lambda c: c.release.update(author={"login": "human"}),
                     lambda c: c.release.update(id=1), lambda c: c.release.update(draft=False),
                     lambda c: c.release.update(prerelease=True), lambda c: c.release.update(target_commitish="a" * 40),
                     lambda c: c.release.update(tag_name="v9.9.9"), lambda c: c.metadata.update(id=2),
                     lambda c: c.metadata.update(name="not-release"),
                     lambda c: c.metadata.update(workflow_run={"id": RUN + 1, "head_sha": self.sha}),
                     lambda c: c.metadata.update(workflow_run={"id": RUN, "head_sha": "a" * 40}),
                     lambda c: c.metadata.update(digest="sha256:" + "0" * 64, expired=True),
                     lambda c: c.metadata.update(expires_at=None), lambda c: c.metadata.update(expired="true"),
                     lambda c: setattr(c, "payload", c.payload + b"changed"),
                     lambda c: c.release.update(body=c.release["body"].replace("Fixture.Package\n", "Other.Package\n"))]
        for index, mutate in enumerate(mutations):
            control = self.control()
            mutate(control)
            with (self.subTest(index=index),
                  patch.object(complete, "destinations_absent", side_effect=AssertionError("verification error became absence")),
                  self.assertRaises(ReleaseError)):
                self.invoke(control)
            self.assertEqual(control.deletes + control.writes, [])
            self.assertEqual(self.emitted, [])

    def test_unsafe_corrupt_payload_and_invalid_expiry_are_never_absence(self):
        for payload in (self.payload[:20], artifact(self.sha, extra=(("../unsafe", b"x"),)),
                        artifact(self.sha, nuspec=b"not-zip")):
            control = self.control()
            control.payload = payload
            control.metadata["digest"] = digest(payload)
            control.release["body"] = control.release["body"].replace(digest(self.payload), digest(payload))
            with (patch.object(complete, "destinations_absent", side_effect=AssertionError("corruption became absence")),
                  self.assertRaises(ReleaseError)):
                self.invoke(control)
            self.assertEqual(control.deletes, [])
        control = self.control()
        control.metadata.update(expired=True, expires_at="invalid")
        with self.assertRaisesRegex(ReleaseError, "expiry"):
            self.invoke(control)
        self.assertEqual(control.deletes, [])

    def test_each_well_formed_producer_mismatch_and_404_allow_only_conservative_stale(self):
        changes = [{"id": RUN + 1}, {"path": ".github/workflows/other.yml"}, {"event": "push"},
                   {"head_branch": "other"}, {"head_sha": "a" * 40}, {"missing": True}]
        for change in changes:
            control = self.control(artifact(self.sha, packages=False))
            if "missing" in change:
                self.gone(control)
            else:
                control.run.update(change)
            with self.subTest(change=change):
                result = self.invoke(control, registry=lambda *args: self.fail("empty OCI inventory requested"))
            self.assertEqual(control.deletes, [789])
            self.assertEqual(list(control.pages("/releases")), [])
            self.assertEqual(result["publish"], "false")
            self.assertEqual(result["noop-reason"], "stale-draft-deleted")
            self.assertNotIn("download", control.events)
            self.assertEqual(git_command(self.root, "tag", "--list", "v" + VERSION), "")

    def test_definitive_metadata_archive_disappearance_or_valid_expiry_allows_stale(self):
        for reason in ("metadata404", "metadata410", "archive404", "archive410", "flag", "date"):
            control = self.control(artifact(self.sha, packages=False))
            if reason.startswith("metadata"):
                control.errors[f"/repos/{REPOSITORY}/actions/artifacts/{ARTIFACT}"] = int(reason[-3:])
            elif reason.startswith("archive"):
                control.artifact_bytes = lambda _identity, status=int(reason[-3:]): (_ for _ in ()).throw(
                    draft.GitHubHttpError("/zip", status))
            elif reason == "flag":
                control.metadata["expired"] = True
            else:
                control.metadata["expires_at"] = (datetime.now(timezone.utc) - timedelta(days=1)).isoformat()
            with self.subTest(reason=reason):
                result = self.invoke(control)
            self.assertEqual(result["noop-reason"], "stale-draft-deleted")
            self.assertEqual(control.deletes, [789])

    def test_http_transport_auth_redirect_and_malformed_producer_fail_without_stale(self):
        for suffix in (f"actions/runs/{RUN}", f"actions/artifacts/{ARTIFACT}"):
            for status in (301, 302, 401, 403, 429, 500, 503):
                control = self.control(artifact(self.sha, packages=False))
                control.errors[f"/repos/{REPOSITORY}/{suffix}"] = status
                with self.subTest(suffix=suffix, status=status), self.assertRaises(ReleaseError):
                    self.invoke(control)
                self.assertEqual(control.deletes, [])
        for run in (None, {}, [], {"id": True}, {"id": RUN, "path": 5},
                    {"id": RUN, "path": "workflow", "event": "push", "head_branch": "main", "head_sha": "invalid"}):
            control = self.control(artifact(self.sha, packages=False))
            control.run = run
            with self.subTest(run=run), self.assertRaises(ReleaseError):
                self.invoke(control)
            self.assertEqual(control.deletes, [])
        control = self.control(artifact(self.sha, packages=False))
        control.artifact_bytes = lambda _id: (_ for _ in ()).throw(ReleaseError("signed artifact download returned HTTP 404"))
        with self.assertRaises(ReleaseError):
            self.invoke(control)
        self.assertEqual(control.deletes, [])

    def test_unmarked_release_preserved_and_ambiguous_drafts_rejected(self):
        control = self.control()
        unrelated = {"id": 111, "draft": True, "author": {"login": "github-actions[bot]"}, "body": "unmarked"}
        control.additional.append(unrelated)
        self.assertEqual(self.invoke(control)["publish"], "true")
        self.assertEqual(control.additional, [unrelated])
        control.additional.append({**control.release, "id": 999})
        with self.assertRaisesRegex(ReleaseError, "multiple marked"):
            self.invoke(control)
        self.assertEqual(control.deletes, [])

    def test_packages_always_retain_when_original_bytes_are_unavailable_without_destination_reads(self):
        for reason in ("run404", "producer-mismatch", "metadata404", "archive410", "expired", "old-expiry"):
            for inventory in ("Fixture.Package", "Indexing.Package,Unlisted.Package", "mixed"):
                control = self.control()
                if reason == "run404":
                    self.gone(control)
                elif reason == "producer-mismatch":
                    control.run["path"] = ".github/workflows/other.yml"
                elif reason == "metadata404":
                    control.errors[f"/repos/{REPOSITORY}/actions/artifacts/{ARTIFACT}"] = 404
                elif reason == "archive410":
                    control.artifact_bytes = lambda _identity: (_ for _ in ()).throw(draft.GitHubHttpError("/zip", 410))
                else:
                    control.metadata["expired"] = reason == "expired"
                    if reason == "old-expiry":
                        control.metadata["expires_at"] = "2000-01-01T00:00:00Z"
                if inventory == "mixed":
                    control.release["body"] = control.release["body"].replace(
                        "hexalith-release-containers: \n", "hexalith-release-containers: product/image\n")
                else:
                    control.release["body"] = control.release["body"].replace("Fixture.Package\n", inventory + "\n")
                saved = copy.deepcopy(control.release)
                with (self.subTest(reason=reason, inventory=inventory),
                      patch.object(urllib.request.OpenerDirector, "open", side_effect=AssertionError("destination lookup ran")),
                      self.assertRaisesRegex(ReleaseError, "^draft v2.3.5 cannot be completed: its build artifact is gone$")):
                    self.invoke(control, registry=lambda *_args: self.fail("package-bearing draft queried an image"))
                self.assertEqual(control.deletes + control.writes, [])
                self.assertEqual(control.release, saved)
                self.assertEqual(self.emitted, [])
                self.assertEqual(control.release_reads, 1)
                self.assertEqual(git_command(self.root, "tag", "--list", "v" + VERSION), "")

    def test_container_only_checks_every_image_and_retains_any_present_unknown_or_missing_credentials(self):
        for state in ("all-absent", "image-present", "image-error", "credentials", "non-boolean"):
            control = self.control(artifact(self.sha, packages=False))
            self.gone(control)
            control.release["body"] = control.release["body"].replace(
                "hexalith-release-containers: \n", "hexalith-release-containers: product/image,product/other\n")
            calls = []
            class Registry:
                def __init__(_self, name, username, password):
                    calls.append(("image", name, username, password))
                    if state == "credentials":
                        raise ReleaseError("missing pull credentials")
                    _self.name = name
                def version_absent(_self, version):
                    self.assertEqual(version, VERSION)
                    if state == "image-error" and _self.name == "product/other":
                        raise ReleaseError("401")
                    if state == "non-boolean":
                        return "true"
                    return not (state == "image-present" and _self.name == "product/other")
            with self.subTest(state=state):
                if state == "all-absent":
                    result = self.invoke(control, registry=Registry, username="pull", password="secret")
                    self.assertEqual(result["publish"], "false")
                    self.assertEqual(control.deletes, [789])
                else:
                    with self.assertRaisesRegex(ReleaseError, "^draft v2.3.5 cannot be completed: its build artifact is gone$"):
                        self.invoke(control, registry=Registry, username="pull", password="secret")
                    self.assertEqual(control.deletes, [])
                    self.assertEqual(self.emitted, [])
            self.assertEqual(calls, [("image", "product/image", "pull", "secret"),
                                     ("image", "product/other", "pull", "secret")])

    def test_delete_race_and_non204_never_emit_success(self):
        for mutation in ({"body": "changed"}, {"draft": False}, {"target_commitish": "a" * 40},
                         {"author": {"login": "human"}}, {"id": 999}):
            control = self.control(artifact(self.sha, packages=False))
            self.gone(control)
            def race(value):
                if value.release_reads == 2:
                    value.release.update(mutation)
            control.before_release = race
            with self.subTest(mutation=mutation), self.assertRaises(ReleaseError):
                self.invoke(control)
            self.assertEqual(control.deletes, [])
            self.assertEqual(self.emitted, [])
        for status in (200, 202, 404, 403, 500):
            control = self.control(artifact(self.sha, packages=False))
            self.gone(control)
            control.delete_status = status
            with self.subTest(status=status), self.assertRaises(ReleaseError):
                self.invoke(control)
            self.assertEqual(control.deletes, [])
            self.assertEqual(self.emitted, [])


@contextmanager
def recovery_http(control, *, tls=None):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass
        def send_data(self, status, data):
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            if data:
                self.wfile.write(data)
        def do_GET(self):
            control.requests.append(("GET", self.path, self.headers.get("Authorization")))
            if self.path in control.errors:
                self.send_data(control.errors[self.path], json.dumps(
                    getattr(control, "error_documents", {}).get(self.path, {})).encode())
            elif control.deleted and self.path == f"/repos/{REPOSITORY}/releases/789":
                self.send_data(404, b"{}")
            elif self.path.endswith("/zip"):
                self.send_response(getattr(control, "archive_status", 302))
                self.send_header("Location", control.location)
                self.end_headers()
            elif self.path == "/archive":
                self.send_data(getattr(control, "signed_status", 200), control.payload)
            elif self.path.startswith(f"/repos/{REPOSITORY}/releases?"):
                self.send_data(200, json.dumps(list(control.pages(self.path))).encode())
            else:
                self.send_data(200, json.dumps(control.get(self.path)).encode())
        def do_DELETE(self):
            control.requests.append(("DELETE", self.path, self.headers.get("Authorization")))
            if control.delete_status == 204:
                control.delete_draft(int(self.path.rsplit("/", 1)[-1]))
            self.send_data(control.delete_status, b"")
        def do_PATCH(self):
            control.requests.append(("PATCH", self.path, self.headers.get("Authorization")))
            document = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            self.send_data(200, json.dumps(control.publish_draft(789, document)).encode())
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    if tls:
        server.socket = tls.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield ("https" if tls else "http") + f"://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


@contextmanager
def destination_http():
    requests, statuses = [], {}
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass
        def respond(self):
            requests.append((self.command, self.path, dict(self.headers)))
            status = statuses.get(self.path, 404)
            data = b""
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/stolen")
            self.end_headers()
            if self.command != "HEAD":
                self.wfile.write(data)
        do_GET = respond
        do_HEAD = respond
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    transport = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    class LocalOpener:
        def open(self, request, **kwargs):
            request = urllib.request.Request(request) if isinstance(request, str) else request
            if not request.full_url.startswith("https://registry.hexalith.com/v2/product/image/"):
                raise AssertionError("credential boundary escaped")
            replacement = urllib.request.Request(f"http://127.0.0.1:{server.server_port}" + request.selector,
                method=request.get_method(), headers=dict(request.headers))
            return transport.open(replacement, **kwargs)
    try:
        yield LocalOpener(), requests, statuses
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


class DestinationHttpTests(unittest.TestCase):
    def test_registry_timeout_never_proves_absence(self):
        registry = oci_publish.Registry("product/image", "u", "p")
        class Timeout:
            def open(self, *_args, **_kwargs):
                raise TimeoutError("timeout")
        registry.opener = Timeout()
        with self.assertRaises(ReleaseError):
            registry.version_absent(VERSION)

    def test_registry_broad_head_404_only_and_credentials_bound_to_registry(self):
        with destination_http() as (opener, requests, statuses):
            registry = oci_publish.Registry("product/image", "pull-user", "pull-password")
            registry.opener = opener
            path = "/v2/product/image/manifests/" + VERSION
            self.assertTrue(registry.version_absent(VERSION))
            statuses[path] = 200
            self.assertFalse(registry.version_absent(VERSION))
            for status in (201, 204, 302, 401, 403, 429, 500):
                statuses[path] = status
                with self.subTest(status=status), self.assertRaises(ReleaseError):
                    registry.version_absent(VERSION)
            for method, path, headers in requests:
                self.assertEqual(method, "HEAD")
                self.assertEqual(headers["Authorization"], registry.authorization)
                for media in ("application/vnd.oci.image.index.v1+json", "application/vnd.oci.image.manifest.v1+json",
                              "application/vnd.docker.distribution.manifest.v2+json", "application/vnd.docker.distribution.manifest.list.v2+json"):
                    self.assertIn(media, headers["Accept"])
                self.assertNotEqual(path, "/stolen")


class RecoveryHttpTests(History):
    control = CompleteTests.control
    invoke = CompleteTests.invoke
    gone = CompleteTests.gone

    def test_package_and_mixed_inventory_refuse_even_with_absent_destinations_and_old_expiry(self):
        for mixed in (False, True):
            control = self.control()
            control.metadata.update(expired=False, expires_at="2000-01-01T00:00:00Z")
            if mixed:
                control.release["body"] = control.release["body"].replace(
                    "hexalith-release-containers: \n", "hexalith-release-containers: product/image\n")
            with (self.subTest(mixed=mixed), recovery_http(control) as base,
                  destination_http() as (opener, destination_requests, _statuses)):
                api = draft.DraftApi(REPOSITORY, "github-private", base)
                with (patch.object(urllib.request, "build_opener", return_value=opener),
                      self.assertRaisesRegex(ReleaseError, "^draft v2.3.5 cannot be completed: its build artifact is gone$")):
                    self.invoke(api, username="pull-user", password="pull-password")
                self.assertEqual(destination_requests, [])
                self.assertEqual(api.get(f"/repos/{REPOSITORY}/releases/789"), control.release)
            self.assertEqual(control.deletes + control.writes, [])
            self.assertEqual(self.emitted, [])
            self.assertFalse(any(method == "DELETE" for method, _path, _auth in control.requests))

    def test_main_container_only_uses_pull_pair_and_only_404_permits_deletion(self):
        for status in (404, 200, 202, 204, 301, 302, 401, 403, 410, 429, 500, "missing-user", "missing-password"):
            control = self.control(artifact(self.sha, packages=False))
            self.gone(control)
            control.release["body"] = control.release["body"].replace(
                "hexalith-release-containers: \n", "hexalith-release-containers: product/image\n")
            output = self.root / "main-output"
            output.write_text("")
            environment = {"GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF": "refs/heads/main",
                "GITHUB_REPOSITORY": REPOSITORY, "GITHUB_TOKEN": "github-private", "GITHUB_WORKFLOW_REF": WORKFLOW_REF,
                "RELEASE_SHA": self.sha, "RELEASE_VERSION": VERSION, "RELEASE_DRAFT_ID": "789", "RELEASE_LAST_TAG": "v2.3.4",
                "REGISTRY_PULL_USERNAME": "pull-user", "REGISTRY_PULL_PASSWORD": "pull-password", "GITHUB_OUTPUT": str(output)}
            if status == "missing-user":
                environment.pop("REGISTRY_PULL_USERNAME")
            elif status == "missing-password":
                environment.pop("REGISTRY_PULL_PASSWORD")
            errors = io.StringIO()
            with (self.subTest(status=status), destination_http() as (opener, requests, statuses),
                  patch.dict(os.environ, environment, clear=True), patch.object(sys, "argv", [complete.__file__]),
                  patch.object(complete, "DraftApi", return_value=control),
                  patch.object(complete.Git, "fetch", return_value=self.git),
                  patch.object(urllib.request, "build_opener", return_value=opener), redirect_stderr(errors)):
                if isinstance(status, int):
                    statuses["/v2/product/image/manifests/" + VERSION] = status
                self.assertEqual(complete.main(), 0 if status == 404 else 1)
                if isinstance(status, str):
                    self.assertEqual(requests, [])
                else:
                    self.assertEqual(len(requests), 1)
                    method, path, headers = requests[0]
                    self.assertEqual((method, path), ("HEAD", "/v2/product/image/manifests/" + VERSION))
                    self.assertEqual(headers["Authorization"], "Basic " + base64.b64encode(b"pull-user:pull-password").decode())
                    self.assertIn("application/vnd.docker.distribution.manifest.list.v2+json", headers["Accept"])
            if status == 404:
                self.assertEqual(control.deletes, [789])
                self.assertIn("publish=false\n", output.read_text())
                self.assertIn("noop-reason=stale-draft-deleted\n", output.read_text())
                self.assertEqual(errors.getvalue(), "")
            else:
                self.assertEqual(control.deletes + control.writes, [])
                self.assertEqual(output.read_text(), "")
                self.assertEqual(errors.getvalue().strip(), "draft v2.3.5 cannot be completed: its build artifact is gone")

    def test_executed_http_disappearance_and_delete_require_exact_status(self):
        for reason in ("run404", "artifact404", "archive410", "expired"):
            control = self.control(artifact(self.sha, packages=False))
            control.location = "https://storage.invalid/archive"
            if reason == "run404":
                self.gone(control)
            elif reason == "artifact404":
                control.errors[f"/repos/{REPOSITORY}/actions/artifacts/{ARTIFACT}"] = 404
            elif reason == "archive410":
                control.archive_status = 410
            else:
                control.metadata["expired"] = True
            with self.subTest(reason=reason), recovery_http(control) as base:
                api = draft.DraftApi(REPOSITORY, "github-private", base)
                result = self.invoke(api)
                self.assertEqual(list(api.pages(f"/repos/{REPOSITORY}/releases?per_page=100")), [])
                with self.assertRaises(draft.GitHubHttpError):
                    api.get(f"/repos/{REPOSITORY}/releases/789")
            self.assertEqual(result["noop-reason"], "stale-draft-deleted")
            self.assertEqual(control.deletes, [789])
            self.assertEqual([path for method, path, _ in control.requests if method == "DELETE"], [f"/repos/{REPOSITORY}/releases/789"])
            self.assertTrue(all(auth == "Bearer github-private" for _, _, auth in control.requests))
        for status in (200, 202, 403, 404, 500):
            control = self.control(artifact(self.sha, packages=False))
            self.gone(control)
            control.delete_status, control.location = status, "https://storage.invalid/archive"
            with self.subTest(status=status), recovery_http(control) as base:
                with self.assertRaises(ReleaseError):
                    self.invoke(draft.DraftApi(REPOSITORY, "github-private", base))
            self.assertEqual(self.emitted, [])


class CompleteCompositeTests(History):
    control = CompleteTests.control
    gone = CompleteTests.gone

    def setUp(self):
        super().setUp()
        self.work = self.root / "adapter"
        self.work.mkdir()
        certificate, key = self.work / "cert.pem", self.work / "key.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", str(key),
            "-out", str(certificate), "-days", "1", "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1"],
            check=True, capture_output=True)
        self.tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.tls.load_cert_chain(certificate, key)
        self.binaries = self.work / "bin"
        self.binaries.mkdir()
        self.ledger = self.work / "git-commands.jsonl"
        (self.binaries / "git").write_text(f'''#!{sys.executable}
import json, os, subprocess, sys
args = sys.argv[1:]
assert not any(key in os.environ for key in ('GITHUB_TOKEN', 'GH_TOKEN', 'GITHUB_SHA'))
assert not any(value in args for value in ('checkout', 'worktree', 'submodule', 'show', 'blob'))
with open({str(self.ledger)!r}, 'a') as stream:
    stream.write(json.dumps(args) + '\\n')
if 'fetch' in args:
    args = [os.environ['TEST_REMOTE'] if value.startswith('https://github.com/') else value for value in args]
    args = [value.replace('protocol.file.allow=never', 'protocol.file.allow=always') for value in args]
raise SystemExit(subprocess.call([{shutil.which('git')!r}, *args]))
''')
        for name in ("dotnet", "npm", "uv", "pip", "docker"):
            (self.binaries / name).write_text("#!/bin/sh\necho forbidden-tool >&2\nexit 99\n")
        for path in self.binaries.iterdir():
            path.chmod(0o755)
        inherited = {name: value for name, value in os.environ.items() if not name.lower().endswith("_proxy")}
        self.environment = {**inherited, "PATH": str(self.binaries) + os.pathsep + os.environ["PATH"],
            "GITHUB_TOKEN": "private-github-marker", "GITHUB_SHA": self.sha, "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_REF": "refs/heads/main", "GITHUB_REPOSITORY": REPOSITORY, "GITHUB_WORKFLOW_REF": WORKFLOW_REF,
            "GITHUB_RUN_ID": "99999", "GITHUB_WORKSPACE": str(self.work), "GITHUB_OUTPUT": str(self.work / "github-output"),
            "TEST_REMOTE": str(self.root),
            "HTTPS_PROXY": "http://127.0.0.1:9", "HTTP_PROXY": "http://127.0.0.1:9", "ALL_PROXY": "http://127.0.0.1:9",
            "NO_PROXY": "127.0.0.1,localhost",
            "SSL_CERT_FILE": str(certificate), "REGISTRY_PULL_USERNAME": "pull-reader", "REGISTRY_PULL_PASSWORD": "pull-secret"}
        for name in list(self.environment):
            if name.startswith("RELEASE_") or name in {"REGISTRY_USERNAME", "REGISTRY_PASSWORD", "NUGET_API_KEY"}:
                self.environment.pop(name)
        self.action = Path(__file__).resolve().parents[2] / "release-draft"
        (self.work / "github-output").write_text("")

    def execute(self, control, inputs=None):
        values = {"mode": "complete", "draft-id": "789", "last-tag": "v2.3.4", "sha": self.sha, "version": VERSION}
        values.update(inputs or {})
        with recovery_http(control, tls=self.tls) as signed, recovery_http(control) as base:
            control.location = signed + "/archive"
            environment = {**self.environment, "GITHUB_API_URL": base}
            result, processes = run_composite(self.action, values, environment, cwd=self.work, runner_temp=self.work)
        text = "".join(process.stdout + process.stderr for process in processes)
        self.assertNotIn("forbidden-tool", text)
        self.assertNotIn("private-github-marker", text)
        self.assertNotIn("pull-secret", text)
        return result, processes

    def test_actual_complete_adapter_flows_through_actual_graph_and_reporter(self):
        from test_release_outcome import ReleaseWorkflowGraphTests
        (self.root / ".github/workflows/new-dispatch.yml").write_text("advanced caller workflows\n")
        (self.root / ".github/hexalith-ci.json").write_text("poison config must never be loaded by recovery\n")
        self.environment["GITHUB_SHA"] = self.commit("ci: advance dispatch source")
        for scenario in ("available", "gone", "package-gone", "mixed-gone", "package-old-expiry",
                         "corrupt", "signed-gone", "signed-partial", "metadata-accepted", "run-accepted"):
            graph_runner = ReleaseWorkflowGraphTests(methodName="runTest")
            graph_runner.setUp()
            self.addCleanup(graph_runner.doCleanups)
            control = self.control(None if scenario in ("available", "package-gone", "mixed-gone", "package-old-expiry")
                                   else artifact(self.sha, packages=False))
            if scenario in ("gone", "package-gone", "mixed-gone"):
                self.gone(control)
                if scenario == "mixed-gone":
                    control.release["body"] = control.release["body"].replace(
                        "hexalith-release-containers: \n", "hexalith-release-containers: product/image\n")
            elif scenario == "package-old-expiry":
                control.metadata.update(expired=False, expires_at="2000-01-01T00:00:00Z")
            elif scenario == "corrupt":
                control.metadata["digest"] = "sha256:" + "0" * 64
            elif scenario == "signed-gone":
                control.signed_status = 404
            elif scenario == "signed-partial":
                control.signed_status = 206
            elif scenario in ("metadata-accepted", "run-accepted"):
                path = f"/repos/{REPOSITORY}/actions/" + (
                    f"artifacts/{ARTIFACT}" if scenario == "metadata-accepted" else f"runs/{RUN}")
                control.errors[path] = 202
                control.error_documents = {path: control.metadata if scenario == "metadata-accepted" else control.run}
            def stage(inputs):
                result, processes = self.execute(control, inputs)
                return ("success", result) if result is not None else ("failure", {})
            with self.subTest(scenario=scenario):
                graph = graph_runner.run_graph(resolve={"mode": "complete", "sha": self.sha, "version": VERSION,
                    "draft-id": "789", "last-tag": "v2.3.4"}, stages={"draft": stage})
                self.assertEqual(set(graph["executed"]), {"draft"})
                self.assertEqual([graph["needs"][name]["result"] for name in ("version", "build")], ["skipped", "skipped"])
                self.assertEqual(graph["outputs"]["sha"], self.sha)
                if scenario == "available":
                    self.assertEqual(graph["outputs"]["publish"], "true")
                    self.assertEqual(graph["outputs"]["artifact-run"], str(RUN))
                    self.assertEqual(graph["outputs"]["artifact-id"], str(ARTIFACT))
                    self.assertEqual(graph["outputs"]["artifact-digest"], digest(control.payload))
                    self.assertFalse(graph["uploaded"])
                    self.assertEqual(control.deletes, [])
                else:
                    self.assertTrue(graph["uploaded"])
                    self.assertEqual(graph_runner.document()["outcome"], "nothing" if scenario == "gone" else "failed")
                    self.assertEqual(graph["outputs"]["publish"], "false")
                    self.assertEqual(control.deletes, [789] if scenario == "gone" else [])

    def test_actual_adapter_rejects_non_dispatch_non_main_and_invalid_mode_before_any_api_read(self):
        for event, ref, mode in (("push", "refs/heads/main", "complete"),
                                 ("workflow_dispatch", "refs/heads/topic", "complete"),
                                 ("workflow_dispatch", "refs/heads/main", "other")):
            self.environment.update(GITHUB_EVENT_NAME=event, GITHUB_REF=ref)
            control = self.control(artifact(self.sha, packages=False))
            self.gone(control)
            with self.subTest(event=event, ref=ref, mode=mode):
                result, processes = self.execute(control, {"mode": mode})
                self.assertIsNone(result)
                self.assertTrue(any(process.returncode for process in processes))
                self.assertEqual(control.requests, [])
                self.assertEqual(control.deletes + control.writes, [])
                self.assertEqual((self.work / "github-output").read_text(), "")

    def test_original_recovery_handoff_retries_actual_publisher_with_identical_package_bytes(self):
        control = self.control()
        package_bytes = dict(draft_files(control.payload))["packages/Fixture.Package." + VERSION + ".nupkg"]
        observed = self.work / "package-digests"
        marker = self.work / "retry-ready"
        (self.binaries / "dotnet").write_text(f'''#!{sys.executable}
import hashlib, pathlib, sys
path = pathlib.Path(sys.argv[3])
assert '--skip-duplicate' in sys.argv
assert path.read_bytes() == bytes.fromhex({package_bytes.hex()!r})
with pathlib.Path({str(observed)!r}).open('a') as stream:
    stream.write(hashlib.sha256(path.read_bytes()).hexdigest() + '\\n')
if not pathlib.Path({str(marker)!r}).exists():
    pathlib.Path({str(marker)!r}).touch()
    raise SystemExit(3)
print('already exists')
''')
        published_action = Path(__file__).resolve().parents[2] / "publish"
        outcomes = []
        def external(reference, arguments):
            if reference.startswith("NuGet/login@"):
                return {"NUGET_API_KEY": "temporary-key-marker"}
            self.assertTrue(reference.startswith("actions/upload-artifact@"))
            path = Path(arguments["path"])
            outcomes.append(json.loads(path.read_text()))
            shutil.rmtree(path.parent)
            return {}
        for attempt in range(2):
            with self.subTest(attempt=attempt):
                handoff, processes = self.execute(control)
                self.assertEqual(handoff, {**control.values, "noop-reason": ""})
                with recovery_http(control, tls=self.tls) as signed, recovery_http(control) as base:
                    control.location = signed + "/archive"
                    result, publisher_processes = run_composite(published_action, {
                        **{key: handoff[key] for key in control.values}, "nuget-user": "fixture",
                        "registry-username": "push-user", "registry-password": "push-secret"},
                        {**self.environment, "GITHUB_API_URL": base}, cwd=self.work, runner_temp=self.work, external=external)
                self.assertEqual(outcomes[-1]["outcome"], "failed" if attempt == 0 else "published")
                self.assertEqual(control.release["draft"], attempt == 0)
                self.assertEqual(len(control.writes), attempt)
                self.assertNotIn("temporary-key-marker", "".join(p.stdout + p.stderr for p in publisher_processes))
        self.assertEqual(len(outcomes), 2)
        self.assertEqual(observed.read_text().splitlines(), [digest(package_bytes)[7:]] * 2)
        self.assertEqual(git_command(self.root, "rev-parse", "v" + VERSION + "^{commit}"), self.sha)
        self.assertEqual(control.deletes, [])
        self.assertTrue(all(auth is None for _, path, auth in control.requests if path == "/archive"))


if __name__ == "__main__":
    unittest.main()
