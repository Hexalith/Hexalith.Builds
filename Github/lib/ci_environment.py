#!/usr/bin/env python3
"""Optional Linux environment setup, shared by CI stage 4 and Linux prepare.

Inputs are successful validated stage outputs. This script never reads the CI
configuration again and never restores or builds the module.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.util
import hashlib
import io
import os
from pathlib import Path
import platform
import re
import shutil
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
                         solution_projects, target_frameworks)


BUILDS_ROOT = Path(__file__).resolve().parents[2]
# Official release asset digest, independently checked against GitHub's asset
# metadata. Versions still come exclusively from the executing Builds Props.
DAPR_CLI_DIGESTS = {"1.19.1": "6e9a339d261e430e5914909cc4443572573c197c174c3e14504c34116831de3b"}
# Trusted native toolchain. A module may enable it but cannot choose executable
# URLs, checksums or dependencies. The CLI embeds the DCP/dashboard bundle.
ASPIRE_VERSION = "13.6.5"
ASPIRE_URL = f"https://github.com/microsoft/aspire/releases/download/v{ASPIRE_VERSION}/aspire-cli-linux-x64-{ASPIRE_VERSION}.tar.gz"
ASPIRE_ARCHIVE_SHA256 = "6e22bd4daaa62d09320638246e57bfaf56ed147a3a87cdd5eafec277726ca978"
ASPIRE_BINARY_SHA256 = "7a9d1c1e264c645d7d78e2891ef53a865fa51d96adf2d827fed1e9cc3cb1e253"
NATIVE_MANAGED_RUNTIME = "11.0.0"
NATIVE_PYTHON_REQUIREMENTS = BUILDS_ROOT / "Github/ci-environment/requirements.txt"


def export_path(directory: Path) -> None:
    """Expose this exact installed tool to this process and later hook steps."""
    os.environ["PATH"] = str(directory) + os.pathsep + os.environ.get("PATH", "")
    if os.environ.get("GITHUB_PATH"):
        with Path(os.environ["GITHUB_PATH"]).open("a", encoding="utf-8") as stream:
            stream.write(f"{directory}\n")


def native_command(arguments: list[str], root: Path, seconds: int, *, capture: bool = False) -> str:
    """Bound setup processes and their TERM-resistant children on Linux."""
    return run_command(["timeout", "--signal=TERM", "--kill-after=30s", str(seconds),
                        *arguments], root, capture=capture)


def native_prerequisites(root: Path) -> None:
    """Fail closed on missing native tools and prove private namespace support."""
    for tool in ("timeout", "git", "dotnet", "docker", "unshare", "ip", "curl", "bash"):
        if not shutil.which(tool):
            raise PipelineError(f"native Aspire setup requires {tool}")
    for library in ("libssl.so.3", "libcrypto.so.3"):
        try:
            ctypes.CDLL(library)
        except OSError as error:
            raise PipelineError(f"native Aspire setup requires {library}") from error
    for library in ("z", "zstd"):
        if not ctypes.util.find_library(library):
            raise PipelineError(f"native Aspire setup requires {library}")
    inventory = native_command(["dotnet", "--list-runtimes"], root, 30, capture=True)
    for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        versions = [tuple(map(int, parts[1].split("."))) for line in inventory.splitlines()
                    if len(parts := line.split()) >= 3 and parts[0] == framework
                    and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", parts[1])]
        if not versions or max(versions) != tuple(map(int, NATIVE_MANAGED_RUNTIME.split("."))):
            raise PipelineError(f"native Aspire setup requires highest installed {framework} {NATIVE_MANAGED_RUNTIME}")
    native_command(["docker", "info", "--format", "{{.ServerVersion}}"], root, 30)
    probe = ["unshare", "--user", "--map-root-user", "--net", "--pid",
             "--mount", "--fork", "--mount-proc", "true"]
    try:
        native_command(probe, root, 30)
    except PipelineError:
        if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
            raise
        # This is permitted only on the disposable GitHub-hosted VM. Product
        # launchers still prove each child's private namespaces and cleanup.
        native_command(["sudo", "-n", "sysctl", "-w",
                        "kernel.apparmor_restrict_unprivileged_userns=0"], root, 30)
        native_command(probe, root, 30)


def install_aspire_cli(archive: bytes, directory: Path) -> Path:
    """Authenticate both release archive and executable before running it."""
    if hashlib.sha256(archive).hexdigest() != ASPIRE_ARCHIVE_SHA256:
        raise PipelineError("Aspire CLI archive SHA-256 mismatch")
    try:
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as bundle:
            bundle.extractall(directory, filter="data")
    except (OSError, tarfile.TarError) as error:
        raise PipelineError(f"invalid Aspire CLI archive: {error}") from error
    binary = directory / "aspire"
    if (not binary.is_file() or binary.is_symlink()
            or hashlib.sha256(binary.read_bytes()).hexdigest() != ASPIRE_BINARY_SHA256):
        raise PipelineError("Aspire CLI executable SHA-256 mismatch")
    binary.chmod(0o755)
    return binary


def verify_aspire_bundle(expected: Path, installed: Path) -> None:
    """Compare the complete embedded bundle, including DCP and dashboard bytes."""
    def manifest(directory: Path) -> dict[str, str]:
        files = {}
        for path in directory.rglob("*"):
            if path.is_symlink():
                raise PipelineError("Aspire embedded bundle contains a symbolic link")
            if path.is_file():
                files[path.relative_to(directory).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
        return files
    hashes = manifest(expected)
    if not {"dcp/dcp", "dashboard/Aspire.Dashboard"} <= hashes.keys() or manifest(installed) != hashes:
        raise PipelineError("Aspire DCP/dashboard bundle differs from the authenticated CLI bundle")


def setup_aspire(root: Path) -> None:
    """Prepare reusable native tools without executing module-owned setup code."""
    native_prerequisites(root)
    directory = Path(tempfile.mkdtemp(prefix="hexalith-ci-aspire-", dir=os.environ.get("RUNNER_TEMP")))
    try:
        with urlopen(ASPIRE_URL, timeout=120) as response:
            archive = response.read()
    except (OSError, URLError) as error:
        raise PipelineError(f"cannot download Aspire CLI {ASPIRE_VERSION}: {error}") from error
    binary = install_aspire_cli(archive, directory / "cli")
    export_path(binary.parent)
    expected = directory / "qualified"
    installed = Path.home() / ".aspire"
    for target in (expected, installed):
        native_command([str(binary), "setup", "--install-path", str(target),
                        "--force", "--non-interactive"], root, 180)
    verify_aspire_bundle(expected / "bundle", installed / "bundle")
    venv = directory / "venv"
    native_command([sys.executable, "-m", "venv", str(venv)], root, 60)
    python = venv / "bin/python"
    native_command([str(python), "-m", "pip", "install", "--disable-pip-version-check",
                    "--require-hashes", "--only-binary=:all:", "-r", str(NATIVE_PYTHON_REQUIREMENTS)], root, 180)
    export_path(python.parent)
    if os.environ.get("GITHUB_ENV"):
        with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8") as stream:
            stream.write(f"HEXALITH_CI_PYTHON={python}\n")
            stream.write(f"HEXALITH_CI_ASPIRE_QUALIFIED_BUNDLE={expected / 'bundle'}\n")


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
    export_path(directory)
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
    if not tools:
        raise PipelineError("browsers requested but no built Playwright tooling exists in evaluated solution outputs")
    return tuple(tools.values())


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
                      aspire: bool = False) -> None:
    """Perform each enabled setup before hooks, with no disabled tool calls."""
    if solution == "none" or not any((dapr, browsers, registry, aspire)):
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
    if aspire and not dapr:
        raise PipelineError("native Aspire setup requires dapr=true")
    if dapr:
        setup_dapr(root)
    if aspire:
        setup_aspire(root)
    if browsers:
        for script in playwright_tools(root, solution_path.relative_to(root).as_posix()):
            run_command(["pwsh", "-NoLogo", "-NoProfile", "-File", str(script), "install", "--with-deps"], root)
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
    for name in ("dapr", "browsers", "registry", "aspire"):
        parser.add_argument(f"--{name}", type=boolean, default=os.environ.get(f"CI_ENVIRONMENT_{name.upper()}", "false"))
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise PipelineError("Python 3.12 or later is required")
        setup_environment(args.workspace, solution=args.solution, module_root=args.module_root,
                          dapr=args.dapr, browsers=args.browsers, registry=args.registry, aspire=args.aspire)
        return 0
    except ConfigError as error:
        print(error, file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    raise SystemExit(main())
