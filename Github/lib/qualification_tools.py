#!/usr/bin/env python3
"""Stage exact official qualification tools in a fresh private job directory.

The product owns version/profile selection and qualification assertions. Builds
owns bounded downloads, integrity checks and explicit fresh-worker Dapr setup.
Staging alone starts no services and never grants runtime eligibility.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from urllib.parse import urlsplit

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError, _read_json
from ci_pipeline import PipelineError, contained_path, publish_outputs
from ci_environment import DAPR_RECEIPT_ENV, export_path, initialize_dapr


NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
DIGEST = re.compile(r"[a-f0-9]{64}\Z")
SOURCE = re.compile(
    r"/(?:dapr/(?:dapr|cli)|kubernetes-sigs/kind|kubernetes/kubernetes)/"
    r"releases/download/v[0-9]+\.[0-9]+\.[0-9]+/[A-Za-z0-9_.-]+\Z")
LIMIT = 512 * 1024 * 1024


def validate_initialization(artifacts: list[dict], version: str) -> None:
    """Allow an explicit historical tuple only on a fresh disposable hosted VM."""
    if not version:
        return
    if (re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version) is None
            or tuple(map(int, version.split("."))) < (1, 18, 0)):
        raise PipelineError("Dapr qualification initialization requires an exact runtime version >=1.18.0")
    if (platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64")
            or os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"):
        raise PipelineError("historical Dapr initialization requires a fresh disposable hosted Linux worker")
    if os.environ.get(DAPR_RECEIPT_ENV) or (Path.home() / ".dapr").exists() or (Path.home() / ".dapr").is_symlink():
        raise PipelineError("historical Dapr initialization refuses an existing Dapr home or initialization receipt")
    cli = [row for row in artifacts if row["name"] == "dapr"]
    if (len(cli) != 1 or cli[0]["format"] != "tar.gz" or cli[0]["member"] != "dapr"
            or not cli[0]["url"].startswith("https://github.com/dapr/cli/releases/download/")):
        raise PipelineError("historical Dapr initialization requires the verified official dapr CLI artifact")


def read_manifest(root: Path, relative: str) -> list[dict]:
    """Require a closed, nonempty manifest of immutable official release assets."""
    path = contained_path(root, relative, kind="qualification tool manifest")
    if (not relative.endswith(".json") or not path.is_file()
            or any(item.is_symlink() for item in (root / relative, *(root / relative).parents))):
        raise PipelineError("qualification tool manifest must be a regular module JSON file")
    document = _read_json(path)
    if (not isinstance(document, dict) or set(document) != {"schema", "artifacts"}
            or document["schema"] != "hexalith.qualification-tools.v1"
            or not isinstance(document["artifacts"], list) or not 1 <= len(document["artifacts"]) <= 64):
        raise PipelineError("invalid qualification tools manifest")
    names = set()
    for item in document["artifacts"]:
        required = {"name", "url", "sha256", "size", "format"}
        if (not isinstance(item, dict) or not required <= item.keys()
                or item.keys() - required - {"member", "binarySha256"}):
            raise PipelineError("invalid qualification artifact fields")
        name = item["name"]
        if (not isinstance(name, str) or NAME.fullmatch(name) is None
                or name in names or name == "receipt.json"):
            raise PipelineError("invalid or duplicate qualification artifact name")
        names.add(name)
        url = item["url"]
        if not isinstance(url, str):
            raise PipelineError("qualification source must be an official HTTPS release URL")
        parsed = urlsplit(url)
        if (parsed.scheme != "https" or parsed.netloc != "github.com" or parsed.query or parsed.fragment
                or SOURCE.fullmatch(parsed.path) is None):
            raise PipelineError("qualification source must be an official HTTPS release URL")
        if not isinstance(item["sha256"], str) or DIGEST.fullmatch(item["sha256"]) is None:
            raise PipelineError("qualification artifact requires an exact SHA-256")
        if type(item["size"]) is not int or not 1 <= item["size"] <= LIMIT:
            raise PipelineError("qualification artifact requires an exact bounded size")
        if item["format"] not in ("file", "binary", "tar.gz"):
            raise PipelineError("unsupported qualification artifact format")
        if item["format"] == "tar.gz":
            if (set(item) != required | {"member", "binarySha256"}
                    or not isinstance(item["member"], str) or NAME.fullmatch(item["member"]) is None
                    or not isinstance(item["binarySha256"], str)
                    or DIGEST.fullmatch(item["binarySha256"]) is None):
                raise PipelineError("tar qualification artifact requires an exact member and executable SHA-256")
        elif set(item) != required:
            raise PipelineError("raw qualification artifacts cannot specify archive members")
    return document["artifacts"]


def download(url: str, size: int) -> bytes:
    """Bound both transport time and bytes; redirects must remain HTTPS."""
    try:
        result = subprocess.run(
            ["curl", "--disable", "--fail", "--location", "--proto", "=https", "--proto-redir", "=https",
             "--max-time", "90", "--max-filesize", str(size), "--silent", "--show-error", url],
            capture_output=True, timeout=100, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise PipelineError("qualification artifact download failed") from error
    if result.returncode:
        raise PipelineError(f"qualification artifact download failed ({result.returncode})")
    data = result.stdout
    if len(data) != size:
        raise PipelineError("qualification artifact size mismatch")
    return data


def executable_bytes(data: bytes, member: str, expected: str) -> bytes:
    """Read one exact regular executable without extracting any archive paths."""
    try:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
            members = archive.getmembers()
            matches = [entry for entry in members if entry.name == member]
            if (len(matches) != 1 or not matches[0].isfile() or not 0 < matches[0].size <= LIMIT
                    or any(not entry.isfile() for entry in members)
                    or len({entry.name for entry in members}) != len(members)
                    or any("/" in entry.name or "\\" in entry.name or entry.name in (".", "..")
                           for entry in members)):
                raise PipelineError("qualification archive has unsafe or ambiguous members")
            with archive.extractfile(matches[0]) as stream:
                content = stream.read(LIMIT + 1)
    except (OSError, EOFError, tarfile.TarError) as error:
        raise PipelineError("invalid qualification archive") from error
    if not content or len(content) > LIMIT or hashlib.sha256(content).hexdigest() != expected:
        raise PipelineError("qualification executable SHA-256 mismatch")
    return content


def stage(root: Path, manifest: str, *, temporary: Path | None = None, transport=download,
          dapr_runtime_version: str = "") -> dict[str, str]:
    """Publish paths only after the entire selected tool inventory verifies."""
    root = root.resolve()
    artifacts = read_manifest(root, manifest)
    validate_initialization(artifacts, dapr_runtime_version)
    parent = temporary or Path(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()))
    if (not parent.is_absolute() or not parent.is_dir() or parent.resolve() != parent
            or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in str(parent))):
        raise PipelineError("qualification tools require a real absolute temporary directory")
    directory = Path(tempfile.mkdtemp(prefix="hexalith-qualification-tools-", dir=parent))
    if directory.is_relative_to(root):
        shutil.rmtree(directory)
        raise PipelineError("qualification tools must be staged outside the module repository")
    try:
        files, observations = {}, []
        for item in artifacts:
            data = transport(item["url"], item["size"])
            if len(data) != item["size"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
                raise PipelineError(f"{item['name']}: qualification archive identity mismatch")
            content = (executable_bytes(data, item["member"], item["binarySha256"])
                       if item["format"] == "tar.gz" else data)
            path = directory / item["name"]
            with path.open("xb") as stream:
                stream.write(content)
            path.chmod(0o400 if item["format"] == "file" else 0o500)
            files[item["name"]] = str(path)
            observations.append({**item, "path": str(path),
                                 "installedSha256": hashlib.sha256(content).hexdigest()})
        initialization = ""
        if dapr_runtime_version:
            # The same shared implementation captures actual preexisting/new
            # Docker resources. No module-owned initializer or catalog mutation.
            initialization = str(initialize_dapr(Path(files["dapr"]), dapr_runtime_version, root))
        receipt = directory / "receipt.json"
        receipt.write_text(json.dumps({"schema": "hexalith.qualification-tools-receipt.v1",
                                       "qualification": "not-established", "artifacts": observations,
                                       "daprInitializationReceipt": initialization},
                                      sort_keys=True) + "\n", encoding="utf-8")
        receipt.chmod(0o400)
        if initialization:
            export_path(directory)
        return {"directory": str(directory), "receipt": str(receipt), "files": json.dumps(files, sort_keys=True),
                "dapr-receipt": initialization}
    except BaseException:
        shutil.rmtree(directory)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path, default=os.environ.get("GITHUB_WORKSPACE", "."))
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--dapr-runtime-version", default="")
    arguments = parser.parse_args()
    try:
        publish_outputs(stage(arguments.workspace, arguments.manifest,
                              dapr_runtime_version=arguments.dapr_runtime_version))
        return 0
    except (ConfigError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
