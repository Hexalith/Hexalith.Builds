"""Publication effects through disposable Git histories, HTTP and actual composites."""
from __future__ import annotations

import copy
import hashlib
from contextlib import contextmanager, redirect_stdout, redirect_stderr
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
import urllib.error
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_publish as publish
import release_draft as draft
import oci_archive as oci
import test_release_oci as oci_fixture
from release_state import Git, ReleaseError
from release_outcome import validate_outcome
from test_release_draft import ARTIFACT, RUN, VERSION, REPOSITORY, DraftApiFixture, History, artifact, digest, git_command, package, zip_bytes
from actions_expressions import run_composite


class PublishFixture(DraftApiFixture):
    def __init__(self, sha, payload, git):
        super().__init__(sha, payload)
        self.git = git
        self.run = {"id": RUN, "path": ".github/workflows/release.yml", "event": "workflow_dispatch",
                    "head_branch": "main", "head_sha": sha}
        self.values = {"publish": "true", "sha": sha, "version": VERSION, "last-tag": "v2.3.4",
            "draft-id": "789", "artifact-id": str(ARTIFACT), "artifact-run": str(RUN), "artifact-digest": digest(payload)}
        inventory = draft.artifact_inventory(payload, sha=sha, version=VERSION, digest=digest(payload), repository=REPOSITORY)
        self.release = {"id": 789, "tag_name": "v" + VERSION, "target_commitish": sha,
            "author": {"login": "github-actions[bot]"}, "draft": True, "prerelease": False,
            "body": draft.release_body("Original notes", sha=sha, run_id=RUN, artifact_id=ARTIFACT,
                                       digest=digest(payload), inventory=inventory)}
        self.errors, self.requests = {}, []
        self.assets, self.asset_data, self.asset_uploads = [], {}, []
        self.after_patch = lambda value: value

    def pages(self, path, deadline=None):
        self.events.append("LIST")
        yield from copy.deepcopy(self.assets if "/assets?" in path else self.drafts)

    def upload_publication(self, identity, data):
        self.events.append("asset-upload")
        self.asset_uploads.append(data)
        asset = {"id": 990, "name": publish.publication.ASSET_NAME, "state": "uploaded",
                 "size": len(data), "digest": "sha256:" + hashlib.sha256(data).hexdigest()}
        self.assets.append(asset)
        self.asset_data[990] = data
        return copy.deepcopy(asset)

    def publication_bytes(self, identity):
        self.events.append("asset-read")
        return self.asset_data[identity]

    def get(self, path, deadline=None):
        self.events.append(path)
        if path in self.errors:
            raise ReleaseError("GitHub API returned HTTP " + str(self.errors[path]))
        if "/actions/artifacts/" in path:
            return self.metadata
        if "/actions/runs/" in path:
            return self.run
        if "/releases/" in path:
            return copy.deepcopy(self.release)
        if "/matching-refs/" in path:
            tag = "refs/tags/" + path.rsplit("/", 1)[-1]
            result = self.git.run("show-ref", "--verify", "--hash", tag, allowed=(0, 128))
            if result.returncode:
                return []
            sha = result.stdout.decode().strip()
            kind = self.git.run("cat-file", "-t", sha).stdout.decode().strip()
            return [{"ref": tag, "object": {"type": kind, "sha": sha}}]
        if "/git/tags/" in path:
            sha = path.rsplit("/", 1)[-1]
            data = self.git.run("cat-file", "tag", sha).stdout.decode().splitlines()
            return {"sha": sha, "object": {"sha": data[0].split()[1], "type": data[1].split()[1]}}
        raise AssertionError(path)

    def publish_draft(self, identity, document):
        self.events.append("PATCH")
        self.writes.append(copy.deepcopy(document))
        self.release.update(document)
        if not git_command(self.git.directory, "tag", "--list", "v" + VERSION):
            git_command(self.git.directory, "tag", "v" + VERSION, document["target_commitish"])
        return self.after_patch(copy.deepcopy(self.release))


@contextmanager
def api_http(control, *, tls=None):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass
        def respond(self, status, value):
            data = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        def do_GET(self):
            control.requests.append(("GET", self.path, self.headers.get("Authorization")))
            if self.path in control.errors:
                self.respond(control.errors[self.path], {})
            elif "/releases/assets/" in self.path and getattr(control, "publication_location", None):
                self.send_response(302)
                self.send_header("Location", control.publication_location)
                self.end_headers()
            elif self.path == "/publication-download":
                if getattr(control, "publication_download_location", None):
                    self.send_response(302)
                    self.send_header("Location", control.publication_download_location)
                    self.end_headers()
                    return
                if getattr(control, "publication_download_status", 200) != 200:
                    self.respond(control.publication_download_status, {})
                    return
                data = getattr(control, "publication_download_bytes", control.publication_bytes(990))
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            elif "/releases/assets/" in self.path:
                data = control.publication_bytes(int(self.path.rsplit("/", 1)[-1]))
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            elif "/assets?" in self.path:
                self.respond(200, list(control.pages(self.path)))
            elif self.path.endswith("/zip"):
                self.send_response(302)
                self.send_header("Location", control.location)
                self.end_headers()
            elif self.path == "/archive":
                self.send_response(200)
                self.send_header("Content-Length", str(len(control.payload)))
                self.end_headers()
                self.wfile.write(control.payload)
            elif self.path.startswith("/nuget/"):
                if getattr(control, "nuget_status", 200) != 200:
                    self.respond(control.nuget_status, {})
                    return
                data = getattr(control, "nuget_payload", package(control.values["sha"]))
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            else:
                self.respond(200, control.get(self.path))
        def do_POST(self):
            control.requests.append(("POST", self.path, self.headers.get("Authorization")))
            self.respond(201, control.upload_publication(789, self.rfile.read(int(self.headers["Content-Length"])) ))
        def do_PATCH(self):
            control.requests.append(("PATCH", self.path, self.headers.get("Authorization")))
            if getattr(control, "patch_status", 200) != 200:
                self.respond(control.patch_status, {})
                return
            if self.path in control.errors:
                self.respond(control.errors[self.path], {})
                return
            self.respond(200, control.publish_draft(789, json.loads(self.rfile.read(int(self.headers["Content-Length"])))))
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


class PublisherTests(History):
    def control(self, payload=None):
        return PublishFixture(self.sha, payload or self.payload, self.git)

    def authenticate(self, control, **kwargs):
        work = self.root / "payload"
        work.mkdir(exist_ok=True)
        values = publish.authenticate(self.git, control, copy.deepcopy(control.values), work,
                                      REPOSITORY + "/.github/workflows/release.yml@refs/heads/main", **kwargs)
        packages = [data for name, data in draft_files(control.payload) if name.startswith("packages/")]
        def feed(url, **_kwargs):
            identity = next(name for name in values["inventory"]["packages"] if "/" + name.lower() + "/" in url)
            for data in packages:
                try:
                    draft.package_identity(data, package_id=identity, version=VERSION, sha=self.sha)
                except ReleaseError:
                    continue
                response = io.BytesIO(data)
                response.status, response.headers = 200, {}
                return response
            self.fail("missing fixture package")
        publish.verify_packages(values, open_url=feed, attempts=1)
        return values

    def refresh(self, git, repository, sha):
        actual = git.run
        def run(*args, **kwargs):
            return None if args[0] == "fetch" else actual(*args, **kwargs)
        with patch.object(git, "run", side_effect=run):
            return draft.workflows_match(git, repository, sha)

    def test_both_tag_forms_conflict_and_noncommit_ref_fail_before_materialization(self):
        for annotated in (False, True):
            for target in (self.base, git_command(self.root, "rev-parse", self.sha + "^{tree}")):
                with self.subTest(annotated=annotated, target=target):
                    args = ("-a", "-m", "fixture") if annotated else ()
                    git_command(self.root, "tag", *args, "v" + VERSION, target)
                    control = self.control()
                    with self.assertRaisesRegex(ReleaseError, "^tag v2.3.5 does not point at the built commit$"):
                        self.authenticate(control)
                    self.assertEqual(control.writes, [])
                    self.assertEqual(list((self.root / "payload").glob("packages/*")), [])
                    git_command(self.root, "tag", "-d", "v" + VERSION)

    def test_absent_and_correct_lightweight_and_annotated_tags_publish_tag_last(self):
        for form in ("absent", "lightweight", "annotated"):
            with self.subTest(form=form):
                if form != "absent":
                    git_command(self.root, "tag", *(("-a", "-m", "fixture") if form == "annotated" else ()),
                                "v" + VERSION, self.sha)
                control = self.control()
                values = self.authenticate(control)
                outcome = publish.finalize_release(self.git, control, values, refresh=self.refresh)
                self.assertEqual(outcome["outcome"], "published")
                self.assertEqual(control.writes, [{"tag_name": "v" + VERSION, "target_commitish": self.sha, "draft": False}])
                self.assertEqual(git_command(self.root, "rev-parse", "v" + VERSION + "^{commit}"), self.sha)
                self.assertGreater(control.events.index("PATCH"), control.events.index("download"))
                git_command(self.root, "tag", "-d", "v" + VERSION)

    def test_source_uses_captured_base_without_recomputing_newer_reachable_tag(self):
        git_command(self.root, "tag", "v2.3.99", self.sha)
        control = self.control()
        values = self.authenticate(control)
        self.assertEqual(values["last-tag"], "v2.3.4")
        self.assertEqual(publish.finalize_release(self.git, control, values, refresh=self.refresh)["outcome"], "published")

    def test_producer_draft_artifact_errors_have_no_writes(self):
        for key, value in (("path", ".github/workflows/other.yml"), ("event", "push"), ("head_branch", "other"),
                           ("head_sha", "a" * 40), ("id", RUN + 1)):
            control = self.control()
            control.run[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ReleaseError, "producer"):
                self.authenticate(control)
            self.assertEqual(control.writes, [])
        for key, value in (("draft", False), ("author", {"login": "human"}), ("target_commitish", "b" * 40),
                           ("body", "hexalith-release: forged"), ("id", 790)):
            control = self.control()
            control.release[key] = value
            with self.subTest(key=key), self.assertRaises(ReleaseError):
                self.authenticate(control)
            self.assertEqual(control.writes, [])
        for mutate in (lambda c: c.metadata.update(digest="sha256:" + "0" * 64),
                       lambda c: c.metadata.update(expired=True), lambda c: setattr(c, "payload", c.payload + b"tamper")):
            control = self.control()
            mutate(control)
            with self.assertRaises(ReleaseError):
                self.authenticate(control)
            self.assertEqual(control.writes, [])
        control = self.control()
        control.errors[f"/repos/{REPOSITORY}/git/matching-refs/tags/v{VERSION}"] = 403
        with self.assertRaisesRegex(ReleaseError, "403"):
            self.authenticate(control)

    def test_unsafe_archive_fails_without_any_materialized_payload(self):
        for payload in (self.payload[:12], artifact(self.sha, extra=(("../poison", b"x"),)),
                        artifact(self.sha, nuspec=b"not a package")):
            control = self.control()
            control.payload = payload
            control.metadata["digest"] = control.values["artifact-digest"] = digest(payload)
            with self.assertRaises(ReleaseError):
                self.authenticate(control)
            self.assertEqual(list((self.root / "payload").glob("packages/*")), [])

    def test_main_advancement_workflow_difference_and_postpatch_race(self):
        control = self.control()
        values = self.authenticate(control)
        (self.root / "product").write_text("new product")
        self.commit("fix: advance product")
        self.assertEqual(publish.finalize_release(self.git, control, values, refresh=self.refresh)["outcome"], "published")
        git_command(self.root, "tag", "-d", "v" + VERSION)
        control = self.control()
        values = self.authenticate(control)
        (self.root / ".github/workflows/new.yml").write_text("changed workflows")
        self.commit("ci: change workflows")
        outcome = publish.finalize_release(self.git, control, values, refresh=self.refresh)
        self.assertEqual(outcome, {"schema": "hexalith.release-outcome.v1", "sha": self.sha,
            "version": VERSION, "outcome": "ready", "draftId": 789, "lastTag": "v2.3.4"})
        self.assertEqual(control.writes, [])
        self.assertTrue(control.release["draft"])
        control = self.control()
        values = self.authenticate(control)
        def wrong_tag(response):
            git_command(self.root, "tag", "-f", "v" + VERSION, self.base)
            return response
        control.after_patch = wrong_tag
        with self.assertRaisesRegex(ReleaseError, "does not point at the built commit"):
            publish.finalize_release(self.git, control, values, refresh=lambda *args: True)
        self.assertEqual(len(control.writes), 1)

    def test_draft_and_tag_revalidated_before_patch_and_returned_identity_verified(self):
        for mutate in (lambda c: c.release.update(body=c.release["body"] + "changed"),
                       lambda c: git_command(self.root, "tag", "v" + VERSION, self.base)):
            control = self.control()
            values = self.authenticate(control)
            mutate(control)
            with self.assertRaises(ReleaseError):
                publish.finalize_release(self.git, control, values, refresh=self.refresh)
            self.assertEqual(control.writes, [])
        git_command(self.root, "tag", "-d", "v" + VERSION)
        for change in ({"draft": True}, {"id": 999}, {"target_commitish": self.base}):
            control = self.control()
            values = self.authenticate(control)
            control.after_patch = lambda response: {**response, **change}
            with self.assertRaises(ReleaseError):
                publish.finalize_release(self.git, control, values, refresh=self.refresh)
            git_command(self.root, "tag", "-d", "v" + VERSION)

    def images(self):
        manifest = {"sha": self.sha, "version": VERSION,
                    "packages": [{"id": "Fixture.Package", "path": "packages/Fixture.nupkg"}], "containers": []}
        from test_release_draft import package
        files = [("packages/Fixture.nupkg", package(self.sha))]
        with patch.object(oci_fixture, "SHA", self.sha), patch.object(oci_fixture, "REPOSITORY", REPOSITORY):
            for index in (1, 2):
                sources = []
                for arch in ("amd64", "arm64"):
                    path = self.root / (arch + ".tar")
                    oci_fixture.sdk_archive(path, arch, version=VERSION)
                    sources.append((path, {"os": "linux", "architecture": arch}))
                combined = self.root / f"container{index}.tar"
                oci.combine_archives(sources, combined, revision=self.sha, version=VERSION,
                                     repository=REPOSITORY, name=f"product/image{index}")
                manifest["containers"].append({"name": f"product/image{index}", "path": f"containers/{index}.tar"})
                files.append((f"containers/{index}.tar", combined.read_bytes()))
        return zip_bytes([("release-manifest.json", json.dumps(manifest)), *files])

    def test_all_images_precede_first_package_and_late_conflict_stops_copy(self):
        events = []
        class Registry:
            conflict = ""
            def __init__(inner, name, *_args):
                inner.name = name
            def preflight(inner, *_args):
                events.append("preflight:" + inner.name)
                if inner.name == Registry.conflict:
                    raise ReleaseError("destination version conflicts")
            def verify(inner, *_args):
                events.append("verify:" + inner.name)
        control = self.control(self.images())
        Registry.conflict = "product/image2"
        with self.assertRaisesRegex(ReleaseError, "conflicts"):
            self.authenticate(control, registry=Registry)
        self.assertEqual(events, ["preflight:product/image1", "preflight:product/image2"])
        events.clear()
        Registry.conflict = ""
        values = self.authenticate(control, registry=Registry)
        def dotnet(arguments, **kwargs):
            events.append("nuget")
            self.assertTrue(Path(arguments[3]).is_absolute())
            self.assertEqual(list(kwargs["cwd"].iterdir()), [])
            self.assertEqual(Path(arguments[3]).read_bytes(), dict(draft_files(control.payload))["packages/Fixture.nupkg"])
            return subprocess.CompletedProcess(arguments, 0)
        publish.push_packages(self.root / "payload", values, "temporary-key", run=dotnet)
        self.assertEqual(events[:3], ["preflight:product/image1", "preflight:product/image2", "nuget"])
        Registry.conflict = "product/image2"
        from oci_publish import publish as push
        copies = []
        for item in values["containers"]:
            try:
                push(self.root / "payload" / item["path"], item["name"], VERSION, item["graph"], "u", "p",
                     registry=Registry(item["name"]), copy=lambda *args: copies.append(args))
            except ReleaseError:
                break
        self.assertEqual(len(copies), 1)
        self.assertEqual(control.writes, [])

    def test_empty_payload_no_package_command_and_partial_package_retry_same_bytes(self):
        control = self.control(artifact(self.sha, packages=False))
        values = self.authenticate(control)
        publish.push_packages(self.root / "payload", values, "", run=lambda *args, **kwargs: self.fail("SDK invoked"))
        self.assertEqual(publish.finalize_release(self.git, control, values, refresh=self.refresh)["outcome"], "published")
        git_command(self.root, "tag", "-d", "v" + VERSION)
        control = self.control()
        values = self.authenticate(control)
        observed = []
        def run(arguments, **kwargs):
            observed.append(Path(arguments[3]).read_bytes())
            self.assertIn("--skip-duplicate", arguments)
            return subprocess.CompletedProcess(arguments, 1 if len(observed) == 1 else 0)
        with self.assertRaisesRegex(ReleaseError, "NuGet publication failed"):
            publish.push_packages(self.root / "payload", values, "key", run=run)
        publish.push_packages(self.root / "payload", values, "key", run=run)
        self.assertEqual(observed[0], observed[1])

    def test_nuget_status_reports_duplicate_without_exposing_tool_output(self):
        control = self.control()
        values = self.authenticate(control)
        for output, expected in ((b"Your package was pushed. private-key", "NuGet package version published"),
                                 (b"private-key already exists at feed", "NuGet package version already exists; skipped duplicate"),
                                 (b"private-key", "NuGet package push completed")):
            recorded = io.StringIO()
            with redirect_stdout(recorded):
                publish.push_packages(self.root / "payload", values, "private-key", run=lambda args, **kwargs:
                                      subprocess.CompletedProcess(args, 0, stdout=output))
            self.assertEqual(recorded.getvalue(), expected + "\n")

    def test_enrolled_artifact_policy_survives_real_authentication_state_and_publication(self):
        import package_archive
        manifest = {"sha": self.sha, "version": VERSION, "packages": [{"id": "Fixture.Package", "path": "packages/Fixture.Package." + VERSION + ".nupkg", "verifyPublishedPackage": True}], "containers": []}
        payload = artifact(self.sha, manifest=manifest)
        original_verify, original_push, original_finalize = publish.verify_packages, publish.push_packages, publish.finalize_release
        for mutation in ("signed", "changed", "unsigned"):
            with self.subTest(mutation=mutation):
                control = self.control(payload)
                directory = self.root / ("enrolled-" + mutation)
                directory.mkdir()
                public = package_archive.members(package(self.sha))
                if mutation != "unsigned":
                    public[".signature.p7s"] = b"controlled signature subprocess"
                if mutation == "changed":
                    public["lib/net11.0/Fixture.dll"] = b"changed public bytes"
                response = io.BytesIO(zip_bytes(list(public.items())))
                response.status, response.headers = 200, {}
                environment = {"RELEASE_" + key.upper().replace("-", "_"): value for key, value in control.values.items()}
                environment.update(GITHUB_EVENT_NAME="workflow_dispatch", GITHUB_REF="refs/heads/main", GITHUB_SHA=self.sha,
                    GITHUB_REPOSITORY=REPOSITORY, GITHUB_WORKFLOW_REF=REPOSITORY + "/.github/workflows/release.yml@refs/heads/main",
                    RELEASE_STATE=str(directory), NUGET_API_KEY="synthetic-publication-control")
                def signature(arguments, **kwargs):
                    self.assertFalse(any("TOKEN" in key or "KEY" in key for key in kwargs["env"]))
                    return subprocess.CompletedProcess(arguments, 0, stdout=b"")
                def verify(values):
                    self.assertIn("Fixture.Package", values["package-policies"])
                    return original_verify(values, open_url=lambda *_a, **_k: response, run=signature, attempts=1, interval=0)
                with patch.dict(os.environ, environment), patch.object(publish, "private_directory", return_value=directory), \
                     patch.object(publish, "PublishApi", return_value=control), patch.object(publish, "Git", return_value=self.git) as git_type, \
                     patch.object(publish, "outputs"), patch.object(publish, "verify_packages", side_effect=verify) as verification, \
                     patch.object(publish, "push_packages", side_effect=lambda directory, values, key: original_push(directory, values, key, run=lambda args, **kwargs: subprocess.CompletedProcess(args, 0, stdout=b""))), \
                     patch.object(publish, "finalize_release", side_effect=lambda git, api, values: original_finalize(git, api, values, refresh=self.refresh)) as finalization:
                    git_type.fetch.return_value = self.git
                    with patch.object(sys, "argv", ["publisher", "prepare"]):
                        self.assertEqual(0, publish.main())
                    state = json.loads((directory / "state.json").read_text())
                    self.assertEqual(package_archive.body_inventory(package(self.sha)), state["values"]["package-policies"]["Fixture.Package"])
                    with patch.object(sys, "argv", ["publisher", "publish"]), redirect_stderr(io.StringIO()):
                        self.assertEqual(0 if mutation == "signed" else 1, publish.main())
                    verification.assert_called_once()
                    if mutation == "signed":
                        finalization.assert_called_once()
                        self.assertEqual("published", json.loads((directory / "state.json").read_text())["document"]["outcome"])
                    else:
                        finalization.assert_not_called()
                        self.assertEqual([], control.writes)
                        self.assertEqual("failed", json.loads((directory / "state.json").read_text())["document"]["outcome"])

    def test_interrupted_state_rewrite_preserves_previous_complete_document(self):
        directory = self.root / "state-writes"
        directory.mkdir()
        original = {"document": {"sha": self.sha, "outcome": "failed"}}
        publish.write_state(directory, original)
        def interrupted(document, stream):
            stream.write('{"document":')
            raise OSError("interrupted write")
        with patch.object(publish.json, "dump", side_effect=interrupted):
            with self.assertRaisesRegex(OSError, "interrupted write"):
                publish.write_state(directory, {"document": {"outcome": "published"}})
        self.assertEqual(json.loads((directory / "state.json").read_text()), original)
        self.assertEqual([path.name for path in directory.iterdir()], ["state.json"])

    def test_publication_asset_https_redirect_reads_exact_bytes_without_credentials_before_finalization(self):
        certificate, key = self.root / "publication-cert.pem", self.root / "publication-key.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", str(key),
            "-out", str(certificate), "-days", "1", "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1"], check=True, capture_output=True)
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(certificate, key)
        control = self.control()
        values = self.authenticate(control)
        with api_http(control, tls=tls) as signed, api_http(control) as base, patch.dict(os.environ, {"SSL_CERT_FILE": str(certificate)}):
            api = publish.PublishApi(REPOSITORY, "synthetic-publication-token", base_url=base)
            control.publication_location = signed + "/publication-download"
            for location in (base + "/publication-download", signed.replace("https://", "https://user:secret@") + "/publication-download", signed + "/publication-download#fragment"):
                control.publication_location = location
                with self.subTest(location=location), self.assertRaisesRegex(ReleaseError, "uncredentialed HTTPS"):
                    publish.ensure_package_publication(api, values)
            control.publication_location = signed + "/publication-download"
            control.publication_download_location = signed + "/second-hop"
            with self.assertRaisesRegex(ReleaseError, "download returned HTTP 302"):
                publish.ensure_package_publication(api, values)
            del control.publication_download_location
            control.publication_download_status = 503
            with self.assertRaisesRegex(ReleaseError, "download returned HTTP 503"):
                publish.ensure_package_publication(api, values)
            del control.publication_download_status
            control.publication_download_bytes = b"wrong actual bytes"
            with self.assertRaisesRegex(ReleaseError, "asset conflict"):
                publish.ensure_package_publication(api, values)
            del control.publication_download_bytes
            self.assertEqual(control.writes, [])
            outcome = publish.finalize_release(self.git, api, values, refresh=self.refresh)
            self.assertEqual(outcome["outcome"], "published")
        self.assertEqual(len(control.asset_uploads), 1)
        downloads = [auth for method, path, auth in control.requests if path == "/publication-download"]
        self.assertGreaterEqual(len(downloads), 4)
        self.assertTrue(all(auth is None for auth in downloads))
        self.assertTrue(all(auth == "Bearer synthetic-publication-token" for method, path, auth in control.requests if "/releases/assets/" in path))
        self.assertLess(control.events.index("asset-read"), control.events.index("PATCH"))

    def test_public_inventory_upload_readback_and_exact_replay_precede_finalization(self):
        control = self.control()
        values = self.authenticate(control)
        publish.ensure_package_publication(control, values)
        publish.ensure_package_publication(control, values)
        self.assertEqual(len(control.asset_uploads), 1)
        self.assertEqual(publish.publication.read(control.asset_uploads[0])["artifactRun"], RUN)
        self.assertEqual(control.events.count("asset-read"), 2)
        result = publish.finalize_release(self.git, control, values, refresh=self.refresh)
        self.assertEqual(result["outcome"], "published")
        self.assertLess(control.events.index("asset-read"), control.events.index("PATCH"))

    def test_missing_conflicting_duplicate_or_unreadable_inventory_never_returns_ready(self):
        for mode in ("missing", "conflict", "duplicate", "read-error", "upload-error"):
            control = self.control()
            values = self.authenticate(control)
            if mode == "missing":
                del values["package-publication"]
            elif mode == "upload-error":
                control.upload_publication = lambda *_args: (_ for _ in ()).throw(ReleaseError("HTTP 403"))
            else:
                publish.ensure_package_publication(control, values)
                if mode == "conflict":
                    control.asset_data[990] = b"conflicting bytes"
                elif mode == "duplicate":
                    control.assets.append(copy.deepcopy(control.assets[0]))
                else:
                    control.publication_bytes = lambda *_args: (_ for _ in ()).throw(ReleaseError("HTTP 403"))
            with self.subTest(mode=mode), self.assertRaises(ReleaseError):
                publish.finalize_release(self.git, control, values, refresh=lambda *_args: False)
            self.assertEqual(control.writes, [])


def draft_files(payload):
    import zipfile
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        return [(name, archive.read(name)) for name in archive.namelist()]


def use_local_nuget_feed(binaries):
    """Keep real publication verification, redirecting only its public feed read.

    Recovery and publisher composites both execute real scripts through this
    adapter. Missing-feed regressions retain bounded retries without a live
    network dependency or a thirty-minute fixture delay.
    """
    executable = binaries / "python3"
    executable.write_text(f'''#!{sys.executable}
import os, runpy, sys, urllib.request
arguments = [value for value in sys.argv[1:] if value != '-I']
path, *sys.argv = arguments
sys.argv.insert(0, path)
original = urllib.request.urlopen
def local_feed(url, **kwargs):
    assert isinstance(url, str) and url.startswith('https://api.nuget.org/v3-flatcontainer/')
    return original(os.environ['NUGET_TEST_URL'] + '/nuget/' + url.rsplit('/', 1)[-1], **kwargs)
urllib.request.urlopen = local_feed
module = runpy.run_path(path)
if 'verify_packages' in module:
    verify = module['verify_packages']
    module['main'].__globals__['verify_packages'] = lambda values: verify(values, attempts=3, interval=0)
raise SystemExit(module['main']())
''')
    executable.chmod(0o755)


class NuGetVerificationTests(unittest.TestCase):
    def setUp(self):
        self.sha = "a" * 40
        self.values = {"sha": self.sha, "version": VERSION,
                       "repository": REPOSITORY, "artifact-run": str(RUN), "artifact-id": str(ARTIFACT),
                       "artifact-digest": "sha256:" + "d" * 64,
                       "inventory": {"packages": ["Fixture.One", "Fixture.Two"], "containers": []},
                       "package-payloads": {name: publish.publication.payload_map(package(self.sha, package_id=name))
                                            for name in ("Fixture.One", "Fixture.Two")}}

    def response(self, payload):
        response = io.BytesIO(payload)
        response.status, response.headers = 200, {"Content-Length": str(len(payload))}
        return response

    def test_inventory_is_generic_complete_and_credential_free(self):
        requests = []
        def read(url, **kwargs):
            requests.append((url, kwargs))
            identity = "Fixture.One" if "/fixture.one/" in url else "Fixture.Two"
            return self.response(package(self.sha, package_id=identity))
        publish.verify_packages(self.values, open_url=read, attempts=1)
        self.assertEqual([url for url, _ in requests], [
            f"https://api.nuget.org/v3-flatcontainer/{name}/{VERSION}/{name}.{VERSION}.nupkg"
            for name in ("fixture.one", "fixture.two")])
        self.assertTrue(all(set(arguments) == {"timeout"} for _, arguments in requests))

    def test_indexing_delay_and_transient_errors_retry_only_pending_packages(self):
        requests, pauses = [], []
        failures = [404, 408, 429, 503, urllib.error.URLError("unavailable"), TimeoutError()]
        def read(url, **_kwargs):
            requests.append(url)
            if "/fixture.two/" in url and failures:
                error = failures.pop(0)
                if isinstance(error, int):
                    raise urllib.error.HTTPError(url, error, "transient", {}, None)
                raise error
            identity = "Fixture.One" if "/fixture.one/" in url else "Fixture.Two"
            return self.response(package(self.sha, package_id=identity))
        publish.verify_packages(self.values, open_url=read, attempts=7, interval=2, sleep=pauses.append)
        self.assertEqual(sum("/fixture.one/" in url for url in requests), 1)
        self.assertEqual(sum("/fixture.two/" in url for url in requests), 7)
        self.assertEqual(pauses, [2] * 6)

    def test_missing_packages_exhaust_bounds_and_identity_conflicts_fail_immediately(self):
        requests, pauses = [], []
        def missing(url, **_kwargs):
            requests.append(url)
            raise urllib.error.HTTPError(url, 404, "missing", {}, None)
        with self.assertRaisesRegex(ReleaseError, "NuGet publication incomplete: Fixture.One, Fixture.Two"):
            publish.verify_packages(self.values, open_url=missing, attempts=2, interval=1, sleep=pauses.append)
        self.assertEqual(len(requests), 4)
        self.assertEqual(pauses, [1])
        self.values["inventory"]["packages"] = ["Fixture.One"]
        del self.values["package-payloads"]["Fixture.Two"]
        for payload in (package("b" * 40, package_id="Fixture.One"),
                        package(self.sha, package_id="Fixture.Other"),
                        package(self.sha, package_id="Fixture.One", version="9.9.9"), b"not a package"):
            with self.subTest(payload=payload[:20]), self.assertRaises(ReleaseError):
                publish.verify_packages(self.values, open_url=lambda *args, **kwargs: self.response(payload),
                                        sleep=lambda _: self.fail("conflicts must not retry"))

    def test_authorization_errors_are_not_retried_and_empty_inventory_needs_no_network(self):
        for status in (401, 403):
            def forbidden(url, **_kwargs):
                raise urllib.error.HTTPError(url, status, "denied", {}, None)
            with self.subTest(status=status), self.assertRaisesRegex(ReleaseError, f"HTTP {status}"):
                publish.verify_packages(self.values, open_url=forbidden,
                                        sleep=lambda _: self.fail("authorization must not retry"))
        self.values["inventory"]["packages"] = []
        self.values["package-payloads"] = {}
        publish.verify_packages(self.values, open_url=lambda *_args, **_kwargs: self.fail("feed read"))

    def test_response_size_and_whole_verification_deadline_are_bounded(self):
        def oversized(*_args, **_kwargs):
            response = self.response(b"")
            response.headers["Content-Length"] = str(draft.MAX_ARCHIVE + 1)
            return response
        with self.assertRaisesRegex(ReleaseError, "size limit"):
            publish.verify_packages(self.values, open_url=oversized)
        now = [0]
        def unavailable(url, **_kwargs):
            now[0] = 1799
            raise urllib.error.HTTPError(url, 404, "missing", {}, None)
        with self.assertRaisesRegex(ReleaseError, "incomplete"):
            publish.verify_packages(self.values, open_url=unavailable, clock=lambda: now[0],
                                    sleep=lambda _: self.fail("deadline must stop retries"))

    def test_repository_signing_may_add_only_signature_bytes_to_original_payloads(self):
        self.values["inventory"]["packages"] = ["Fixture.One"]
        self.values["package-payloads"].pop("Fixture.Two")
        original = package(self.sha, package_id="Fixture.One")
        signed = zip_bytes([*draft_files(original), (".signature.p7s", b"feed signature")])
        publish.verify_packages(self.values, open_url=lambda *_args, **_kwargs: self.response(signed), attempts=1)
        record = self.values["package-publication"]["packages"][0]
        self.assertEqual(record["archiveSha256"], hashlib.sha256(signed).hexdigest())
        changed = zip_bytes([(name, b"different compiled bytes" if name.endswith(".dll") else payload)
                             for name, payload in draft_files(signed)])
        with self.assertRaisesRegex(ReleaseError, "payload differs"):
            publish.verify_packages(self.values, open_url=lambda *_args, **_kwargs: self.response(changed), attempts=1)


class PublishCompositeTests(History):
    control = PublisherTests.control
    def test_actual_composite_one_outcome_for_success_login_push_api_and_malformed_handoff(self):
        actual_git = shutil.which("git")
        work = self.root / "adapter"
        work.mkdir()
        certificate, key = work / "cert.pem", work / "key.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", str(key),
            "-out", str(certificate), "-days", "1", "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1"],
            check=True, capture_output=True)
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(certificate, key)
        binaries = work / "bin"
        binaries.mkdir()
        (binaries / "git").write_text(f'''#!{sys.executable}
import os, subprocess, sys
args = sys.argv[1:]
assert 'GITHUB_TOKEN' not in os.environ
if 'fetch' in args:
    args = [os.environ['TEST_REMOTE'] if value.startswith('https://github.com/') else value for value in args]
    args = [value.replace('protocol.file.allow=never', 'protocol.file.allow=always') for value in args]
raise SystemExit(subprocess.call([{actual_git!r}, *args]))
''')
        (binaries / "dotnet").write_text(f'''#!{sys.executable}
import hashlib, json, os, pathlib, sys
assert 'GITHUB_TOKEN' not in os.environ and 'NUGET_API_KEY' not in os.environ
assert pathlib.Path.cwd() != pathlib.Path({str(work)!r})
assert not pathlib.Path('NuGet.Config').exists() and not pathlib.Path('global.json').exists()
path = pathlib.Path(sys.argv[3])
assert path.read_bytes() == bytes.fromhex({dict(draft_files(self.payload))["packages/Fixture.Package." + VERSION + ".nupkg"].hex()!r})
with pathlib.Path({str(work / "pushes")!r}).open('a') as log:
    log.write(hashlib.sha256(path.read_bytes()).hexdigest() + '\\n')
raise SystemExit({0})
''')
        use_local_nuget_feed(binaries)
        for executable in binaries.iterdir():
            executable.chmod(0o755)
        (work / "NuGet.Config").write_text("poison")
        (work / "global.json").write_text("poison")
        action = Path(__file__).resolve().parents[2] / "publish"
        tag_conflicts = {"tag-lightweight", "tag-annotated", "tag-noncommit"}
        state_faults = {"state-missing", "state-truncated", "state-invalid"}
        for scenario in ("success", "login", "push", "api", "malformed", "empty", "patch",
                         *sorted(tag_conflicts), "tag-correct-lightweight", "tag-correct-annotated",
                         *sorted(state_faults), "ready", "nuget-missing", "nuget-source", "nuget-authorization"):
            with self.subTest(scenario=scenario):
                if git_command(self.root, "tag", "--list", "v" + VERSION):
                    git_command(self.root, "tag", "-d", "v" + VERSION)
                control = self.control(artifact(self.sha, packages=False) if scenario == "empty" else None)
                if scenario.startswith("tag-"):
                    target = (git_command(self.root, "rev-parse", self.sha + "^{tree}") if scenario == "tag-noncommit"
                              else self.base if scenario in tag_conflicts else self.sha)
                    annotation = ("-a", "-m", "fixture") if scenario.endswith("annotated") else ()
                    git_command(self.root, "tag", *annotation, "v" + VERSION, target)
                push_log = work / "pushes"
                push_log.write_text("")
                output = work / "output"
                output.write_text("")
                environment = {**os.environ, "PATH": str(binaries) + os.pathsep + os.environ["PATH"],
                    "GITHUB_TOKEN": "private-github-marker", "GITHUB_SHA": self.sha,
                    "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF": "refs/heads/main",
                    "GITHUB_REPOSITORY": REPOSITORY, "GITHUB_WORKFLOW_REF": REPOSITORY + "/.github/workflows/release.yml@refs/heads/main",
                    "GITHUB_OUTPUT": str(output), "GITHUB_WORKSPACE": str(work), "TEST_REMOTE": str(self.root),
                    "SSL_CERT_FILE": str(certificate)}
                inputs = {**control.values, "nuget-user": "fixture-user", "registry-username": "u", "registry-password": "p"}
                if scenario == "malformed":
                    inputs["sha"] = "malformed"
                if scenario == "api":
                    control.errors[f"/repos/{REPOSITORY}/git/matching-refs/tags/v{VERSION}"] = 403
                if scenario == "patch":
                    control.patch_status = 403
                if scenario == "nuget-missing":
                    control.nuget_status = 404
                if scenario == "nuget-source":
                    control.nuget_payload = package("b" * 40)
                if scenario == "nuget-authorization":
                    control.nuget_status = 403
                if scenario == "ready":
                    (self.root / ".github/workflows/new.yml").write_text("new workflow")
                    environment["GITHUB_SHA"] = self.commit("ci: advance caller workflows")
                if scenario in state_faults:
                    environment["GITHUB_SHA"] = self.base
                tool = binaries / "dotnet"
                contents = tool.read_text()
                if scenario == "push":
                    tool.write_text(contents.replace("raise SystemExit(0)", "raise SystemExit(3)"))
                uploads, logins = [], []
                def external(reference, arguments):
                    if reference.startswith("NuGet/login@"):
                        logins.append(reference)
                        self.assertEqual(arguments, {"user": "fixture-user"})
                        if scenario == "login":
                            raise subprocess.CalledProcessError(1, "NuGet/login")
                        if scenario in state_faults:
                            state = Path(next(line.split("=", 1)[1] for line in output.read_text().splitlines()
                                              if line.startswith("state=")))
                            self.assertTrue((state / "packages").is_dir())
                            if scenario == "state-missing":
                                (state / "state.json").unlink()
                            else:
                                (state / "state.json").write_text('{"document":' if scenario == "state-truncated"
                                                                 else '{"document": {"sha": "invalid"}}')
                        return {"NUGET_API_KEY": "temporary-key-marker"}
                    self.assertTrue(reference.startswith("actions/upload-artifact@"))
                    self.assertEqual(arguments["name"], "hexalith-release-outcome")
                    self.assertEqual(arguments["overwrite"], "true")
                    path = Path(arguments["path"])
                    document = json.loads(path.read_text())
                    from jsonschema import Draft202012Validator
                    schema = json.loads((Path(__file__).resolve().parents[3] / "schemas/hexalith.release-outcome.v1.json").read_text())
                    Draft202012Validator(schema).validate(document)
                    uploads.append(validate_outcome(document))
                    self.assertFalse(path.is_relative_to(work))
                    shutil.rmtree(path.parent)
                    return {}
                with api_http(control, tls=tls) as signed, api_http(control) as base:
                    control.location = signed + "/archive"
                    control.publication_location = signed + "/publication-download"
                    environment["GITHUB_API_URL"] = base
                    environment["NUGET_TEST_URL"] = base
                    result, processes = run_composite(action, inputs, environment, cwd=work, runner_temp=work, external=external)
                tool.write_text(contents)
                self.assertEqual(len(uploads), 1, "\n".join(process.stderr for process in processes))
                published = scenario in {"success", "empty", "tag-correct-lightweight", "tag-correct-annotated"}
                expected = "ready" if scenario == "ready" else "published" if published else "failed"
                self.assertEqual(uploads[0]["outcome"], expected)
                self.assertEqual(result is None, expected == "failed")
                if scenario in state_faults:
                    self.assertEqual(uploads[0], {"schema": "hexalith.release-outcome.v1",
                                                 "sha": environment["GITHUB_SHA"], "outcome": "failed"})
                    cause = {"state-missing": "FileNotFoundError", "state-truncated": "ReleaseError",
                             "state-invalid": "OutcomeError"}[scenario]
                    self.assertIn(f"release publication state unavailable ({cause})",
                                  "".join(process.stderr for process in processes))
                self.assertEqual(len(control.writes), 1 if published else 0)
                no_push = scenario in {"malformed", "api", "empty", "login"} | tag_conflicts | state_faults
                self.assertEqual(len(push_log.read_text().splitlines()), 0 if no_push else 1)
                self.assertEqual(len(logins), 0 if scenario in {"malformed", "api", "empty"} | tag_conflicts else 1)
                if scenario in tag_conflicts:
                    self.assertIn("tag v2.3.5 does not point at the built commit",
                                  "".join(process.stderr for process in processes))
                    self.assertTrue(control.release["draft"])
                    self.assertEqual(git_command(self.root, "rev-parse", "v" + VERSION + "^{}"), target)
                self.assertNotIn("temporary-key-marker", "".join(process.stdout + process.stderr for process in processes))
                self.assertFalse(any(Path(line.split("=", 1)[1]).exists() for line in output.read_text().splitlines() if line.startswith("state=")))
                self.assertTrue(all(auth is None for method, path, auth in control.requests if path in {"/archive", "/publication-download"}))
                self.assertTrue(all(auth is None for method, path, auth in control.requests if path.startswith("/nuget/")))


if __name__ == "__main__":
    unittest.main()
