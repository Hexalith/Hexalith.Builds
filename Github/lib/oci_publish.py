#!/usr/bin/env python3
"""Publish original complete OCI graphs using the pinned official Skopeo image."""
from __future__ import annotations

import base64
import hashlib
import http.client
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

from oci_archive import INDEX, MANIFEST
from release_draft import CONTAINER_NAME, bounded
from release_state import NoRedirect, ReleaseError

HOST = "registry.hexalith.com"
ROOT = Path(__file__).resolve().parents[2]


def tool_environment():
    """Do not expose publisher secrets or workspace tool configuration to Docker."""
    return {key: value for key, value in os.environ.items()
            if key in {"PATH", "HOME", "TMPDIR", "XDG_RUNTIME_DIR", "DOCKER_HOST"}}


class Registry:
    """Read only one repository at the intended endpoint, never following redirects."""

    def __init__(self, name, username, password):
        if not CONTAINER_NAME.fullmatch(name) or not username or not password:
            raise ReleaseError("container name and both scoped registry credentials are required")
        self.name = name
        self.authorization = "Basic " + base64.b64encode((username + ":" + password).encode()).decode()
        self.opener = urllib.request.build_opener(NoRedirect())

    def read(self, kind, reference, *, missing_ok=False, expected=None):
        """Return manifest bytes, or stream-check a blob's exact digest and length."""
        if kind not in {"manifests", "blobs"}:
            raise ReleaseError("unsupported registry operation")
        deadline = time.monotonic() + 180
        def request():
            url = f"https://{HOST}/v2/{self.name}/{kind}/{reference}"
            headers = {"Authorization": self.authorization, "Accept": INDEX + ", " + MANIFEST}
            try:
                with self.opener.open(urllib.request.Request(url, headers=headers), timeout=60) as response:
                    limit = expected[1] if expected else 4 * 1024 * 1024
                    total, digest, chunks = 0, hashlib.sha256(), []
                    while chunk := response.read(min(1024 * 1024, limit + 1 - total)):
                        total += len(chunk)
                        if total > limit:
                            raise ReleaseError("registry response exceeds expected size")
                        digest.update(chunk)
                        if expected is None:
                            chunks.append(chunk)
                    if expected:
                        if total != expected[1] or "sha256:" + digest.hexdigest() != expected[0]:
                            raise ReleaseError("remote manifest/blob digest or size mismatch")
                        return b""
                    return b"".join(chunks)
            except urllib.error.HTTPError as error:
                with error:
                    if missing_ok and error.code == 404:
                        return None
                    raise ReleaseError(f"registry read failed: HTTP {error.code}") from error
            except (OSError, http.client.HTTPException) as error:
                raise ReleaseError("registry read transport failed") from error
        return bounded(request, deadline=deadline)

    def preflight(self, version, graph):
        existing = self.read("manifests", version, missing_ok=True)
        if existing is not None and "sha256:" + hashlib.sha256(existing).hexdigest() != graph["root"]:
            raise ReleaseError("destination version conflicts with original artifact: " + self.name)
        return existing is not None

    def verify(self, version, graph):
        self.read("manifests", version, expected=(graph["root"], graph["blobs"][graph["root"]]))
        for digest, size in graph["blobs"].items():
            kind = "manifests" if digest in [graph["root"], *graph["children"]] else "blobs"
            self.read(kind, digest, expected=(digest, size))


def skopeo_copy(archive, name, version, username, password, *, run=subprocess.run):
    """No credentials in argv or logs; private endpoint-bound auth removed on all exits."""
    tool = json.loads((ROOT / "Tools/spikes/publish-tools.json").read_text())["skopeo"]
    archive = Path(archive).resolve()
    with tempfile.TemporaryDirectory(prefix="hexalith-oci-auth-", dir=archive.parent) as temporary:
        work = Path(temporary)
        auth = work / "auth.json"
        descriptor = os.open(auth, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, "w") as stream:
            json.dump({"auths": {HOST: {"auth": base64.b64encode(
                (username + ":" + password).encode()).decode()}}}, stream)
        container = "hexalith-publish-" + work.name
        base = ["docker", "run", "--rm", "--name", container, "--network", "host", "--volume", f"{work}:/auth:ro",
                "--volume", f"{archive}:/payload.tar:ro", tool["distribution"] + "@" + tool["digest"]]
        environment = tool_environment()
        try:
            result = run([*base, "--version"], capture_output=True, text=True, check=False,
                         timeout=180, cwd=work, env=environment)
            if result.returncode or result.stdout.strip() != f"skopeo version {tool['version']} commit: {tool['source_commit']}":
                raise ReleaseError("Skopeo distribution version/source mismatch")
            result = run([*base, "copy", "--all", "--preserve-digests", "--authfile", "/auth/auth.json",
                          "oci-archive:/payload.tar", "docker://" + HOST + "/" + name + ":" + version],
                         capture_output=True, check=False, timeout=1800, cwd=work, env=environment)
            if result.returncode:
                raise ReleaseError("Skopeo publication failed")
        except (OSError, subprocess.TimeoutExpired) as error:
            # Killing a Docker client does not stop its daemon-owned container.
            try:
                run(["docker", "rm", "--force", container], capture_output=True, check=False,
                    timeout=60, cwd=work, env=environment)
            except (OSError, subprocess.TimeoutExpired):
                pass
            raise ReleaseError("Skopeo publication execution failed or timed out") from error


def publish(archive, name, version, graph, username, password, *, registry=None, copy=skopeo_copy):
    registry = registry or Registry(name, username, password)
    registry.preflight(version, graph)  # Recheck immediately before each copy, including retries.
    copy(archive, name, version, username, password)
    registry.verify(version, graph)
