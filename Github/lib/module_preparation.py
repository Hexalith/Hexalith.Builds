#!/usr/bin/env python3
"""One verified, runtime-free producer lifetime for shared product commands.

Ordinary entry points accept only authenticated delivered authority. Candidate
qualification uses its separate explicit command and never becomes a CI knob.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError, _read_json
import expected_state
from module_authority import digest_document

NUGET_CONFIG = Path(__file__).resolve().parents[2] / "NuGet.ci.Config"
FROZEN = Path(__file__).resolve().parents[1] / "prepare-module-build/prepare_module_build.py"
SPEC = importlib.util.spec_from_file_location("_frozen_preparation_validation", FROZEN)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)
PUBLISHED_KEYS = frozenset(("HexalithBuildsPublishedToolRoot", "Configuration"))


def enabled(prepared):
    return bool(prepared.document.get("moduleManifest") or prepared.document.get("publishedBuilds"))


STATE_VARIABLE = "HEXALITH_CI_PREPARATION_STATE"


@dataclass(frozen=True)
class BuildEnvironment:
    """Verified producer output plus the exact inputs and file-byte inventory."""

    workspace: Path | None
    values: dict[str, str]
    authority: dict[str, Any]
    inputs: dict[str, str]
    files: dict[str, str]


def _digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _real(path: Path) -> Path:
    for part in (path, *path.parents):
        if part.is_symlink() or (part.exists() and getattr(part.lstat(), "st_file_attributes", 0) & 1024):
            raise ConfigError("producer path traverses a symbolic link or reparse point")
    return path.resolve(strict=True)


def _run(arguments: list[str], root: Path) -> str:
    result = subprocess.run(arguments, cwd=root, capture_output=True, text=True, check=False)
    if result.returncode:
        raise ConfigError(f"managed preparation: {arguments[0]} {arguments[1]} failed ({result.returncode})")
    return result.stdout


def _git(root: Path, *arguments: str) -> str:
    if any(name in os.environ for name in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES")):
        raise ConfigError("ambient Git repository/index selectors are forbidden")
    return _run(["git", "--literal-pathspecs", "-c", "core.protectNTFS=true", "-c", "submodule.recurse=false", *arguments], root).rstrip("\r\n")


def _same_git_root(root: Path, reported: str) -> bool:
    """Compare the actual root identity across Git/native path spellings."""
    try:
        return os.path.samefile(root, _real(Path(reported)))
    except OSError as error:
        raise ConfigError("Git reported an unavailable or invalid repository root") from error


def _pin(root: Path, path: str) -> str:
    row = _git(root, "ls-tree", "-z", "HEAD", "--", path)
    match = re.fullmatch(r"160000 commit ([0-9a-f]{40}|[0-9a-f]{64})\t" + re.escape(path) + "\x00", row)
    if match is None:
        raise ConfigError(f"managed preparation requires a committed direct gitlink: {path}")
    return match[1]


def _tool(root: Path) -> str:
    manifest = _read_json(_real(root / ".config/dotnet-tools.json"))
    tool = manifest.get("tools", {}).get("hexalith.builds.module.cli", {})
    version = tool.get("version")
    if (manifest.get("isRoot") is not True or not isinstance(version, str)
            or re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version) is None
            or tool.get("rollForward") is not False or tool.get("commands") != ["hexalith-module"]):
        raise ConfigError("managed preparation requires an exact stable module CLI pin with rollForward=false")
    return version


def accepted_authority(prepared, *, repository: str, comparison=None, fetch_tags: bool = False,
                       delivered_sha: str = "") -> dict:
    """Bind an authenticated guard comparison to actual initialized identities."""
    if not enabled(prepared):
        return {}
    root = _real(prepared.root)
    if not _same_git_root(root, _git(root, "rev-parse", "--show-toplevel")) or _git(root, "rev-parse", "--show-superproject-working-tree"):
        raise ConfigError("managed preparation requires a standalone module Git root; do not initialize nested submodules")
    head = _git(root, "rev-parse", "HEAD")
    if comparison is None:
        comparison = expected_state.compare_repository(expected_state.Git(root), repository=repository, ref=head, delivered_sha=delivered_sha)
    if comparison is None:
        raise ConfigError("managed preparation requires accepted delivered authority")
    comparison.require_clean()
    config_bytes = comparison.actual.read(".github/hexalith-ci.json")
    if _read_json(root / ".github/hexalith-ci.json") != prepared.document or config_bytes != (root / ".github/hexalith-ci.json").read_bytes():
        raise ConfigError("accepted configuration differs from the actual prepared inputs")
    builds = _real(root / "references/Hexalith.Builds")
    published = bool(prepared.document.get("publishedBuilds"))
    platform = None if published else _real(root / "references/Hexalith.Platform")
    for path in ((builds,) if published else (builds, platform)):
        if not _same_git_root(path, _git(path, "rev-parse", "--show-toplevel")):
            raise ConfigError("initialize the exact direct identity checkout before managed preparation")
        if fetch_tags:
            _git(path, "fetch", "--no-recurse-submodules", "--no-tags", "origin", "refs/tags/*:refs/tags/*")
    if published:
        return {"schema": "hexalith.published-build.v1", "kind": "accepted-delivery", "repository": repository,
            "moduleRoot": str(root), "moduleHead": head, "originalBuildsGitlink": _pin(root, "references/Hexalith.Builds"),
            "buildsTag": prepared.document["buildsTag"], "buildsCommit": _git(builds, "rev-parse", "HEAD"),
            "buildsRoot": str(builds), "deliveredSha": comparison.delivered_sha,
            "configurationDigest": _digest(config_bytes), "perimeterDigest": digest_document(comparison.document),
            "producerPin": _tool(root)}
    platform_commit = _pin(root, "references/Hexalith.Platform")
    tags = sorted(tag for tag in _git(platform, "tag", "--points-at", platform_commit).splitlines()
                  if re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", tag))
    if not tags:
        raise ConfigError("Platform identity has no local release tag; fetch its identity tags before checking")
    return {"schema": "hexalith.managed-build.v1", "kind": "accepted-delivery", "repository": repository,
        "moduleRoot": str(root), "moduleHead": head, "originalBuildsGitlink": _pin(root, "references/Hexalith.Builds"),
        "buildsTag": prepared.document["buildsTag"], "buildsCommit": _git(builds, "rev-parse", "HEAD"),
        "buildsRoot": str(builds), "deliveredSha": comparison.delivered_sha,
        "configurationDigest": _digest(config_bytes), "perimeterDigest": digest_document(comparison.document),
        "platformCommit": platform_commit, "platformCatalogCommit": _pin(platform, "references/Hexalith.Builds"),
        "platformReleaseTag": tags[0], "producerPin": _tool(root)}


def _inputs(prepared, authority: dict, *, qualification: bool = False) -> dict[str, str]:
    if prepared.document.get("publishedBuilds"):
        if qualification:
            raise ConfigError("published preparation rejects candidate qualification")
        import published_builds
        return published_builds.inputs(prepared, authority, git=_git, real=_real, pin=_pin, tool=_tool, digest=digest_document)
    root = _real(prepared.root)
    kind = "candidate-qualification" if qualification else "accepted-delivery"
    if (authority.get("schema") != "hexalith.managed-build.v1" or authority.get("kind") != kind
            or (not qualification and authority.get("qualification") is not None) or authority.get("moduleRoot") != str(root)
            or _git(root, "rev-parse", "HEAD") != authority.get("moduleHead")
            or _git(root, "write-tree") != _git(root, "rev-parse", "HEAD^{tree}")
            or _git(root, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all")):
        raise ConfigError("production preparation requires exact clean accepted-delivery inputs")
    result = {"authority": digest_document(authority), "configuration": digest_document(prepared.document)}
    for name in (prepared.document["moduleManifest"], ".config/dotnet-tools.json", ".gitmodules"):
        path = _real(root / name)
        if not path.is_relative_to(root) or not path.is_file():
            raise ConfigError("managed preparation input escapes its standalone repository")
        result[name] = _digest(path.read_bytes())
    if _tool(root) != authority.get("producerPin"):
        raise ConfigError("producer manifest pin changed")
    for name, commit_key in (("references/Hexalith.Builds", "buildsCommit"), ("references/Hexalith.Platform", "platformCommit")):
        path = _real(Path(authority["buildsRoot"]) if qualification and name == "references/Hexalith.Builds" else root / name)
        actual = _git(path, "rev-parse", "HEAD")
        recorded = authority["originalBuildsGitlink"] if qualification and name == "references/Hexalith.Builds" else actual
        if actual != authority.get(commit_key) or _pin(root, name) != recorded:
            raise ConfigError("direct identity HEAD/gitlink changed")
        if _git(path, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all"):
            raise ConfigError("direct identity checkout is dirty")
        result[name] = actual
    builds = Path(authority["buildsRoot"])
    if _git(builds, "rev-parse", "--verify", "refs/tags/" + authority["buildsTag"] + "^{commit}") != authority["buildsCommit"]:
        raise ConfigError("exact active Builds release tag changed")
    platform = root / "references/Hexalith.Platform"
    if (_pin(platform, "references/Hexalith.Builds") != authority["platformCatalogCommit"]
            or _git(platform, "rev-parse", "--verify", "refs/tags/" + authority["platformReleaseTag"] + "^{commit}") != authority["platformCommit"]):
        raise ConfigError("truthful Platform provenance changed")
    return result


def _files(workspace: Path) -> dict[str, str]:
    result = {}
    for path in sorted(workspace.rglob("*")):
        _real(path)
        if path.is_file():
            result[path.relative_to(workspace).as_posix()] = _digest(path.read_bytes())
        elif not path.is_dir():
            raise ConfigError("unsupported producer output file type")
    if not result:
        raise ConfigError("producer output inventory is empty")
    return result


def prepare_environment(prepared, authority: dict, temporary_root: Path, *, fetch_tags: bool = False) -> BuildEnvironment:
    """Restore and invoke the exact released tool once; export nothing on failure."""
    return _prepare_environment(prepared, authority, temporary_root, fetch_tags=fetch_tags)


def prepare_qualified_environment(prepared, authority: dict, temporary_root: Path) -> BuildEnvironment:
    """Explicit local-only qualification, unavailable through any CI document knob."""
    if authority.get("kind") != "candidate-qualification" or not isinstance(authority.get("qualification"), dict):
        raise ConfigError("candidate qualification requires its separate complete authority")
    return _prepare_environment(prepared, authority, temporary_root, qualification=True)


def _prepare_environment(prepared, authority: dict, temporary_root: Path, *, fetch_tags: bool = False,
                         qualification: bool = False) -> BuildEnvironment:
    if not enabled(prepared):
        return BuildEnvironment(None, {}, {}, {}, {})
    if fetch_tags:
        for path in (("references/Hexalith.Builds",) if prepared.document.get("publishedBuilds") else ("references/Hexalith.Builds", "references/Hexalith.Platform")):
            _git(prepared.root / path, "fetch", "--no-recurse-submodules", "--no-tags", "origin", "refs/tags/*:refs/tags/*")
    if prepared.document.get("publishedBuilds") and sys.platform != "linux":
        raise ConfigError("published preparation supports Linux only")
    inputs = _inputs(prepared, authority, qualification=qualification)
    if any(os.environ.get(name) for name in (*VALIDATOR.IMPORTS, "HexalithBuildsPublishedToolRoot")):
        raise ConfigError("ambient producer import overrides are forbidden before preparation")
    if os.environ.get("Configuration") not in (None, "", "Release"):
        raise ConfigError("managed preparation requires Release")
    temporary_root = _real(temporary_root)
    private = Path(tempfile.mkdtemp(prefix="hexalith-module-preparation-", dir=temporary_root))
    try:
        if prepared.document.get("publishedBuilds"):
            import published_builds
            workspace = private / "producer"
            values = published_builds.prepare(prepared, workspace)
            environment = BuildEnvironment(workspace, values, authority, inputs, _files(workspace))
            _verify_environment(prepared, authority, environment, qualification=qualification)
            return environment
        receipt = private / "authority.json"
        receipt.write_text(json.dumps(authority, sort_keys=True), encoding="utf-8")
        if os.name != "nt":
            receipt.chmod(0o600)
        _run(["dotnet", "tool", "restore", "--tool-manifest", str(prepared.root / ".config/dotnet-tools.json"),
              "--configfile", str(NUGET_CONFIG)], prepared.root)
        observed = _run(["dotnet", "tool", "run", "hexalith-module", "--", "--version"], prepared.root).strip().split("+", 1)[0]
        if observed != authority["producerPin"]:
            raise ConfigError("restored tool differs from exact producer pin")
        workspace = private / "producer"
        command = "prepare-qualified-build" if qualification else "prepare-managed-build"
        output = _run(["dotnet", "tool", "run", "hexalith-module", "--", command, "--manifest",
            str(prepared.root / prepared.document["moduleManifest"]), "--authority", str(receipt), "--workspace", str(workspace), "--output", "json"], prepared.root)
        result = VALIDATOR.read_json(output)
        if set(result) != {"status", "outcome", "diagnostics", "buildPreparation"}:
            raise ConfigError("managed producer returned runtime state or undeclared result fields")
        values = VALIDATOR.verified_environment(result, workspace)
        if _inputs(prepared, authority, qualification=qualification) != inputs:
            raise ConfigError("managed inputs changed while producer was running")
        environment = BuildEnvironment(workspace, values, authority, inputs, _files(workspace))
        _verify_environment(prepared, authority, environment, qualification=qualification)
        return environment
    except BaseException as error:
        shutil.rmtree(private)
        if isinstance(error, (OSError, ValueError)):
            raise ConfigError("managed producer validation refused: " + str(error)) from error
        raise


def verify_environment(prepared, authority: dict, environment: BuildEnvironment) -> None:
    """Revalidate exact original identities and all producer bytes before reuse."""
    _verify_environment(prepared, authority, environment)


def _verify_environment(prepared, authority: dict, environment: BuildEnvironment, *, qualification: bool = False) -> None:
    if not enabled(prepared):
        if environment != BuildEnvironment(None, {}, {}, {}, {}):
            raise ConfigError("disabled preparation cannot consume a producer state")
        return
    if prepared.document.get("publishedBuilds"):
        if (qualification or environment.workspace is None or environment.authority != authority
                or _inputs(prepared, authority) != environment.inputs or _files(environment.workspace) != environment.files
                or set(environment.values) != PUBLISHED_KEYS or environment.values.get("Configuration") != "Release"):
            raise ConfigError("published preparation state, identities or producer bytes changed")
        import published_builds
        version = published_builds.binding(prepared)["publication"]["version"]
        framework = "net11.0" if version.startswith(("5.", "6.")) else "net10.0"
        if environment.values["HexalithBuildsPublishedToolRoot"] != str(environment.workspace / "published" / "tools" / framework / "any"):
            raise ConfigError("published tool root differs from verified workspace")
        return
    if (environment.workspace is None or environment.authority != authority or _inputs(prepared, authority, qualification=qualification) != environment.inputs
            or _files(environment.workspace) != environment.files or set(environment.values) != VALIDATOR.KEYS):
        raise ConfigError("managed preparation state, identities or import bytes changed")
    for key, filename in VALIDATOR.IMPORTS.items():
        if environment.values.get(key) != str(environment.workspace / "source-mapping" / filename):
            raise ConfigError("managed import value differs from verified workspace")
    if environment.values.get("Configuration") != "Release":
        raise ConfigError("managed state must select Release")


@contextmanager
def applied(prepared, authority: dict, environment: BuildEnvironment):
    """Keep one mapping active and restore the caller's process environment."""
    verify_environment(prepared, authority, environment)
    previous = {key: os.environ.get(key) for key in environment.values}
    os.environ.update(environment.values)
    try:
        from build_projects import output_scope
        with output_scope(prepared, environment):
            yield environment
        verify_environment(prepared, authority, environment)
    finally:
        for key, value in previous.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


@contextmanager
def applied_qualification(prepared, authority: dict, environment: BuildEnvironment):
    """Apply only an explicit local qualification; ordinary entry points reject it."""
    _verify_environment(prepared, authority, environment, qualification=True)
    previous = {key: os.environ.get(key) for key in environment.values}
    os.environ.update(environment.values)
    try:
        yield environment
        _verify_environment(prepared, authority, environment, qualification=True)
    finally:
        for key, value in previous.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


def ensure_for_job(prepared, *, repository: str, authority: dict | None = None) -> BuildEnvironment:
    """Reuse a private runner receipt across independent stage processes."""
    if not enabled(prepared):
        return BuildEnvironment(None, {}, {}, {}, {})
    state = os.environ.get(STATE_VARIABLE)
    if state:
        path = _real(Path(state))
        saved = _read_json(path)
        if not isinstance(saved, dict) or set(saved) != {"workspace", "values", "authority", "inputs", "files"}:
            raise ConfigError("invalid private managed preparation receipt")
        environment = BuildEnvironment(Path(saved["workspace"]), saved["values"], saved["authority"], saved["inputs"], saved["files"])
        verify_environment(prepared, environment.authority, environment)
        if any(os.environ.get(key) not in (None, "", value) for key, value in environment.values.items()):
            raise ConfigError("ambient producer values conflict with the private preparation receipt")
        from build_projects import VARIABLE, mapping_value
        mapping = mapping_value(prepared, environment)
        if mapping and os.environ.get(VARIABLE) not in (None, "", mapping):
            raise ConfigError("declared build output mapping changed")
        os.environ.update(environment.values)
        if mapping:
            os.environ[VARIABLE] = mapping
        return environment
    if prepared.document.get("buildProjects") and os.environ.get("HexalithBuildProjects"):
        raise ConfigError("ambient declared build output override is forbidden")
    if authority is None:
        authority = accepted_authority(prepared, repository=repository, fetch_tags=True)
    else:
        if prepared.document.get("publishedBuilds"):
            raise ConfigError("published preparation supports Linux entry points only")
        if authority.get("kind") != "accepted-delivery" or authority.get("repository") != repository:
            raise ConfigError("Windows preparation accepts only CI's accepted-delivery authority")
        authority = {**authority, "moduleRoot": str(prepared.root),
                     "buildsRoot": str(prepared.root / "references/Hexalith.Builds")}
        for path in (("references/Hexalith.Builds",) if prepared.document.get("publishedBuilds") else ("references/Hexalith.Builds", "references/Hexalith.Platform")):
            _git(prepared.root / path, "fetch", "--no-recurse-submodules", "--no-tags", "origin", "refs/tags/*:refs/tags/*")
    environment = prepare_environment(prepared, authority, Path(os.environ["RUNNER_TEMP"]))
    try:
        receipt = environment.workspace.parent / "state.json"
        receipt.write_text(json.dumps({"workspace": str(environment.workspace), "values": environment.values,
            "authority": environment.authority, "inputs": environment.inputs, "files": environment.files}, sort_keys=True), encoding="utf-8")
        if os.name != "nt":
            receipt.chmod(0o600)
        exported = {**environment.values, STATE_VARIABLE: str(receipt)}
        from build_projects import VARIABLE, mapping_value
        mapping = mapping_value(prepared, environment)
        if mapping:
            if os.environ.get(VARIABLE):
                raise ConfigError("ambient declared build output override is forbidden")
            exported[VARIABLE] = mapping
        # Every value is validated before writing any environment command.
        if any("\n" in value or "\r" in value for value in exported.values()):
            raise ConfigError("invalid managed environment channel value")
        with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8", newline="\n") as stream:
            stream.write("".join(key + "=" + value + "\n" for key, value in sorted(exported.items())))
        os.environ.update(exported)
        return environment
    except BaseException:
        shutil.rmtree(environment.workspace.parent)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--windows", action="store_true", required=True)
    parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ConfigError("managed preparation requires Python 3.12 or later")
        from ci_pipeline import PreparedConfig
        workspace = _real(Path(os.environ["GITHUB_WORKSPACE"]))
        sdk = _real(workspace / os.environ["CI_PREPARE_GLOBAL_JSON"])
        manifest = _real(workspace / os.environ["CI_PREPARE_MODULE_MANIFEST"])
        if not sdk.is_relative_to(workspace) or not manifest.is_relative_to(sdk.parent):
            raise ConfigError("trusted Windows preparation paths escape the module root")
        prepared = PreparedConfig(workspace, sdk.parent,
            {"moduleManifest": manifest.relative_to(sdk.parent).as_posix()},
            os.environ["CI_PREPARE_SOLUTION"], os.environ["CI_PREPARE_GLOBAL_JSON"], ())
        authority = VALIDATOR.read_json(os.environ["CI_PREPARE_MODULE_AUTHORITY"])
        ensure_for_job(prepared, repository=os.environ["GITHUB_REPOSITORY"], authority=authority)
        return 0
    except (ConfigError, OSError, ValueError, KeyError) as error:
        print(ConfigError(str(error)), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
