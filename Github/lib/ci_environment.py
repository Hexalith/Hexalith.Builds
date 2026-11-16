#!/usr/bin/env python3
"""Optional Linux environment setup, shared by CI stage 4 and Linux prepare.

Inputs are successful validated stage outputs. This script never reads the CI
configuration again and never restores or builds the module.
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
import subprocess
import sys
import tarfile
import tempfile
from urllib.error import URLError
from urllib.request import urlopen
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError, _read_json
from ci_pipeline import (PipelineError, contained_path, evaluated_properties, run_command,
                         solution_projects, target_frameworks, NUGET_CONFIG)


BUILDS_ROOT = Path(__file__).resolve().parents[2]
# Official release asset digest, independently checked against GitHub's asset
# metadata. Versions still come exclusively from the executing Builds Props.
DAPR_CLI_DIGESTS = {"1.19.1": "6e9a339d261e430e5914909cc4443572573c197c174c3e14504c34116831de3b"}


def dapr_versions(builds_root: Path = BUILDS_ROOT) -> tuple[str, str]:
    """Read the single runtime/CLI pins from workflow-owned Props, never a gitlink."""
    try:
        tree = ET.parse(builds_root / "Props/Directory.Packages.props")
    except (OSError, ET.ParseError) as error:
        raise PipelineError(f"cannot read trusted Dapr pins: {error}") from error
    versions = []
    for name in ("HexalithDaprRuntimeVersion", "HexalithDaprCliVersion"):
        elements = tree.findall(f".//{name}")
        if (len(elements) != 1 or elements[0].attrib
                or re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", elements[0].text or "") is None):
            raise PipelineError(f"trusted Props must contain one literal {name}")
        versions.append(elements[0].text)
    return tuple(versions)


def install_dapr_cli(archive: bytes, expected_digest: str, directory: Path) -> Path:
    """Verify before extracting one regular executable; never extract archive paths."""
    if hashlib.sha256(archive).hexdigest() != expected_digest:
        raise PipelineError("Dapr CLI archive SHA-256 mismatch")
    try:
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as bundle:
            members = bundle.getmembers()
            if len(members) != 1 or members[0].name != "dapr" or not members[0].isfile():
                raise PipelineError("Dapr CLI archive must contain only the regular dapr executable")
            with bundle.extractfile(members[0]) as stream:
                executable = stream.read()
    except (tarfile.TarError, OSError) as error:
        raise PipelineError(f"invalid Dapr CLI archive: {error}") from error
    directory.mkdir(parents=True, exist_ok=True)
    binary = directory / "dapr"
    binary.write_bytes(executable)
    binary.chmod(0o755)
    return binary


def setup_dapr(root: Path) -> None:
    """Install the verified pinned CLI and initialize its pinned runtime once."""
    runtime, cli = dapr_versions()
    digest = DAPR_CLI_DIGESTS.get(cli)
    if digest is None:
        raise PipelineError(f"Dapr CLI {cli} has no trusted archive digest")
    url = f"https://github.com/dapr/cli/releases/download/v{cli}/dapr_linux_amd64.tar.gz"
    try:
        with urlopen(url, timeout=120) as response:
            archive = response.read()
    except (OSError, URLError) as error:
        raise PipelineError(f"cannot download Dapr CLI {cli}: {error}") from error
    directory = Path(tempfile.mkdtemp(prefix="hexalith-ci-dapr-", dir=os.environ.get("RUNNER_TEMP")))
    binary = install_dapr_cli(archive, digest, directory)
    # Subsequent hook actions and integration tests use this exact executable.
    if os.environ.get("GITHUB_PATH"):
        with Path(os.environ["GITHUB_PATH"]).open("a", encoding="utf-8") as stream:
            stream.write(f"{directory}\n")
    run_command([str(binary), "init", "--runtime-version", runtime], root)


def playwright_tools(root: Path, solution: str) -> tuple[Path, ...]:
    """Select built tooling from evaluated outputs, once per package version."""
    tools = {}
    for project in solution_projects(root, solution):
        outer = evaluated_properties(root, project)
        targets = [evaluated_properties(root, project, framework=framework)
                   for framework in target_frameworks(outer)] or [outer]
        for properties in targets:
            if not properties["TargetPath"]:
                continue
            assembly = Path(properties["TargetPath"])
            if not assembly.is_absolute():
                assembly = root / assembly
            output = assembly.parent.resolve()
            script = output / "playwright.ps1"
            package = output / ".playwright/package/package.json"
            library = output / "Microsoft.Playwright.dll"
            if not any(path.exists() or path.is_symlink() for path in (script, library, package)):
                continue
            source = (root / project).resolve().parent
            if (not output.is_relative_to(root.resolve()) or source.is_relative_to(output)
                    or not script.resolve().is_relative_to(output)):
                raise PipelineError(f"{project}: Playwright tooling is not a contained build output")
            if (not script.is_file() or not library.is_file() or not package.is_file()
                    or not library.resolve().is_relative_to(output)
                    or not package.resolve().is_relative_to(output)):
                raise PipelineError(f"{project}: built Playwright tooling is incomplete")
            metadata = _read_json(package)
            version = metadata.get("version") if isinstance(metadata, dict) else None
            if not isinstance(version, str) or re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version) is None:
                raise PipelineError(f"{project}: built Playwright tooling has no valid package version")
            tools.setdefault(version, script)
    return tuple(tools.values())


def committed_file(root: Path, relative: str) -> Path:
    """Require a regular contained tracked input, rejecting every symlink component."""
    path = contained_path(root, relative, kind="preparation input")
    current = root
    for component in relative.split("/"):
        current /= component
        if current.is_symlink():
            raise PipelineError(f"unsafe symlink preparation input: {relative}")
    if not path.is_file():
        raise PipelineError(f"missing preparation input: {relative}")
    run_command(["git", "ls-files", "--error-unmatch", "--", ":(literal)" + relative], root, capture=True)
    if run_command(["git", "cat-file", "-t", "HEAD:" + relative], root, capture=True).strip() != "blob":
        raise PipelineError(f"preparation input is not a committed blob: {relative}")
    return path


def preparation_inputs(root: Path, workspaces: list[str], tools: bool) -> tuple[tuple[Path, ...], Path | None]:
    """Validate all manifests before any installations, including exact tool versions."""
    if (not isinstance(workspaces, list) or any(not isinstance(item, str) for item in workspaces)
            or len(workspaces) != len(set(workspaces))):
        raise PipelineError("npmWorkspaces must be a unique array of contained directories")
    directories = []
    for relative in workspaces:
        directory = contained_path(root, relative, kind="npm workspace")
        package = _read_json(committed_file(root, relative + "/package.json"))
        lock = _read_json(committed_file(root, relative + "/package-lock.json"))
        if (not isinstance(package, dict) or not isinstance(lock, dict)
                or type(lock.get("lockfileVersion")) is not int or lock.get("lockfileVersion") not in (2, 3) or not isinstance(lock.get("packages"), dict)):
            raise PipelineError(f"{relative}: expected an npm package manifest and version 2/3 lock")
        directories.append(directory)
    manifest = None
    if tools:
        manifest = committed_file(root, ".config/dotnet-tools.json")
        document = _read_json(manifest)
        entries = document.get("tools") if isinstance(document, dict) else None
        if (not isinstance(document, dict) or document.get("version") != 1
                or document.get("isRoot") is not True or not isinstance(entries, dict) or not entries):
            raise PipelineError("dotnetTools requires a version 1 root manifest with pinned tools")
        for name, entry in entries.items():
            if (not re.fullmatch(r"[A-Za-z0-9_.-]+", name) or not isinstance(entry, dict)
                    or not isinstance(entry.get("version"), str)
                    or re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?", entry["version"]) is None
                    or not isinstance(entry.get("commands"), list) or not entry["commands"]
                    or any(not isinstance(command, str) or re.fullmatch(r"[A-Za-z0-9_.-]+", command) is None
                           for command in entry["commands"])):
                raise PipelineError(f"{name}: .NET tools require exact versions and literal commands")
    return tuple(directories), manifest


def npm_playwright(directory: Path) -> Path | None:
    """Select only the locked, installed Playwright CLI, never npx resolution."""
    lock = _read_json(directory / "package-lock.json")
    packages = lock["packages"]
    key = next((key for key in ("node_modules/@playwright/test", "node_modules/playwright")
                if key in packages), None)
    if key is None:
        return None
    version = packages[key].get("version")
    metadata = directory / key / "package.json"
    cli = directory / key / "cli.js"
    if (not isinstance(version, str) or not metadata.is_file() or not cli.is_file()
            or not cli.resolve().is_relative_to(directory.resolve())
            or not metadata.resolve().is_relative_to(directory.resolve())
            or _read_json(metadata).get("version") != version):
        raise PipelineError(f"{directory}: locked installed Playwright tooling is missing or mismatched")
    return cli


def login_registry(root: Path) -> None:
    """Pass only the dedicated pull password on stdin and keep it out of logs."""
    username = os.environ.get("REGISTRY_PULL_USERNAME", "")
    password = os.environ.get("REGISTRY_PULL_PASSWORD", "")
    if not username or not password:
        raise PipelineError("registry setup requires the dedicated pull-only username and password")
    try:
        result = subprocess.run(["docker", "login", "registry.hexalith.com", f"--username={username}",
                                 "--password-stdin"], cwd=root, input=password, text=True, check=False)
    except OSError as error:
        raise PipelineError(f"cannot execute docker registry login: {error}") from error
    if result.returncode:
        raise PipelineError("pull-only registry login failed", result.returncode)


def setup_environment(workspace: Path, *, solution: str, module_root: str = "",
                      dapr: bool = False, browsers: bool = False, registry: bool = False,
                      npm_workspaces: list[str] | None = None, dotnet_tools: bool = False) -> None:
    """Perform each enabled setup before hooks, with no disabled tool calls."""
    npm_workspaces = [] if npm_workspaces is None else npm_workspaces
    if solution == "none":
        if npm_workspaces or dotnet_tools:
            raise PipelineError("npmWorkspaces and dotnetTools must be disabled when solution is none")
        return
    if not any((dapr, browsers, registry, npm_workspaces, dotnet_tools)):
        return
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        raise PipelineError("shared environment setup requires Linux x86_64")
    workspace = workspace.resolve()
    root = contained_path(workspace, module_root, kind="module-root") if module_root else workspace
    solution_path = contained_path(workspace, solution, kind="solution")
    if not root.is_dir() or not solution_path.is_file() or not solution_path.is_relative_to(root):
        raise PipelineError("environment solution must exist within the module root")
    # Missing credentials fail before any tool installation or downloads.
    if registry and not all(os.environ.get(name) for name in ("REGISTRY_PULL_USERNAME", "REGISTRY_PULL_PASSWORD")):
        raise PipelineError("registry setup requires the dedicated pull-only username and password")
    directories, manifest = preparation_inputs(root, npm_workspaces, dotnet_tools)
    if directories:
        if re.fullmatch(r"v24\.[0-9]+\.[0-9]+\s*", run_command(["node", "--version"], root, capture=True)) is None:
            raise PipelineError("npm preparation requires the trusted Node 24 runtime")
        for directory in directories:
            run_command(["npm", "ci", "--ignore-scripts"], directory)
            run_command(["npm", "audit", "signatures"], directory)
    if manifest:
        run_command(["dotnet", "tool", "restore", "--tool-manifest", str(manifest),
                     "--configfile", str(NUGET_CONFIG)], root)
    if dapr:
        setup_dapr(root)
    if browsers:
        dotnet_browsers = playwright_tools(root, solution_path.relative_to(root).as_posix())
        npm_browsers = [(directory, npm_playwright(directory)) for directory in directories]
        if not dotnet_browsers and not any(cli for _, cli in npm_browsers):
            raise PipelineError("browsers requested but no built Playwright tooling or locked npm tooling exists")
        for script in dotnet_browsers:
            run_command(["pwsh", "-NoLogo", "-NoProfile", "-File", str(script), "install", "--with-deps"], root)
        for directory, cli in npm_browsers:
            if cli:
                run_command(["node", str(cli), "install", "--with-deps"], directory)
    if registry:
        login_registry(root)


def boolean(value: str) -> bool:
    """Require literal validated booleans instead of shell truthiness."""
    if value not in ("true", "false"):
        raise argparse.ArgumentTypeError("expected true or false")
    return value == "true"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path, default=os.environ.get("GITHUB_WORKSPACE", "."))
    parser.add_argument("--module-root", default=os.environ.get("CI_MODULE_ROOT", ""))
    parser.add_argument("--solution", default=os.environ.get("CI_ENVIRONMENT_SOLUTION"), required=not os.environ.get("CI_ENVIRONMENT_SOLUTION"))
    for name in ("dapr", "browsers", "registry"):
        parser.add_argument(f"--{name}", type=boolean, default=os.environ.get(f"CI_ENVIRONMENT_{name.upper()}", "false"))
    parser.add_argument("--dotnet-tools", type=boolean, default=os.environ.get("CI_ENVIRONMENT_DOTNET_TOOLS", "false"))
    parser.add_argument("--npm-workspaces", default=os.environ.get("CI_ENVIRONMENT_NPM_WORKSPACES", "[]"))
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise PipelineError("Python 3.12 or later is required")
        setup_environment(args.workspace, solution=args.solution, module_root=args.module_root,
                          dapr=args.dapr, browsers=args.browsers, registry=args.registry,
                          npm_workspaces=json.loads(args.npm_workspaces), dotnet_tools=args.dotnet_tools)
        return 0
    except (ConfigError, ValueError) as error:
        print(error, file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    raise SystemExit(main())
