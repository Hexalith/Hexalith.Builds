"""Publication effects through disposable Git histories, HTTP and actual composites."""
from __future__ import annotations

import copy
from contextlib import contextmanager, redirect_stdout
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
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_publish as publish
import release_draft as draft
import oci_archive as oci
import test_release_oci as oci_fixture
from release_state import Git, ReleaseError
from release_outcome import validate_outcome
from test_release_draft import ARTIFACT, RUN, VERSION, REPOSITORY, DraftApiFixture, History, artifact, digest, git_command, zip_bytes
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
        self.after_patch = lambda value: value

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
            elif self.path.endswith("/zip"):
                self.send_response(302)
                self.send_header("Location", control.location)
                self.end_headers()
            elif self.path == "/archive":
                self.send_response(200)
                self.send_header("Content-Length", str(len(control.payload)))
                self.end_headers()
                self.wfile.write(control.payload)
            else:
                self.respond(200, control.get(self.path))
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
        return publish.authenticate(self.git, control, copy.deepcopy(control.values), work,
                                    REPOSITORY + "/.github/workflows/release.yml@refs/heads/main", **kwargs)

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


def draft_files(payload):
    import zipfile
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        return [(name, archive.read(name)) for name in archive.namelist()]


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
        for executable in binaries.iterdir():
            executable.chmod(0o755)
        (work / "NuGet.Config").write_text("poison")
        (work / "global.json").write_text("poison")
        action = Path(__file__).resolve().parents[2] / "publish"
        tag_conflicts = {"tag-lightweight", "tag-annotated", "tag-noncommit"}
        state_faults = {"state-missing", "state-truncated", "state-invalid"}
        for scenario in ("success", "login", "push", "api", "malformed", "empty", "patch",
                         *sorted(tag_conflicts), "tag-correct-lightweight", "tag-correct-annotated",
                         *sorted(state_faults), "ready"):
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
                    environment["GITHUB_API_URL"] = base
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
                self.assertTrue(all(auth is None for method, path, auth in control.requests if path == "/archive"))


if __name__ == "__main__":
    unittest.main()
