"""Exercise draft creation against real Git histories and a local HTTP API."""
from __future__ import annotations

import copy
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import http.server
import io
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.request
from unittest.mock import patch
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_draft as draft
from release_state import Git, ReleaseError
sys.path.insert(0, str(Path(__file__).resolve().parent))
from actions_expressions import run_composite

REPOSITORY = "Example/ReleaseFixture"
VERSION = "2.3.5"
RUN = 123
ARTIFACT = 456


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def zip_bytes(files):
    result = io.BytesIO()
    with zipfile.ZipFile(result, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, data in files:
            archive.writestr(name, data)
    return result.getvalue()


def package(sha, *, package_id="Fixture.Package", version=VERSION):
    return zip_bytes([(package_id + ".nuspec", '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
        '<metadata><id>' + package_id + '</id><version>' + version + '</version>'
        '<repository type="git" url="https://github.com/' + REPOSITORY + '" commit="' + sha + '"/>'
        '</metadata></package>'), ("lib/net10.0/Fixture.dll", b"original-payload")])


def artifact(sha, *, version=VERSION, packages=True, manifest=None, extra=(), nuspec=None):
    values = {"sha": sha, "version": version,
              "packages": [{"id": "Fixture.Package", "path": "packages/Fixture.Package." + version + ".nupkg"}] if packages else [],
              "containers": []}
    values = values if manifest is None else manifest
    files = [("release-manifest.json", json.dumps(values))]
    if packages:
        files.append(("packages/Fixture.Package." + version + ".nupkg", nuspec or package(sha, version=version)))
    return zip_bytes([*files, *extra])


def git_command(directory, *arguments):
    return subprocess.check_output(["git", "-C", str(directory), *arguments], stderr=subprocess.DEVNULL).decode().strip()


class History(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="release-draft-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        git_command(self.root, "init", "-b", "main")
        git_command(self.root, "config", "user.name", "Fixture")
        git_command(self.root, "config", "user.email", "fixture@example.invalid")
        (self.root / ".github/workflows").mkdir(parents=True)
        (self.root / ".github/workflows/release.yml").write_text("fixture\n")
        self.base = self.commit("feat: initial")
        git_command(self.root, "tag", "-a", "v2.3.4", "-m", "annotated base")
        self.sha = self.commit("fix: ship original payload")
        self.git = Git(self.root)
        self.payload = artifact(self.sha)

    def commit(self, message):
        git_command(self.root, "add", ".")
        git_command(self.root, "commit", "--allow-empty", "-m", message)
        return git_command(self.root, "rev-parse", "HEAD")

    def refresh(self, git, repository, sha):
        actual = git.run
        def run(*arguments, **keywords):
            if arguments[0] == "fetch":
                self.refresh_count += 1
                return None
            return actual(*arguments, **keywords)
        with patch.object(git, "run", side_effect=run):
            draft.refresh_workflows(git, repository, sha)

    def invoke(self, api, **changes):
        values = {"sha": self.sha, "version": VERSION, "artifact_id": ARTIFACT,
                  "digest": digest(self.payload), "run_id": RUN, "notes": "Release notes"}
        values.update(changes)
        self.refresh_count = 0
        self.emitted = []
        return draft.create_draft(self.git, api, refresh=self.refresh, emit=self.emitted.append, **values)


class DraftApiFixture:
    def __init__(self, sha, payload):
        self.repository, self.payload = REPOSITORY, payload
        self.metadata = {"id": ARTIFACT, "name": "release", "expired": False, "digest": digest(payload),
            "expires_at": (datetime.now(timezone.utc) + timedelta(days=90)).isoformat(),
            "workflow_run": {"id": RUN, "head_sha": sha}}
        self.drafts, self.writes, self.events = [], [], []
        self.response_change = lambda value: value
        self.clock = time.monotonic

    def get(self, path, deadline=None):
        self.events.append("metadata")
        return self.metadata

    def pages(self, path, deadline=None):
        self.events.append("relist")
        yield from self.drafts

    def artifact_bytes(self, artifact_id):
        self.events.append("download")
        return self.payload

    def post_draft(self, document):
        self.events.append("post")
        self.writes.append(copy.deepcopy(document))
        response = {**document, "id": 789, "author": {"login": "github-actions[bot]"}}
        self.drafts.append(response)
        return self.response_change(response)


class VersionCreationTests(History):
    def test_exact_successors_and_every_rejected_shape(self):
        for version in ("2.3.5", "2.4.0", "3.0.0"):
            with self.subTest(version=version):
                self.assertEqual(draft.strict_successor(self.git, self.sha, version), "v2.3.4")
        for version in ("2.3.6", "2.4.1", "3.1.0", "2.3.4", "1.9.9", "02.3.5", "2.03.5", "2.3.05",
                        "2.3.5-rc.1", "2.3.5+metadata", "v2.3.5", "2.3", "", "2.3.5\n"):
            with self.subTest(version=version), self.assertRaises(ReleaseError):
                draft.strict_successor(self.git, self.sha, version)

    def test_reachable_annotated_tag_wins_over_prerelease_and_unreachable(self):
        git_command(self.root, "tag", "v99.0.0-rc.1")
        git_command(self.root, "checkout", "--orphan", "other")
        self.commit("feat: unreachable")
        git_command(self.root, "tag", "v99.0.0")
        git_command(self.root, "checkout", "main")
        self.assertEqual(draft.strict_successor(self.git, self.sha, VERSION), "v2.3.4")

    def test_tagless_accepts_only_one_zero_zero(self):
        git_command(self.root, "tag", "-d", "v2.3.4")
        self.assertEqual(draft.strict_successor(self.git, self.sha, "1.0.0"), "")
        for version in ("0.1.0", "1.0.1", "2.0.0"):
            with self.subTest(version=version), self.assertRaises(ReleaseError):
                draft.strict_successor(self.git, self.sha, version)


class InventoryTests(History):
    def inventory(self, payload):
        return draft.artifact_inventory(payload, sha=self.sha, version=VERSION,
                                         digest=digest(payload), repository=REPOSITORY)

    def test_real_package_metadata_and_empty_payload(self):
        self.assertEqual(self.inventory(self.payload), {"packages": ["Fixture.Package"], "containers": []})
        self.assertEqual(self.inventory(artifact(self.sha, packages=False)), {"packages": [], "containers": []})

    def test_nested_nuspec_content_is_payload_but_root_nuspec_stays_unique(self):
        with zipfile.ZipFile(io.BytesIO(package(self.sha))) as archive:
            files = [(name, archive.read(name)) for name in archive.namelist()]
        nested = zip_bytes([*files, ("content/templates/template.nuspec", b"<package/>")])
        self.assertEqual(self.inventory(artifact(self.sha, nuspec=nested))["packages"], ["Fixture.Package"])
        second = zip_bytes([*files, ("Other.nuspec", files[0][1])])
        with self.assertRaisesRegex(ReleaseError, "one bounded root nuspec"):
            self.inventory(artifact(self.sha, nuspec=second))

    def test_nuspec_dtd_entities_rejected_for_utf8_and_utf16_encodings(self):
        with zipfile.ZipFile(io.BytesIO(package(self.sha))) as archive:
            xml = archive.read("Fixture.Package.nuspec").decode("utf-8")
        for encoding, declaration in (("utf-8", "UTF-8"), ("utf-16", "UTF-16"),
                                      ("utf-16-le", "UTF-16"), ("utf-16-be", "UTF-16")):
            header = '<?xml version="1.0" encoding="' + declaration + '"?>'
            valid = zip_bytes([("Fixture.Package.nuspec", (header + xml).encode(encoding))])
            with self.subTest(encoding=encoding, declaration=False):
                self.assertEqual(self.inventory(artifact(self.sha, nuspec=valid))["packages"], ["Fixture.Package"])
            poisoned = (header + '<!DOCTYPE package [<!ENTITY id "Fixture.Package">]>'
                        + xml.replace("<id>Fixture.Package</id>", "<id>&id;</id>"))
            malicious = zip_bytes([("Fixture.Package.nuspec", poisoned.encode(encoding))])
            with self.subTest(encoding=encoding, declaration=True), self.assertRaisesRegex(
                    ReleaseError, "package XML declarations are forbidden"):
                self.inventory(artifact(self.sha, nuspec=malicious))

    def test_tampered_digest_and_package_metadata(self):
        with self.assertRaisesRegex(ReleaseError, "digest"):
            draft.artifact_inventory(self.payload + b"tampered", sha=self.sha, version=VERSION,
                                       digest=digest(self.payload), repository=REPOSITORY)
        for change in ({"sha": "a" * 40}, {"package_id": "Other.Package"}, {"version": "9.9.9"}):
            with self.subTest(change=change), self.assertRaises(ReleaseError):
                self.inventory(artifact(self.sha, nuspec=package(**{"sha": self.sha, **change})))

    def test_unsafe_duplicate_and_unlisted_members(self):
        for name in ("../escaped", "/escaped", "packages/../../escaped", "packages\\escaped", "unlisted.txt"):
            with self.subTest(name=name), self.assertRaises(ReleaseError):
                self.inventory(artifact(self.sha, extra=[(name, b"data")]))
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            repeated = artifact(self.sha, extra=[("release-manifest.json", "{}")])
        with self.assertRaises(ReleaseError):
            self.inventory(repeated)
        symlink = zipfile.ZipInfo("packages/link.nupkg")
        symlink.external_attr = (0o120777 << 16)
        with self.assertRaises(ReleaseError):
            self.inventory(zip_bytes([("release-manifest.json", "{}"), (symlink, "outside")]))

    def test_manifest_closed_identity_and_duplicate_json_rules(self):
        base = {"sha": self.sha, "version": VERSION, "packages": [], "containers": []}
        invalid = [{**base, "sha": "a" * 40}, {**base, "version": "9.9.9"}, {**base, "extra": "value"},
                   {**base, "packages": {}}, {**base, "containers": None},
                   {**base, "packages": [{"id": "Invalid,Id", "path": "packages/F.nupkg"}]},
                   {**base, "packages": [{"id": "A", "path": "../F.nupkg"}]},
                   {**base, "packages": [{"id": "A", "path": "packages/missing.nupkg"}]}]
        for manifest in invalid:
            with self.subTest(manifest=manifest), self.assertRaises(ReleaseError):
                self.inventory(artifact(self.sha, packages=False, manifest=manifest))
        duplicate = '{"sha":"' + self.sha + '","sha":"' + self.sha + '","version":"' + VERSION + '","packages":[],"containers":[]}'
        with self.assertRaises(ReleaseError):
            self.inventory(zip_bytes([("release-manifest.json", duplicate)]))

    def test_size_caps_are_enforced_before_reading_members(self):
        with patch.object(draft, "MAX_ARCHIVE", 10), self.assertRaises(ReleaseError):
            self.inventory(self.payload)
        with patch.object(draft, "MAX_MANIFEST", 10), self.assertRaises(ReleaseError):
            self.inventory(self.payload)

    def test_authenticated_container_inventory_runs_production_oci_graph_validation(self):
        import oci_archive as oci
        import test_release_oci as fixtures
        source, combined = self.root / "sdk.tar", self.root / "original.tar"
        with patch.object(fixtures, "SHA", self.sha), patch.object(fixtures, "REPOSITORY", REPOSITORY):
            fixtures.sdk_archive(source, version=VERSION)
        oci.combine_archives([(source, {"os": "linux", "architecture": "amd64"})], combined,
                             revision=self.sha, version=VERSION, repository=REPOSITORY, name="product/image")
        manifest = {"sha": self.sha, "version": VERSION, "packages": [],
                    "containers": [{"name": "product/image", "path": "containers/image.tar"}]}
        payload = artifact(self.sha, packages=False, manifest=manifest,
                           extra=[("containers/image.tar", combined.read_bytes())])
        self.assertEqual(self.inventory(payload), {"packages": [], "containers": ["product/image"]})
        manifest["containers"][0]["name"] = "other/image"
        payload = artifact(self.sha, packages=False, manifest=manifest,
                           extra=[("containers/image.tar", combined.read_bytes())])
        with self.assertRaisesRegex(ReleaseError, "container identity/graph"):
            self.inventory(payload)

    def test_duplicate_package_ids_and_paths_are_rejected(self):
        for second_id, second_path in (("fixture.package", "packages/second.nupkg"),
                                       ("Other.Package", "packages/Fixture.Package.2.3.5.nupkg")):
            manifest = {"sha": self.sha, "version": VERSION, "containers": [], "packages": [
                {"id": "Fixture.Package", "path": "packages/Fixture.Package.2.3.5.nupkg"},
                {"id": second_id, "path": second_path}]}
            extras = [] if second_path.endswith("2.3.5.nupkg") else [(second_path, package(self.sha))]
            with self.subTest(second_id=second_id), self.assertRaises(ReleaseError):
                self.inventory(artifact(self.sha, manifest=manifest, extra=extras))


class DraftCreationTests(History):
    def api(self):
        return DraftApiFixture(self.sha, self.payload)

    def test_create_has_five_trailers_and_exact_eight_outputs_then_retry_reuses(self):
        api = self.api()
        api.drafts.append({"id": 12, "draft": True, "author": {"login": "human"}, "body": "untouched"})
        result = self.invoke(api)
        self.assertEqual(api.events, ["metadata", "download", "relist", "post"])
        self.assertEqual(self.refresh_count, 1)
        self.assertEqual(set(result), {"publish", "version", "sha", "last-tag", "draft-id", "artifact-id", "artifact-digest", "artifact-run"})
        self.assertEqual(result["publish"], "true")
        self.assertEqual(result["artifact-digest"], digest(self.payload))
        self.assertEqual(result["last-tag"], "v2.3.4")
        body = api.writes[0]["body"]
        self.assertEqual(len(draft.RESERVED.findall(body)), 5)
        self.assertIn("hexalith-release-packages: Fixture.Package\n", body)
        self.assertIn("hexalith-release-containers: \n", body)
        self.assertEqual(api.writes[0]["target_commitish"], self.sha)
        self.assertTrue(api.writes[0]["draft"])
        self.assertEqual(git_command(self.root, "tag", "--list", "v2.3.5"), "")
        self.assertEqual(self.invoke(api), result)
        self.assertEqual(len(api.writes), 1)
        self.assertEqual(api.drafts[0]["body"], "untouched")

    def test_artifact_identity_failures_never_write(self):
        for change in ({"id": 1}, {"name": "other"}, {"expired": True}, {"digest": "sha256:" + "0" * 64},
                       {"workflow_run": {"id": 1, "head_sha": self.sha}},
                       {"workflow_run": {"id": RUN, "head_sha": "a" * 40}},
                       {"expires_at": "2020-01-01T00:00:00Z"}, {"expires_at": None}):
            api = self.api()
            api.metadata.update(change)
            with self.subTest(change=change), self.assertRaises(ReleaseError):
                self.invoke(api)
            self.assertEqual(api.writes, [])

    def test_invalid_versions_notes_or_payload_never_write(self):
        for changes in ({"version": "2.4.1"}, {"notes": "Notes\nhexalith-release-run: 999"},
                        {"notes": "Notes\n  HEXALITH-RELEASE: poison"},
                        {"notes": "Notes\rhexalith-release: poison"}, {"digest": "bad"}):
            api = self.api()
            with self.subTest(changes=changes), self.assertRaises(ReleaseError):
                self.invoke(api, **changes)
            self.assertEqual(api.writes, [])
        api = self.api()
        api.payload += b"tamper"
        with self.assertRaises(ReleaseError):
            self.invoke(api)
        self.assertEqual(api.writes, [])

    def test_competing_and_conflicting_same_run_drafts_never_write_again(self):
        for marker, replacement in (("hexalith-release-run: 123", "hexalith-release-run: 999"),
                                    ("hexalith-release-packages: Fixture.Package", "hexalith-release-packages: Other.Package"),
                                    ("hexalith-release-artifact: 456", "hexalith-release-artifact: 999")):
            api = self.api()
            self.invoke(api)
            api.drafts[0]["body"] = api.drafts[0]["body"].replace(marker, replacement)
            with self.subTest(marker=marker), self.assertRaises(ReleaseError):
                self.invoke(api)
            self.assertEqual(len(api.writes), 1)

    def test_invalid_bot_marker_and_multiple_marked_drafts_block_creation(self):
        api = self.api()
        self.invoke(api)
        api.drafts[0]["target_commitish"] = "a" * 40
        with self.assertRaises(ReleaseError):
            self.invoke(api)
        self.assertEqual(len(api.writes), 1)
        api = self.api()
        self.invoke(api)
        api.drafts.append({**api.drafts[0], "id": 790})
        with self.assertRaises(ReleaseError):
            self.invoke(api)
        self.assertEqual(len(api.writes), 1)

    def test_complete_workflow_tree_race_blocks_api_write(self):
        api = self.api()
        (self.root / ".github/workflows/unrelated.yml").write_text("new workflow\n")
        self.commit("ci: change another workflow")
        with self.assertRaisesRegex(ReleaseError, "workflows changed on main during the release, nothing released"):
            self.invoke(api)
        self.assertEqual(api.writes, [])
        self.assertEqual(self.emitted, [])

    def test_main_product_change_preserves_frozen_source_and_allows_creation(self):
        api = self.api()
        (self.root / "product.txt").write_text("new product change\n")
        self.commit("fix: product change during build")
        self.assertEqual(self.invoke(api)["sha"], self.sha)
        self.assertEqual(api.writes[0]["target_commitish"], self.sha)

    def test_fresh_fetch_force_rewrite_with_identical_workflows_blocks_post_and_handoff(self):
        api = self.api()
        unrelated = git_command(self.root, "commit-tree", self.sha + "^{tree}", "-m", "unrelated same tree")
        git_command(self.root, "update-ref", "refs/heads/rewritten", unrelated)
        temporary = tempfile.TemporaryDirectory(prefix="release-draft-force-rewrite-")
        self.addCleanup(temporary.cleanup)
        remote, frozen = Path(temporary.name) / "remote.git", Path(temporary.name) / "frozen.git"
        git_command(self.root, "clone", "--bare", str(self.root), str(remote))
        git_command(self.root, "clone", "--bare", str(self.root), str(frozen))
        fetches = []
        class FetchRemote(Git):
            def run(inner, *arguments, **keywords):
                if arguments[0] == "fetch":
                    # Rewrite the remote only when the production fresh-main fetch starts.
                    git_command(remote, "update-ref", "refs/heads/main", unrelated, self.sha)
                    fetches.append(arguments)
                    arguments = ("-c", "protocol.file.allow=always", *(
                        str(remote) if value == "https://github.com/" + REPOSITORY + ".git" else value
                        for value in arguments))
                return super().run(*arguments, **keywords)
        git = FetchRemote(frozen)
        self.assertTrue(git.ancestor(self.sha, "refs/heads/main"))
        self.assertEqual(git.run("ls-tree", "-z", self.sha, "--", ".github/workflows").stdout,
                         git.run("ls-tree", "-z", unrelated, "--", ".github/workflows").stdout)
        emitted = []
        with self.assertRaisesRegex(ReleaseError, "release source is no longer an ancestor of main"):
            draft.create_draft(git, api, sha=self.sha, version=VERSION, artifact_id=ARTIFACT,
                               digest=digest(self.payload), run_id=RUN, notes="Release notes", emit=emitted.append)
        self.assertEqual(len(fetches), 1)
        self.assertEqual(git.run("rev-parse", "refs/heads/main").stdout.decode().strip(), unrelated)
        self.assertFalse(git.ancestor(self.sha, "refs/heads/main"))
        self.assertEqual(api.writes, [])
        self.assertEqual(emitted, [])

    def test_returned_draft_must_validate_before_outputs(self):
        for change in (lambda value: {**value, "author": {"login": "other"}}, lambda _value: [],
                       lambda value: {**value, "draft": False},
                       lambda value: {**value, "body": value["body"] + "hexalith-release-run: 123\n"}):
            api = self.api()
            api.response_change = change
            with self.subTest(change=change), self.assertRaises(ReleaseError):
                self.invoke(api)
            self.assertEqual(self.emitted, [])

    def test_empty_inventory_persists_empty_list_trailers(self):
        self.payload = artifact(self.sha, packages=False)
        api = self.api()
        self.invoke(api)
        self.assertIn("hexalith-release-packages: \nhexalith-release-containers: \n", api.writes[0]["body"])


@contextmanager
def http_fixture(control, *, tls=None):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_arguments):
            pass
        def send_json(self, status, document):
            encoded = json.dumps(document).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)
        def do_GET(self):
            control.requests.append(("GET", self.path, self.headers.get("Authorization")))
            if self.path.endswith("/zip"):
                self.send_response(302)
                self.send_header("Location", control.location)
                self.end_headers()
            elif self.path == "/archive":
                self.send_response(200)
                self.send_header("Content-Length", str(len(control.payload)))
                self.end_headers()
                self.wfile.write(control.payload)
            elif "/actions/artifacts/" in self.path:
                self.send_json(200, control.metadata)
            elif "/releases?" in self.path:
                self.send_json(200, control.drafts)
            elif "/releases/" in self.path:
                release = next((value for value in control.drafts
                                if str(value["id"]) == self.path.rsplit("/", 1)[-1]), None)
                self.send_json(200 if release is not None else 404, release or {})
            else:
                self.send_json(404, {})
        def do_POST(self):
            control.requests.append(("POST", self.path, self.headers.get("Authorization")))
            if control.post_status != 201:
                self.send_json(control.post_status, {"message": "secret transport details suppressed"})
                return
            document = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            self.send_json(201, control.post_draft(document))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    if tls is not None:
        server.socket = tls.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield ("https" if tls is not None else "http") + "://127.0.0.1:" + str(server.server_port)
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


class HttpDraftTests(History):
    def test_executed_metadata_signed_download_and_post_are_credential_separated(self):
        control = DraftApiFixture(self.sha, self.payload)
        control.requests, control.location, control.post_status = [], "https://signed.example.invalid/archive", 201
        notes = ("## [2.3.5](https://github.com/Example/ReleaseFixture/compare/v2.3.4...v2.3.5) (2026-11-11)\n\n"
                 "### Bug Fixes\n\n* preserve generated multiline notes ([abc1234](https://example.invalid/abc1234))\n\n"
                 "### Performance Improvements\n\n* keep internal blank lines and punctuation: a:b\n")
        with http_fixture(control) as base:
            api = draft.DraftApi(REPOSITORY, "test-token", base)
            actual_opener = urllib.request.build_opener(draft.NoRedirect())
            class SignedTransport:
                def open(self, url, timeout):
                    self_url = url
                    if self_url != control.location:
                        raise AssertionError("unexpected signed destination")
                    return actual_opener.open(base + "/archive", timeout=timeout)
            with patch.object(urllib.request, "build_opener", return_value=SignedTransport()):
                result = self.invoke(api, notes=notes)
            persisted = api.get(f"/repos/{REPOSITORY}/releases/{result['draft-id']}", deadline=time.monotonic() + 2)
        self.assertEqual(result["draft-id"], "789")
        prefix, boundary, trailers = persisted["body"].partition("\n\nhexalith-release:")
        self.assertEqual(prefix, notes.rstrip())
        self.assertEqual(boundary, "\n\nhexalith-release:")
        self.assertEqual("hexalith-release:" + trailers, "\n".join([
            "hexalith-release: " + self.sha, "hexalith-release-run: " + str(RUN),
            f"hexalith-release-artifact: {ARTIFACT} {digest(self.payload)}",
            "hexalith-release-packages: Fixture.Package", "hexalith-release-containers: ", ""]))
        self.assertEqual(len(draft.RESERVED.findall(persisted["body"])), 5)
        self.assertEqual([entry[0] for entry in control.requests], ["GET", "GET", "GET", "GET", "POST", "GET"])
        self.assertIsNone(next(entry[2] for entry in control.requests if entry[1] == "/archive"))
        self.assertTrue(all(entry[2] == "Bearer test-token" for entry in control.requests if entry[1] != "/archive"))

    def test_insecure_artifact_redirect_and_http_errors_fail_closed(self):
        control = DraftApiFixture(self.sha, self.payload)
        control.requests, control.location, control.post_status = [], "http://example.invalid/archive", 201
        with http_fixture(control) as base:
            api = draft.DraftApi(REPOSITORY, "test-token", base)
            with self.assertRaisesRegex(ReleaseError, "HTTPS"):
                self.invoke(api)
            self.assertFalse(any(entry[0] == "POST" for entry in control.requests))
            control.post_status = 403
            with self.assertRaisesRegex(ReleaseError, "HTTP 403") as error:
                api.post_draft({"draft": True})
            self.assertNotIn("secret", str(error.exception))

    def test_api_json_reads_and_writes_are_capped_and_redirects_are_not_followed(self):
        control = DraftApiFixture(self.sha, self.payload)
        control.requests, control.location, control.post_status = [], "https://example.invalid/archive", 201
        with http_fixture(control) as base:
            api = draft.DraftApi(REPOSITORY, "test-token", base)
            with patch.object(draft, "MAX_JSON", 10), self.assertRaises(ReleaseError):
                api.get(f"/repos/{REPOSITORY}/actions/artifacts/{ARTIFACT}", deadline=time.monotonic() + 2)
            with patch.object(draft, "MAX_JSON", 10), self.assertRaises(ReleaseError):
                api.post_draft({"draft": True})
            control.post_status = 302
            previous = len(control.requests)
            with self.assertRaisesRegex(ReleaseError, "HTTP 302"):
                api.post_draft({"draft": True})
            self.assertEqual(len(control.requests), previous + 1)
            with self.assertRaisesRegex(ReleaseError, "HTTP 404"):
                api.get(f"/repos/{REPOSITORY}/missing", deadline=time.monotonic() + 2)

    def test_network_deadline_bounds_dribbling_and_size_controls(self):
        with self.assertRaisesRegex(ReleaseError, "deadline"):
            draft.bounded(lambda: time.sleep(0.2), deadline=time.monotonic() + 0.02)
        response = io.BytesIO(b"01234567890")
        response.headers = {}
        with self.assertRaisesRegex(ReleaseError, "size limit"):
            draft.limited_read(response, 10)
        for length, message in (("11", "size limit"), ("-1", "size limit"), ("eleven", "invalid release response length")):
            with self.subTest(length=length):
                response = io.BytesIO(b"0123456789")
                response.headers = {"Content-Length": length}
                with self.assertRaisesRegex(ReleaseError, message):
                    draft.limited_read(response, 10)


class DraftCompositeTests(History):
    def test_subprocess_action_uses_only_trusted_python_git_api_and_never_checks_out(self):
        """Execute the action's real run command with anonymous real Git and TLS data."""
        actual_git = shutil.which("git")
        actual_openssl = shutil.which("openssl")
        self.assertIsNotNone(actual_openssl)
        (self.root / ".gitattributes").write_text("*.json filter=poison\n")
        self.sha = self.commit("ci: add poisoned checkout filter")
        self.payload = artifact(self.sha)
        control = DraftApiFixture(self.sha, self.payload)
        control.requests, control.location, control.post_status = [], "", 201
        work = self.root / "adapter"
        work.mkdir()
        certificate, key = work / "certificate.pem", work / "key.pem"
        subprocess.run([actual_openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes",
            "-keyout", str(key), "-out", str(certificate), "-days", "1", "-subj", "/CN=127.0.0.1",
            "-addext", "subjectAltName=IP:127.0.0.1"], check=True, capture_output=True)
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(certificate, key)
        commands = work / "commands.jsonl"
        binaries = work / "bin"
        binaries.mkdir()
        wrapper = binaries / "git"
        wrapper.write_text(f'''#!{sys.executable}
import json, os, subprocess, sys
assert not any(k in os.environ for k in ('GITHUB_TOKEN', 'GH_TOKEN', 'GITHUB_SHA'))
arguments = sys.argv[1:]
with open(os.environ['TEST_GIT_LEDGER'], 'a') as stream:
    stream.write(json.dumps(arguments) + '\\n')
if 'fetch' in arguments:
    arguments = [os.environ['TEST_REMOTE'] if argument.startswith('https://github.com/') else argument
                 for argument in arguments]
    arguments = [argument.replace('protocol.file.allow=never', 'protocol.file.allow=always')
                 for argument in arguments]
raise SystemExit(subprocess.call([{actual_git!r}, *arguments]))
''')
        wrapper.chmod(0o755)
        for tool in ("dotnet", "npm", "uv", "pip"):
            executable = binaries / tool
            executable.write_text("#!/bin/sh\necho forbidden-tool >&2\nexit 99\n")
            executable.chmod(0o755)
        action_path = Path(__file__).resolve().parents[2] / "release-draft"
        # Execute the composite's own input/env/output mappings; RELEASE_* is never preset.
        output = work / "output"
        output.write_text("")
        environment = {**os.environ, "PATH": str(binaries) + os.pathsep + os.environ["PATH"],
            "GITHUB_ACTION_PATH": str(action_path), "GITHUB_TOKEN": "adapter-secret-marker",
            "GITHUB_REPOSITORY": REPOSITORY, "GITHUB_RUN_ID": str(RUN), "GITHUB_OUTPUT": str(output),
            "TEST_REMOTE": str(self.root), "TEST_GIT_LEDGER": str(commands), "SSL_CERT_FILE": str(certificate)}
        for name in ("RELEASE_SHA", "RELEASE_VERSION", "RELEASE_NOTES", "RELEASE_ARTIFACT_ID", "RELEASE_ARTIFACT_DIGEST"):
            environment.pop(name, None)
        notes = "## Subprocess notes\n\n* multiline entry"
        inputs = {"sha": self.sha, "version": VERSION, "notes": notes, "artifact-id": str(ARTIFACT),
                  "artifact-digest": digest(self.payload)}
        with http_fixture(control, tls=tls) as signed_base, http_fixture(control) as base:
            control.location = signed_base + "/archive"
            environment["GITHUB_API_URL"] = base
            values, processes = run_composite(action_path, inputs, environment, cwd=work, runner_temp=work)
        result = processes[-1]
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values["publish"], "true")
        self.assertEqual(values["sha"], self.sha)
        self.assertEqual(values["artifact-run"], str(RUN))
        self.assertEqual(values["artifact-digest"], digest(self.payload))
        self.assertEqual(len(values), 8)
        self.assertNotIn("adapter-secret-marker", result.stdout + result.stderr + commands.read_text())
        self.assertNotIn("forbidden-tool", result.stdout + result.stderr)
        invocations = [json.loads(line) for line in commands.read_text().splitlines()]
        self.assertEqual(sum("fetch" in arguments for arguments in invocations), 2)
        self.assertFalse(any("checkout" in arguments or "submodule" in arguments for arguments in invocations))
        self.assertEqual(len(control.writes), 1)
        self.assertTrue(control.writes[0]["body"].startswith(notes + "\n\nhexalith-release: " + self.sha + "\n"))
        self.assertIsNone(next(entry[2] for entry in control.requests if entry[1] == "/archive"))
        self.assertEqual(git_command(self.root, "tag", "--list", "v2.3.5"), "")


if __name__ == "__main__":
    unittest.main()
