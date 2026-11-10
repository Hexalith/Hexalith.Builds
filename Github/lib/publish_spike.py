#!/usr/bin/env python3
"""Isolated OCI artifact experiment; stdlib only, no source checkout or build tools.

All trusted code and tool pins resolve beside this file in the executing action.
Artifact identity is supplied by the producing job, never inferred from a name.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "Hexalith/Hexalith.Builds"
DESTINATION = "registry.hexalith.com/cicd-publish-spike-3-1"
TAG = "0.0.0-spike.20261110"
DIGEST = re.compile(r"sha256:[0-9a-f]{64}\Z")
SHA = re.compile(r"[0-9a-f]{40}\Z")
MEDIA = "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json"


class SpikeError(ValueError):
    """A required identity, graph, authentication or command boundary failed."""


def sha256(data: bytes) -> str:
    return "sha256:" + hashlib.sha256(data).hexdigest()


def inspect_archive(path: Path, *, revision: str) -> dict:
    """Verify the complete archive graph before any registry write."""
    try:
        with tarfile.open(path, "r:*") as archive:
            files = {}
            for member in archive:
                name = member.name.removeprefix("./")
                parts = PurePosixPath(name).parts
                if member.isdir():
                    continue
                if (not member.isfile() or name.startswith("/") or ".." in parts
                        or name in files or member.size > 1024 * 1024 * 1024):
                    raise SpikeError("unsafe or duplicate archive member")
                files[name] = archive.extractfile(member).read()
        if json.loads(files["oci-layout"])["imageLayoutVersion"] != "1.0.0":
            raise SpikeError("unsupported OCI layout")
        layout = json.loads(files["index.json"])

        def blob(descriptor: dict) -> bytes:
            digest = descriptor["digest"]
            if not DIGEST.fullmatch(digest):
                raise SpikeError("invalid descriptor digest")
            data = files["blobs/sha256/" + digest.split(":")[1]]
            if sha256(data) != digest or len(data) != descriptor["size"]:
                raise SpikeError("blob digest/size mismatch")
            return data

        roots = layout["manifests"]
        if len(roots) != 1:
            raise SpikeError("expected one root index")
        root = roots[0]
        index = json.loads(blob(root))
        children = index["manifests"]
        platforms = []
        blobs = {root["digest"]: root["size"]}
        manifests = []
        for child in children:
            platform = child["platform"]
            platforms.append(platform["os"] + "/" + platform["architecture"])
            manifest = json.loads(blob(child))
            manifests.append(child["digest"])
            blobs[child["digest"]] = child["size"]
            config = json.loads(blob(manifest["config"]))
            if config["os"] != platform["os"] or config["architecture"] != platform["architecture"]:
                raise SpikeError("configuration platform mismatch")
            labels = config["config"]["Labels"]
            expected = {"org.opencontainers.image.source": "https://github.com/" + REPOSITORY,
                        "org.opencontainers.image.revision": revision,
                        "org.opencontainers.image.version": TAG}
            if any(labels.get(key) != value for key, value in expected.items()):
                raise SpikeError("source/revision/version label mismatch")
            for descriptor in [manifest["config"], *manifest["layers"]]:
                blob(descriptor)
                blobs[descriptor["digest"]] = descriptor["size"]
        if sorted(platforms) != ["linux/amd64", "linux/arm64"]:
            raise SpikeError("expected exactly linux/amd64 and linux/arm64")
        return {"root": root["digest"], "children": manifests, "platforms": sorted(platforms),
                "blobs": blobs, "archive_sha256": sha256(path.read_bytes())}
    except (KeyError, TypeError, tarfile.TarError, json.JSONDecodeError, OSError) as error:
        raise SpikeError(f"invalid or incomplete OCI archive: {error}") from error


def validate_identity(metadata: dict, *, artifact_id: int, run_id: int, digest: str, revision: str) -> None:
    """Compare GitHub-authenticated metadata with explicit producer outputs."""
    if not DIGEST.fullmatch(digest) or not SHA.fullmatch(revision):
        raise SpikeError("invalid trusted artifact digest/source revision")
    if (metadata.get("id") != artifact_id or metadata.get("expired") is not False
            or metadata.get("digest") != digest or metadata.get("workflow_run", {}).get("id") != run_id
            or metadata.get("workflow_run", {}).get("head_sha") != revision):
        raise SpikeError("artifact ID/run/digest/source identity mismatch")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Keep the GitHub token away from signed artifact storage redirects."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def download_artifact(directory: Path, *, artifact_id: int, run_id: int, digest: str,
                      revision: str, token: str) -> dict:
    """Authenticate metadata and archive bytes, then extract only regular safe files."""
    url = f"https://api.github.com/repos/{REPOSITORY}/actions/artifacts/{artifact_id}"
    headers = {"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
               "X-GitHub-Api-Version": "2022-11-28"}
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as response:
        metadata = json.load(response)
    validate_identity(metadata, artifact_id=artifact_id, run_id=run_id, digest=digest, revision=revision)
    try:
        urllib.request.build_opener(NoRedirect()).open(
            urllib.request.Request(url + "/zip", headers=headers), timeout=60)
    except urllib.error.HTTPError as error:
        if error.code != 302:
            raise SpikeError(f"artifact archive endpoint returned HTTP {error.code}") from error
        location = error.headers["Location"]
    else:
        raise SpikeError("expected a signed archive download redirect")
    if not location.startswith("https://"):
        raise SpikeError("artifact redirect must use HTTPS")
    with urllib.request.urlopen(location, timeout=120) as response:
        payload = response.read()
    unpack_artifact(payload, directory, digest=digest)
    inventory = json.loads((directory / "inventory.json").read_text(encoding="utf-8"))
    if (inventory["source_revision"] != revision or inventory["run_id"] != run_id
            or inventory["repository"] != REPOSITORY):
        raise SpikeError("inventory producer mismatch")
    for name, expected in inventory["files"].items():
        if not safe_name(name) or hashlib.sha256((directory / name).read_bytes()).hexdigest() != expected:
            raise SpikeError("inventory file digest mismatch")
    return metadata


def safe_name(name: str) -> bool:
    return bool(name and not name.startswith("/") and "\\" not in name
                and all(part not in ("", ".", "..") for part in name.split("/")))


def unpack_artifact(payload: bytes, directory: Path, *, digest: str) -> None:
    """Reject corruption and traversal before exposing any archive file."""
    if sha256(payload) != digest:
        raise SpikeError("downloaded artifact digest mismatch")
    try:
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            seen = set()
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                if (not safe_name(entry.filename) or entry.filename in seen
                        or (entry.external_attr >> 16) & 0o170000 == 0o120000):
                    raise SpikeError("unsafe artifact member")
                seen.add(entry.filename)
            for name in seen:
                destination = directory / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(archive.read(name))
    except (zipfile.BadZipFile, OSError) as error:
        raise SpikeError(f"invalid artifact archive: {error}") from error


def require_login(key: str) -> None:
    """Observe nonempty credentials without retaining or printing the temporary key."""
    if not key or key.isspace():
        raise SpikeError("NuGet login returned an empty temporary key")
    print("NuGet login returned a nonempty masked temporary key; no package publication")


class Registry:
    """Read-only verification using the explicitly supplied scoped identity."""

    def __init__(self, username: str, password: str):
        if not username or not password:
            raise SpikeError("both scoped registry credentials are required")
        self.authorization = "Basic " + base64.b64encode(f"{username}:{password}".encode()).decode()

    def get(self, kind: str, reference: str, *, missing_ok: bool = False) -> bytes | None:
        host, repository = DESTINATION.split("/", 1)
        request = urllib.request.Request(f"https://{host}/v2/{repository}/{kind}/{reference}",
                                         headers={"Authorization": self.authorization, "Accept": MEDIA})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code == 404 and missing_ok:
                return None
            raise SpikeError(f"registry read failed: HTTP {error.code}") from error


def publish_archive(archive: Path, graph: dict, registry: Registry, copy) -> dict:
    """Reject conflicts before copy, then compare every immutable remote byte digest."""
    existing = registry.get("manifests", TAG, missing_ok=True)
    if existing is not None and sha256(existing) != graph["root"]:
        raise SpikeError("destination version conflicts with original artifact")
    copy()
    remote = registry.get("manifests", TAG)
    if sha256(remote) != graph["root"]:
        raise SpikeError("remote root digest mismatch")
    for digest, size in graph["blobs"].items():
        kind = "manifests" if digest in [graph["root"], *graph["children"]] else "blobs"
        data = registry.get(kind, digest)
        if sha256(data) != digest or len(data) != size:
            raise SpikeError("remote manifest/blob digest or size mismatch")
    return {"destination": DESTINATION + ":" + TAG, "previously_present": existing is not None,
            "verified": graph}


def skopeo_copy(archive: Path, work: Path, username: str, password: str) -> None:
    """Run a version/digest pinned official distribution with a private authfile."""
    tool = json.loads((ROOT / "Tools/spikes/publish-tools.json").read_text())["skopeo"]
    image = tool["distribution"] + "@" + tool["digest"]
    auth = work / "auth.json"
    encoded = base64.b64encode(f"{username}:{password}".encode()).decode()
    auth.write_text(json.dumps({"auths": {"registry.hexalith.com": {"auth": encoded}}}))
    auth.chmod(0o600)
    base = ["docker", "run", "--rm", "--volume", f"{work}:/spike:ro", image]
    try:
        version = subprocess.check_output([*base, "--version"], text=True).strip()
        if version != "skopeo version " + tool["version"]:
            raise SpikeError("Skopeo distribution version mismatch")
        command = [*base, "copy", "--all", "--preserve-digests", "--authfile", "/spike/auth.json",
                   "oci-archive:/spike/" + str(archive.relative_to(work)),
                   "docker://" + DESTINATION + ":" + TAG]
        subprocess.run(command, check=True)
    finally:
        auth.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--login-only", action="store_true")
    args = parser.parse_args()
    try:
        require_login(os.environ.get("NUGET_API_KEY", ""))
        if args.login_only:
            return 0
        if os.environ["SPIKE_REPOSITORY"] != DESTINATION or os.environ["SPIKE_TAG"] != TAG:
            raise SpikeError("caller destination does not match isolated spike inputs")
        username, password = os.environ["REGISTRY_USERNAME"], os.environ["REGISTRY_PASSWORD"]
        registry = Registry(username, password)
        with tempfile.TemporaryDirectory(prefix="publish-spike-", dir=os.environ.get("RUNNER_TEMP")) as temporary:
            work = Path(temporary)
            artifact_id, run_id = int(os.environ["ARTIFACT_ID"]), int(os.environ["ARTIFACT_RUN_ID"])
            metadata = download_artifact(work, artifact_id=artifact_id, run_id=run_id,
                                         digest=os.environ["ARTIFACT_DIGEST"], revision=os.environ["SOURCE_REVISION"],
                                         token=os.environ["GITHUB_TOKEN"])
            archive = work / "containers/publish-spike.tar"
            graph = inspect_archive(archive, revision=os.environ["SOURCE_REVISION"])
            result = publish_archive(archive, graph, registry,
                                     lambda: skopeo_copy(archive, work, username, password))
            result.update(artifact_id=artifact_id, producing_run_id=run_id,
                          artifact_digest=metadata["digest"], source_revision=os.environ["SOURCE_REVISION"],
                          action_path=os.environ.get("GITHUB_ACTION_PATH"),
                          caller_revision=os.environ["GITHUB_SHA"], run_id=os.environ["GITHUB_RUN_ID"],
                          run_attempt=os.environ["GITHUB_RUN_ATTEMPT"])
            evidence = Path(os.environ["SPIKE_EVIDENCE"])
            evidence.parent.mkdir(parents=True, exist_ok=True)
            evidence.write_text(json.dumps(result, indent=2) + "\n")
            print(json.dumps(result, indent=2))
        return 0
    except (SpikeError, KeyError, ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"publish spike failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
