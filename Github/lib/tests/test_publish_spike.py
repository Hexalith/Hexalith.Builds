"""Behavioral archive, artifact, registry and credential-boundary controls."""

from __future__ import annotations

from contextlib import contextmanager
import copy
import http.client
import http.server
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import threading
import unittest
import urllib.request
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import publish_spike as spike

REVISION = "a" * 40


def archive_fixture(path: Path, *, platforms=("amd64", "arm64"), missing_blob=False) -> dict:
    """Create a real OCI archive with shared layer bytes and two distinct configs."""
    files = {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}'}

    def store(value, media="application/vnd.oci.image.manifest.v1+json"):
        data = value if isinstance(value, bytes) else json.dumps(value).encode()
        digest = spike.sha256(data)
        files["blobs/sha256/" + digest[7:]] = data
        return {"digest": digest, "size": len(data), "mediaType": media}

    layer = store(b"layer payload", "application/vnd.oci.image.layer.v1.tar+gzip")
    children = []
    for architecture in platforms:
        config = store({"os": "linux", "architecture": architecture, "config": {"Labels": {
            "org.opencontainers.image.source": "https://github.com/" + spike.REPOSITORY,
            "org.opencontainers.image.revision": REVISION,
            "org.opencontainers.image.version": spike.TAG}}})
        child = store({"schemaVersion": 2, "config": config, "layers": [layer]})
        child["platform"] = {"os": "linux", "architecture": architecture}
        children.append(child)
    root = store({"schemaVersion": 2, "manifests": children}, "application/vnd.oci.image.index.v1+json")
    files["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [root]}).encode()
    if missing_blob:
        del files["blobs/sha256/" + layer["digest"][7:]]
    with tarfile.open(path, "w") as archive:
        for name, data in files.items():
            member = tarfile.TarInfo(name)
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
    return {spike.sha256(data): data for name, data in files.items() if name.startswith("blobs/")}


@contextmanager
def http_boundary(responses):
    """Use real HTTP sockets while preserving urllib's logical HTTPS origins."""
    requests = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append((self.headers["Host"], self.path, self.headers.get("Authorization")))
            status, headers, body = responses.get(self.path, (404, {}, b"missing"))
            self.send_response(status)
            for name, value in headers.items():
                self.send_header(name, value)
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)

    class LoopbackHTTPS(urllib.request.HTTPSHandler):
        def https_open(self, request):
            def connection(_host, **kwargs):
                return http.client.HTTPConnection("127.0.0.1", server.server_port, **kwargs)
            return self.do_open(connection, request)

    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        with patch.object(urllib.request, "HTTPSHandler", LoopbackHTTPS), \
                patch.object(urllib.request, "_opener", None), \
                patch.object(urllib.request, "getproxies", return_value={}):
            yield requests, server.server_port
    finally:
        server.shutdown()
        server.server_close()
        worker.join()


class MemoryRegistry:
    """Observe exactly which graph bytes the copy effect installed."""

    def __init__(self):
        self.data = {}
        self.tag = None

    def get(self, kind, reference, *, missing_ok=False):
        return self.tag if reference == spike.TAG else self.data[reference]


class PublishSpikeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "image.tar"
        self.blobs = archive_fixture(self.archive)
        self.graph = spike.inspect_archive(self.archive, revision=REVISION)

    def test_archive_and_replay_preserve_all_original_bytes(self):
        registry = MemoryRegistry()
        writes = []

        def copy_command_effect():
            # A real child command produces the copy marker; registry double records its bytes.
            subprocess.run([sys.executable, "-c", "from pathlib import Path; import sys; Path(sys.argv[1]).write_text('copied')",
                            str(self.root / "copy-marker")], check=True)
            writes.append(self.archive.read_bytes())
            registry.data.update(self.blobs)
            registry.tag = self.blobs[self.graph["root"]]

        first = spike.publish_archive(self.archive, self.graph, registry, copy_command_effect)
        replay = spike.publish_archive(self.archive, self.graph, registry, copy_command_effect)
        self.assertFalse(first["previously_present"])
        self.assertTrue(replay["previously_present"])
        self.assertEqual(writes[0], writes[1])
        self.assertTrue((self.root / "copy-marker").is_file())
        self.assertEqual(first["verified"], replay["verified"])

    def test_conflicting_destination_stops_before_command(self):
        registry = MemoryRegistry()
        registry.tag = b"other content"
        with self.assertRaisesRegex(spike.SpikeError, "conflicts"):
            spike.publish_archive(self.archive, self.graph, registry,
                                  lambda: self.fail("must not invoke copy on conflicting tag"))

    def test_corrupt_archive_missing_platform_blob_and_labels(self):
        for platforms, missing in [(("amd64",), False), (("amd64", "arm64"), True)]:
            archive_fixture(self.archive, platforms=platforms, missing_blob=missing)
            with self.assertRaises(spike.SpikeError):
                spike.inspect_archive(self.archive, revision=REVISION)
        self.archive.write_bytes(b"corrupt tar")
        with self.assertRaises(spike.SpikeError):
            spike.inspect_archive(self.archive, revision=REVISION)
        archive_fixture(self.archive)
        with self.assertRaisesRegex(spike.SpikeError, "label mismatch"):
            spike.inspect_archive(self.archive, revision="b" * 40)

    def test_wrong_artifact_id_run_digest_source_and_expiration(self):
        original = {"id": 10, "expired": False, "digest": "sha256:" + "b" * 64,
                    "workflow_run": {"id": 20, "head_sha": REVISION}}
        arguments = dict(artifact_id=10, run_id=20, digest=original["digest"], revision=REVISION)
        spike.validate_identity(original, **arguments)
        for value in [{"id": 11}, {"expired": True}, {"digest": "sha256:" + "c" * 64},
                      {"workflow_run": {"id": 21, "head_sha": REVISION}},
                      {"workflow_run": {"id": 20, "head_sha": "c" * 40}}]:
            with self.subTest(value=value), self.assertRaises(spike.SpikeError):
                spike.validate_identity({**copy.deepcopy(original), **value}, **arguments)

    def test_artifact_hash_corruption_and_traversal_write_nothing(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            archive.writestr("../escape", "unsafe")
        payload = stream.getvalue()
        target = self.root / "download"
        target.mkdir()
        for digest in ["sha256:" + "0" * 64, spike.sha256(payload)]:
            with self.assertRaises(spike.SpikeError):
                spike.unpack_artifact(payload, target, digest=digest)
            self.assertEqual(list(target.iterdir()), [])
        valid = io.BytesIO()
        with zipfile.ZipFile(valid, "w") as archive:
            archive.writestr("containers/fixture.tar", self.archive.read_bytes())
        spike.unpack_artifact(valid.getvalue(), target, digest=spike.sha256(valid.getvalue()))
        self.assertEqual((target / "containers/fixture.tar").read_bytes(), self.archive.read_bytes())

    def test_remote_graph_loss_fails_verification(self):
        registry = MemoryRegistry()
        def copy_incomplete():
            registry.data.update(self.blobs)
            registry.tag = self.blobs[self.graph["root"]]
            registry.data[self.graph["children"][0]] = b"truncated"
        with self.assertRaisesRegex(spike.SpikeError, "digest or size mismatch"):
            spike.publish_archive(self.archive, self.graph, registry, copy_incomplete)

    def test_registry_redirects_never_reach_destination_or_forward_credentials(self):
        responses = {"/destination": (200, {}, b"unexpected redirect")}
        registry = spike.Registry("scoped-user", "synthetic-password")
        with http_boundary(responses) as (requests, port):
            for location in ("https://other-origin.invalid/destination",
                             f"http://127.0.0.1:{port}/destination"):
                with self.subTest(location=location):
                    requests.clear()
                    source = "/v2/" + spike.DESTINATION.split("/", 1)[1] + "/manifests/" + spike.TAG
                    responses[source] = (302, {"Location": location}, b"")
                    with self.assertRaisesRegex(spike.SpikeError, "registry read failed: HTTP 302"):
                        registry.get("manifests", spike.TAG, missing_ok=True)
                    self.assertEqual(requests, [("registry.hexalith.com", source, registry.authorization)])

    def test_artifact_download_separates_authenticated_api_from_signed_storage(self):
        inventory = {"source_revision": REVISION, "run_id": 20, "repository": spike.REPOSITORY,
                     "files": {"containers/fixture.tar": spike.sha256(self.archive.read_bytes())[7:]}}
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            archive.writestr("inventory.json", json.dumps(inventory))
            archive.writestr("containers/fixture.tar", self.archive.read_bytes())
        payload = stream.getvalue()
        metadata = {"id": 10, "expired": False, "digest": spike.sha256(payload),
                    "workflow_run": {"id": 20, "head_sha": REVISION}}
        api = "/repos/" + spike.REPOSITORY + "/actions/artifacts/10"
        responses = {}
        arguments = dict(artifact_id=10, run_id=20, digest=metadata["digest"], revision=REVISION,
                         token="synthetic-github-token")
        expected = [("api.github.com", api, "Bearer synthetic-github-token"),
                    ("api.github.com", api + "/zip", "Bearer synthetic-github-token"),
                    ("signed-storage.invalid", "/artifact.zip", None)]
        with http_boundary(responses) as (requests, port):
            for scenario in ("valid", "wrong-metadata", "corrupt-download", "insecure-redirect"):
                with self.subTest(scenario=scenario):
                    requests.clear()
                    target = self.root / scenario
                    target.mkdir()
                    current = {**metadata, "id": 11} if scenario == "wrong-metadata" else metadata
                    location = (f"http://127.0.0.1:{port}/artifact.zip" if scenario == "insecure-redirect"
                                else "https://signed-storage.invalid/artifact.zip")
                    responses.update({api: (200, {}, json.dumps(current).encode()),
                                      api + "/zip": (302, {"Location": location}, b""),
                                      "/artifact.zip": (200, {}, b"corrupt" if scenario == "corrupt-download"
                                                       else payload)})
                    if scenario == "valid":
                        self.assertEqual(spike.download_artifact(target, **arguments), metadata)
                        self.assertEqual((target / "containers/fixture.tar").read_bytes(), self.archive.read_bytes())
                        self.assertEqual(requests, expected)
                    else:
                        message = {"wrong-metadata": "identity mismatch",
                                   "corrupt-download": "downloaded artifact digest mismatch",
                                   "insecure-redirect": "artifact redirect must use HTTPS"}[scenario]
                        with self.assertRaisesRegex(spike.SpikeError, message):
                            spike.download_artifact(target, **arguments)
                        count = {"wrong-metadata": 1, "insecure-redirect": 2, "corrupt-download": 3}[scenario]
                        self.assertEqual(requests, expected[:count])
                        self.assertEqual(list(target.iterdir()), [])

    def test_login_failure_has_nonzero_exit_and_never_echoes_key(self):
        for key, status in [("", 1), ("   ", 1), ("synthetic-sensitive-control-key", 0)]:
            result = subprocess.run([sys.executable, "-I", str(Path(spike.__file__)), "--login-only"],
                                    env={**os.environ, "NUGET_API_KEY": key}, text=True, capture_output=True)
            self.assertEqual(result.returncode, status)
            if key.strip():
                self.assertNotIn(key, result.stdout + result.stderr)

    def test_skopeo_command_executes_pinned_distribution_and_removes_authfile(self):
        docker = self.root / "docker"
        docker.write_text("#!/usr/bin/env python3\nimport json,os,sys\nfrom pathlib import Path\n"
                          "if sys.argv[-1]=='--version': print(os.environ['TOOL_VERSION'])\n"
                          "else: Path(os.environ['COMMAND_LOG']).write_text(json.dumps(sys.argv[1:]))\n")
        docker.chmod(0o755)
        archive = self.root / "fixture.tar"
        archive.write_bytes(self.archive.read_bytes())
        log = self.root / "command.json"
        tool = json.loads((spike.ROOT / "Tools/spikes/publish-tools.json").read_text())["skopeo"]
        expected = f"skopeo version {tool["version"]} commit: {tool["source_commit"]}"
        with patch.dict(os.environ, {"PATH": str(self.root) + os.pathsep + os.environ["PATH"],
                                    "COMMAND_LOG": str(log), "TOOL_VERSION": expected}):
            spike.skopeo_copy(archive, self.root, "scoped-user", "synthetic-password")
        command = json.loads(log.read_text())
        self.assertIn("--all", command)
        self.assertIn("--preserve-digests", command)
        tool = json.loads((spike.ROOT / "Tools/spikes/publish-tools.json").read_text())["skopeo"]
        self.assertIn(tool["distribution"] + "@" + tool["digest"], command)
        self.assertNotIn("synthetic-password", log.read_text())
        self.assertFalse((self.root / "auth.json").exists())


    def test_wrong_skopeo_version_prevents_copy_and_removes_authfile(self):
        docker = self.root / "docker"
        marker = self.root / "unexpected-copy"
        docker.write_text("#!/usr/bin/env python3\nimport os,sys\nfrom pathlib import Path\n"
                          "if sys.argv[-1]=='--version': print(os.environ['TOOL_VERSION'])\n"
                          f"else: Path({str(marker)!r}).touch()\n")
        docker.chmod(0o755)
        for version in ("skopeo version 1.22.2", "skopeo version 1.22.3 commit: " + "0" * 40,
                        "skopeo version 1.22.3 unexpected output"):
            with self.subTest(version=version), patch.dict(os.environ, {
                    "PATH": str(self.root) + os.pathsep + os.environ["PATH"], "TOOL_VERSION": version}):
                with self.assertRaisesRegex(spike.SpikeError, "version mismatch"):
                    spike.skopeo_copy(self.archive, self.root, "scoped-user", "synthetic-password")
            self.assertFalse(marker.exists())
            self.assertFalse((self.root / "auth.json").exists())


if __name__ == "__main__":
    unittest.main()
