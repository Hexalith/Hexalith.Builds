"""Real registry HTTP reads and pinned-tool subprocess effects."""
from __future__ import annotations

import base64
from contextlib import contextmanager
import hashlib
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.request
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import oci_publish as publisher
from oci_archive import archive_files
from release_state import NoRedirect, ReleaseError
import test_release_oci as fixture


@contextmanager
def registry_http(files, graph):
    requests, failures = [], {}
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass
        def do_GET(self):
            requests.append((self.path, self.headers.get("Authorization")))
            reference = self.path.rsplit("/", 1)[-1]
            if self.path in failures:
                status, payload = failures[self.path]
            else:
                data = files.get("blobs/sha256/" + (graph["root"] if reference == "2.4.0" else reference)[7:])
                status, payload = (200, data) if data is not None else (404, b"")
            self.send_response(status)
            if status == 302:
                self.send_header("Location", self.server.destination + "/stolen")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.destination = f"http://127.0.0.1:{server.server_port}"
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    class LocalOpener:
        def open(self, request, **kwargs):
            assert request.full_url.startswith("https://registry.hexalith.com/v2/product/image/")
            replacement = urllib.request.Request(server.destination + request.selector, headers=dict(request.headers))
            return urllib.request.build_opener(NoRedirect()).open(replacement, **kwargs)
    registry = publisher.Registry("product/image", "scoped-user", "private-marker")
    registry.opener = LocalOpener()
    try:
        yield registry, requests, failures
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


class OciPublishTests(unittest.TestCase):
    setUp = fixture.OciTests.setUp
    combine = fixture.OciTests.combine
    def test_complete_remote_bytes_and_correct_retry(self):
        graph = self.combine()
        files = archive_files(self.output)
        with registry_http(files, graph) as (registry, requests, failures):
            self.assertTrue(registry.preflight("2.4.0", graph))
            copies = []
            publisher.publish(self.output, "product/image", "2.4.0", graph, "scoped-user", "private-marker",
                              registry=registry, copy=lambda *args: copies.append(args))
            self.assertEqual(len(copies), 1)
            self.assertTrue(all(auth == "Basic " + base64.b64encode(b"scoped-user:private-marker").decode()
                                for _, auth in requests))
            self.assertTrue(any("/blobs/" in path for path, _ in requests))
            self.assertTrue(any("/manifests/sha256:" in path for path, _ in requests))
            failures["/v2/product/image/blobs/" + next(
                digest for digest in graph["blobs"] if digest not in [graph["root"], *graph["children"]])] = (200, b"corrupt")
            with self.assertRaisesRegex(ReleaseError, "digest or size"):
                registry.verify("2.4.0", graph)

    def test_absence_conflict_auth_and_redirect_are_distinct(self):
        graph = self.combine()
        with registry_http({}, graph) as (registry, requests, failures):
            self.assertFalse(registry.preflight("2.4.0", graph))
            path = "/v2/product/image/manifests/2.4.0"
            for status, payload, message in ((200, b"different root", "conflicts"),
                                              (401, b"", "HTTP 401"), (500, b"", "HTTP 500"),
                                              (302, b"", "HTTP 302")):
                failures[path] = (status, payload)
                copies = []
                with self.subTest(status=status), self.assertRaisesRegex(ReleaseError, message):
                    publisher.publish(self.output, "product/image", "2.4.0", graph, "u", "p", registry=registry,
                                      copy=lambda *args: copies.append(args))
                self.assertEqual(copies, [])
            self.assertFalse(any(path == "/stolen" for path, _ in requests))

    def test_private_auth_lifetime_exact_tool_and_original_archive(self):
        self.combine()
        calls, auth_paths = [], []
        tool = json.loads((publisher.ROOT / "Tools/spikes/publish-tools.json").read_text())["skopeo"]
        def run(arguments, **kwargs):
            calls.append(arguments)
            mount = next(arguments[index + 1] for index, value in enumerate(arguments)
                         if value == "--volume" and arguments[index + 1].endswith(":/auth:ro"))
            auth = Path(mount.removesuffix(":/auth:ro")) / "auth.json"
            auth_paths.append(auth)
            self.assertEqual(auth.stat().st_mode & 0o777, 0o600)
            self.assertEqual(set(json.loads(auth.read_text())["auths"]), {publisher.HOST})
            self.assertNotIn("private-marker", " ".join(arguments))
            self.assertNotIn("GITHUB_TOKEN", kwargs["env"])
            return subprocess.CompletedProcess(arguments, 0,
                stdout=f"skopeo version {tool['version']} commit: {tool['source_commit']}\n")
        publisher.skopeo_copy(self.output, "product/image", "2.4.0", "u", "private-marker", run=run)
        self.assertEqual(len(calls), 2)
        self.assertIn("--all", calls[-1])
        self.assertIn("--preserve-digests", calls[-1])
        self.assertIn(str(self.output) + ":/payload.tar:ro", calls[-1])
        self.assertTrue(all(not auth.exists() for auth in auth_paths))
        for returned in ("skopeo version 1.22.3", "skopeo version 1.22.3 commit: wrong"):
            calls.clear()
            def wrong(arguments, **kwargs):
                calls.append(arguments)
                return subprocess.CompletedProcess(arguments, 0, stdout=returned)
            with self.assertRaisesRegex(ReleaseError, "version/source"):
                publisher.skopeo_copy(self.output, "product/image", "2.4.0", "u", "p", run=wrong)
            self.assertEqual(len(calls), 1)
        with self.assertRaisesRegex(ReleaseError, "failed"):
            publisher.skopeo_copy(self.output, "product/image", "2.4.0", "u", "p",
                run=lambda *args, **kwargs: (_ for _ in ()).throw(subprocess.TimeoutExpired("docker", 1)))
        self.assertEqual(list(self.root.glob("hexalith-oci-auth-*")), [])


if __name__ == "__main__":
    unittest.main()
