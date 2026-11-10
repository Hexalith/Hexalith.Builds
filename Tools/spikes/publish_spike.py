#!/usr/bin/env python3
"""Build the isolated fixture once; normalize packaging without changing OCI bytes."""

from __future__ import annotations

import io
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Github/lib"))
from ci_pipeline import build_flags
from publish_spike import inspect_archive


def combine_archives(archives: list[Path], destination: Path) -> list[str]:
    """Wrap unchanged SDK manifests in one OCI index; never rewrite an image blob.

    SDK 10.0.403's multi-RID label transport splits on ':' and truncates source
    URLs. Independent SDK platform archives avoid that bug without patching SDK.
    """
    files = {}
    children = []
    compressions = []
    for path, architecture in zip(archives, ("amd64", "arm64"), strict=True):
        compressions.append("gzip" if path.read_bytes()[:2] == b"\x1f\x8b" else "none")
        with tarfile.open(path, "r:*") as archive:
            platform_files = {member.name.removeprefix("./"): archive.extractfile(member).read()
                              for member in archive if member.isfile()}
        descriptor, = json.loads(platform_files["index.json"])["manifests"]
        descriptor["platform"] = {"os": "linux", "architecture": architecture}
        children.append(descriptor)
        for name, data in platform_files.items():
            if name.startswith("blobs/"):
                if name in files and files[name] != data:
                    raise ValueError("conflicting OCI blob during archive assembly")
                files[name] = data
    index = json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json",
                        "manifests": children}, separators=(",", ":")).encode()
    digest = hashlib.sha256(index).hexdigest()
    files["blobs/sha256/" + digest] = index
    files["oci-layout"] = b'{"imageLayoutVersion":"1.0.0"}'
    files["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [{
        "mediaType": "application/vnd.oci.image.index.v1+json", "digest": "sha256:" + digest,
        "size": len(index), "annotations": {"org.opencontainers.image.ref.name": "0.0.0-spike.20261110"}}]}).encode()
    with tarfile.open(destination, "w") as archive:
        for name, data in sorted(files.items()):
            member = tarfile.TarInfo(name)
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
    return compressions


def main() -> None:
    """Build each platform once, assemble unchanged images, then record identity."""
    output = ROOT / "artifacts/publish-spike"
    output.mkdir(parents=True, exist_ok=False)
    (output / "containers").mkdir()
    project = ROOT / "Samples/PublishSpike/PublishSpike.csproj"
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    flags = [*build_flags(), f"-p:SourceRevisionId={revision}"]
    subprocess.run(["dotnet", "restore", str(project), *flags], cwd=ROOT, check=True)
    archives = []
    for rid in ("linux-x64", "linux-arm64"):
        archive = output / f"sdk-{rid}.tar.gz"
        subprocess.run(["dotnet", "publish", str(project), "--no-restore", "-t:PublishContainer",
                        "--runtime", rid, f"-p:ContainerArchiveOutputPath={archive}",
                        *flags], cwd=ROOT, check=True)
        archives.append(archive)
    normalized = output / "containers/publish-spike.tar"
    compressions = combine_archives(archives, normalized)
    for archive in archives:
        archive.unlink()
    subprocess.run(["dotnet", "pack", str(project), "--no-build", "--no-restore",
                    "-p:RuntimeIdentifier=linux-x64", "--output", str(output / "packages"),
                    *flags], cwd=ROOT, check=True)
    graph = inspect_archive(normalized, revision=revision)
    inventory = {
        "repository": "Hexalith/Hexalith.Builds", "source_revision": revision,
        "run_id": int(os.environ.get("GITHUB_RUN_ID", "0")),
        "run_attempt": int(os.environ.get("GITHUB_RUN_ATTEMPT", "0")),
        "sdk": subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True).strip(),
        "sdk_archive_compression": compressions, "graph": graph,
        "files": {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in sorted(output.rglob("*")) if path.is_file()},
    }
    (output / "inventory.json").write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(inventory, indent=2))


if __name__ == "__main__":
    main()
