"""Consume only the exact pinned producer tool and its verified build environment."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

IMPORTS = {
    "CustomBeforeDirectoryBuildProps": "SourceMapping.targets",
    "CustomAfterMicrosoftCommonProps": "SourceMapping.props",
    "CustomBeforeDirectoryBuildTargets": "SourceMapping.before-targets.props",
    "CustomAfterDirectoryBuildTargets": "SourceMapping.late.props",
    "AfterMicrosoftNETSdkTargets": "SourceMapping.final.targets",
}
KEYS = set(IMPORTS) | {"HEXALITH_SOURCE_MAPPING_HASH", "Configuration"}


def read_json(text: str) -> dict:
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate JSON key: {key}")
            result[key] = value
        return result
    value = json.loads(text, object_pairs_hook=unique)
    if not isinstance(value, dict):
        raise ValueError("expected a JSON object")
    return value


def real_path(path: Path) -> Path:
    if any(part.is_symlink() for part in (path, *path.parents)):
        raise ValueError(f"symbolic-link traversal is forbidden: {path}")
    return path.resolve(strict=True)


def verified_environment(result: dict, workspace: Path) -> dict[str, str]:
    outcome = result.get("outcome")
    if result.get("status") != "prepared" or not isinstance(outcome, dict) or outcome.get("exitCode") != "Success" or result.get("runId") is not None:
        raise ValueError("producer did not return a runtime-free successful preparation")
    prepared = result.get("buildPreparation", {})
    if not isinstance(prepared, dict) or prepared.get("mode") != "package" or prepared.get("workspace") != str(workspace):
        raise ValueError("producer did not select the requested package workspace")
    environment = prepared.get("environment")
    if not isinstance(environment, dict) or set(environment) != KEYS:
        raise ValueError("producer environment differs from the allowed mapping import keys")
    if any(not isinstance(value, str) or any(ord(c) < 32 or ord(c) == 127 for c in value) for value in environment.values()):
        raise ValueError("producer environment contains an invalid value")
    digest = prepared.get("mappingHash", "")
    if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest) or environment["HEXALITH_SOURCE_MAPPING_HASH"] != digest:
        raise ValueError("producer mapping hash is invalid")
    if environment["Configuration"] != "Release":
        raise ValueError("CI preparation must select Release")
    root = real_path(workspace)
    for key, filename in IMPORTS.items():
        expected = workspace / "source-mapping" / filename
        if environment[key] != str(expected) or not real_path(expected).is_relative_to(root) or not expected.is_file():
            raise ValueError(f"invalid generated import for {key}")
    metadata = workspace / "source-mapping" / "mapping.json"
    if not real_path(metadata).is_relative_to(root) or not metadata.is_file():
        raise ValueError("invalid generated mapping metadata")
    mapping = read_json(metadata.read_text(encoding="utf-8"))
    mode = mapping.get("Mode")
    # WorkspaceMode serializes as "package"; integer 1 remains the historical encoding.
    package_mode = mode == "package" or type(mode) is int and mode == 1
    if mapping.get("ContentHash") != digest or not package_mode or mapping.get("HasGit") is not True:
        raise ValueError("persisted mapping differs from the package preparation")
    return environment


def run(command: list[str], root: Path) -> str:
    completed = subprocess.run(command, cwd=root, text=True, capture_output=True, check=False)
    if completed.returncode:
        raise ValueError(f"{command[0]} exited {completed.returncode}: {completed.stdout} {completed.stderr}")
    return completed.stdout.strip()


def prepare(manifest: str, root: Path, temporary_root: Path, github_env: Path) -> None:
    relative = Path(manifest)
    if relative.is_absolute() or not re.fullmatch(r"[A-Za-z0-9_./-]+", manifest) or any(part in (".", "..", "") for part in manifest.split("/")):
        raise ValueError("manifest must be a repository-relative path without traversal")
    if not real_path(root / relative).is_relative_to(real_path(root)):
        raise ValueError("manifest escapes the repository")
    manifest_path = root / ".config" / "dotnet-tools.json"
    real_path(manifest_path)
    tools = read_json(manifest_path.read_text(encoding="utf-8"))
    entries = tools.get("tools")
    if not isinstance(entries, dict) or not isinstance(entries.get("hexalith.builds.module.cli"), dict):
        raise ValueError("the local manifest must declare the module CLI tool")
    tool = entries["hexalith.builds.module.cli"]
    version = tool.get("version", "")
    if tools.get("isRoot") is not True or not isinstance(version, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version) or tool.get("rollForward") is not False or tool.get("commands") != ["hexalith-module"]:
        raise ValueError("the local tool manifest must pin the exact module CLI with rollForward=false")
    platform = root / "references" / "Hexalith.Platform"
    # Identity resolution reads local tags at the committed direct Platform pin.
    # Never let Git discover the parent repository through an uninitialized path.
    real_path(platform / ".git")
    run(["git", "-c", "submodule.recurse=false", "-C", str(platform), "fetch", "--no-recurse-submodules", "--tags", "origin"], root)
    run(["dotnet", "tool", "restore", "--tool-manifest", str(manifest_path)], root)
    observed = run(["dotnet", "tool", "run", "hexalith-module", "--", "--version"], root)
    if observed.split("+", 1)[0] != version:
        raise ValueError(f"restored tool version {observed!r} differs from exact pin {version!r}")
    workspace = real_path(temporary_root) / ("hexalith-module-build-" + uuid.uuid4().hex)
    result = read_json(run(["dotnet", "tool", "run", "hexalith-module", "--", "prepare-build", "--manifest", manifest, "--mode", "package", "--workspace", str(workspace), "--output", "json"], root))
    environment = verified_environment(result, workspace)
    # Validate everything before appending any GitHub environment command. Never evaluate producer text.
    with github_env.open("a", encoding="utf-8", newline="\n") as stream:
        stream.write("".join(f"{key}={environment[key]}\n" for key in sorted(environment)))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    arguments = parser.parse_args()
    try:
        prepare(arguments.manifest, Path.cwd(), Path(os.environ["RUNNER_TEMP"]), Path(os.environ["GITHUB_ENV"]))
    except (OSError, ValueError, KeyError) as error:
        print(f"prepare-module-build: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
