#!/usr/bin/env python3
"""Optional Linux environment setup, shared by CI stage 4 and Linux prepare.

Inputs are successful validated stage outputs. This script never reads the CI
configuration again and never restores or builds the module.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.util
from datetime import datetime, timezone
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
from urllib.error import URLError
from urllib.request import urlopen
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError, _read_json
from ci_pipeline import (PipelineError, contained_path, evaluated_properties, publish_outputs,
                         run_command, solution_projects, target_frameworks, NUGET_CONFIG)


BUILDS_ROOT = Path(__file__).resolve().parents[2]
EXACT_VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
# Official release asset digest, independently checked against GitHub's asset
# metadata. Versions still come exclusively from the executing Builds Props.
DAPR_CLI_DIGESTS = {"1.19.1": "6e9a339d261e430e5914909cc4443572573c197c174c3e14504c34116831de3b"}
DAPR_RECEIPT_ENV = "HEXALITH_DAPR_INITIALIZATION_RECEIPT"
DAPR_RECEIPT_NAME = "hexalith-dapr-initialization.json"


def dapr_job_identity() -> dict[str, str]:
    """Bind a receipt to the actual shared Actions job, independent of its name."""
    fields = {"repository": ("GITHUB_REPOSITORY", r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+"),
              "runId": ("GITHUB_RUN_ID", r"[1-9][0-9]*"),
              "runAttempt": ("GITHUB_RUN_ATTEMPT", r"[1-9][0-9]*"),
              "job": ("GITHUB_JOB", r"[A-Za-z_][A-Za-z0-9_-]*")}
    identity = {}
    for key, (variable, pattern) in fields.items():
        value = os.environ.get(variable, "")
        if re.fullmatch(pattern, value) is None:
            raise PipelineError(f"Dapr initialization requires a valid {variable}")
        identity[key] = value
    return identity


def docker_output(root: Path, arguments: list[str]) -> str:
    """Keep partial inspect output private even when Docker returns failure."""
    operation = ("volume inspection" if arguments[:2] == ["volume", "inspect"] else
                 "container inspection" if arguments[:1] == ["inspect"] else "resource inventory")
    try:
        result = subprocess.run(["docker", *arguments], cwd=root, capture_output=True,
                                text=True, timeout=60, check=False)
    except (OSError, UnicodeError, subprocess.TimeoutExpired) as error:
        # TimeoutExpired may carry partial Config.Env too; never log the error
        # object or either output stream from a failed metadata operation.
        raise PipelineError(f"Dapr initialization Docker {operation} unavailable") from error
    if result.returncode:
        raise PipelineError(f"Dapr initialization Docker {operation} failed ({result.returncode})",
                            result.returncode)
    return result.stdout


def docker_inventory(root: Path, arguments: list[str], pattern: str) -> list[str]:
    """Read canonical unique Docker names/IDs without accepting option injection."""
    values = docker_output(root, arguments).splitlines()
    if (len(values) != len(set(values))
            or any(re.fullmatch(pattern, value) is None for value in values)):
        raise PipelineError("Dapr initialization received a malformed Docker inventory")
    return sorted(values)


def docker_records(root: Path, arguments: list[str]) -> list[dict]:
    """Require a JSON object array; never export raw Docker configuration/secrets."""
    raw = docker_output(root, arguments)
    try:
        records = json.loads(raw)
    except (ValueError, RecursionError) as error:
        raise PipelineError("Dapr initialization received malformed Docker metadata") from error
    if not isinstance(records, list) or any(not isinstance(item, dict) for item in records):
        raise PipelineError("Dapr initialization received malformed Docker metadata")
    return records


def docker_created(value: object) -> str:
    """Require the UTC creation time supplied by Docker, retaining its precision."""
    try:
        if not isinstance(value, str) or re.fullmatch(
                r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:Z|\+00:00)", value) is None:
            raise ValueError("not UTC")
        datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise PipelineError("Dapr initialization received an invalid Docker creation time") from error
    return value


def dapr_created_resources(root: Path, preexisting: list[str]) -> tuple[list[dict], list[dict]]:
    """Record only newly observed containers and their actual mounts/volumes."""
    identifiers = docker_inventory(root, ["ps", "--all", "--quiet", "--no-trunc"], r"[0-9a-f]{64}")
    created = sorted(set(identifiers) - set(preexisting))
    containers, volume_names = [], set()
    records = docker_records(root, ["inspect", *created]) if created else []
    if (len(records) != len(created) or any(not isinstance(record.get("Id"), str) for record in records)
            or sorted(record["Id"] for record in records) != created):
        raise PipelineError("Dapr initialization container identities changed during inspection")
    for record in sorted(records, key=lambda item: item["Id"]):
        name, mounts, state = record.get("Name"), record.get("Mounts"), record.get("State")
        config = record.get("Config")
        image = config.get("Image") if isinstance(config, dict) else None
        if (not isinstance(name, str) or re.fullmatch(r"/[A-Za-z0-9][A-Za-z0-9_.-]*", name) is None
                or not isinstance(image, str) or not image or not isinstance(mounts, list)
                or not isinstance(state, dict) or type(state.get("Running")) is not bool):
            raise PipelineError("Dapr initialization received malformed container metadata")
        selected = []
        for mount in mounts:
            if (not isinstance(mount, dict) or mount.get("Type") not in ("volume", "bind", "tmpfs")
                    or not isinstance(mount.get("Source"), str)
                    or not isinstance(mount.get("Destination"), str) or not mount["Destination"].startswith("/")
                    or type(mount.get("RW")) is not bool):
                raise PipelineError("Dapr initialization received malformed container mounts")
            entry = {key: mount[key] for key in ("Type", "Source", "Destination", "RW")}
            if mount["Type"] == "volume":
                name = mount.get("Name")
                if not isinstance(name, str) or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", name) is None:
                    raise PipelineError("Dapr initialization received an invalid volume identity")
                entry["Name"] = name
                volume_names.add(name)
            selected.append(entry)
        containers.append({"Id": record["Id"], "Name": record["Name"], "Image": image,
                           "Created": docker_created(record.get("Created")), "Running": state["Running"],
                           "Mounts": selected})
    volumes = docker_records(root, ["volume", "inspect", *sorted(volume_names)]) if volume_names else []
    if (len(volumes) != len(volume_names)
            or any(not isinstance(record.get("Name"), str) for record in volumes)
            or sorted(record["Name"] for record in volumes) != sorted(volume_names)):
        raise PipelineError("Dapr initialization volume identities changed during inspection")
    selected_volumes = []
    for record in sorted(volumes, key=lambda item: item["Name"]):
        if any(not isinstance(record.get(key), str) or not record[key] for key in ("Driver", "Scope", "Mountpoint")):
            raise PipelineError("Dapr initialization received malformed volume metadata")
        selected_volumes.append({**{key: record[key] for key in ("Name", "Driver", "Scope", "Mountpoint")},
                                 "CreatedAt": docker_created(record.get("CreatedAt"))})
    return containers, selected_volumes


def initialize_dapr(binary: Path, runtime: str, root: Path) -> Path:
    """Create evidence exclusively before init; publish its path only on success.

    A retained, stale, malformed or symlink receipt is never overwritten. A
    failed initialization/inspection leaves an unpublished initializing receipt,
    so callers cannot mistake partial evidence for fresh backend ownership.
    """
    if os.environ.get(DAPR_RECEIPT_ENV):
        raise PipelineError("Dapr initialization receipt is already supplied; refusing reuse")
    identity = dapr_job_identity()
    temporary = Path(os.environ.get("RUNNER_TEMP", ""))
    if (not temporary.is_absolute() or not temporary.is_dir()
            or temporary.resolve() != temporary
            or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in str(temporary))):
        raise PipelineError("Dapr initialization requires a real absolute RUNNER_TEMP directory")
    receipt = temporary / DAPR_RECEIPT_NAME
    try:
        descriptor = os.open(receipt, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    except OSError as error:
        raise PipelineError("cannot exclusively create Dapr initialization receipt; refusing reuse") from error
    with os.fdopen(descriptor, "w+", encoding="utf-8") as stream:
        volumes = docker_inventory(root, ["volume", "ls", "--format", "{{.Name}}"], r"[A-Za-z0-9][A-Za-z0-9_.-]*")
        containers = docker_inventory(root, ["ps", "--all", "--quiet", "--no-trunc"], r"[0-9a-f]{64}")
        document = {"schema": "hexalith.dapr-initialization.v1", "status": "initializing",
                    "identity": identity, "runtimeVersion": runtime,
                    "startedAt": datetime.now(timezone.utc).isoformat(),
                    "preexistingVolumes": volumes, "preexistingContainerIds": containers}
        stream.write(json.dumps(document, sort_keys=True) + "\n")
        stream.flush()
        os.fsync(stream.fileno())
        run_command([str(binary), "init", "--runtime-version", runtime], root)
        created_containers, mounted_volumes = dapr_created_resources(root, containers)
        document.update(status="complete", completedAt=datetime.now(timezone.utc).isoformat(),
                        createdContainers=created_containers, mountedVolumes=mounted_volumes)
        stream.seek(0)
        stream.write(json.dumps(document, sort_keys=True) + "\n")
        stream.truncate()
        stream.flush()
        os.fsync(stream.fileno())
    if os.environ.get("GITHUB_ENV"):
        with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8") as stream:
            stream.write(f"{DAPR_RECEIPT_ENV}={receipt}\n")
    os.environ[DAPR_RECEIPT_ENV] = str(receipt)
    return receipt


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
        native_command(["env", f"ASPIRE_LAYOUT_PATH={target}", str(binary), "setup", "--install-path", str(target),
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


def download_dapr_cli(directory: Path) -> tuple[Path, str]:
    """Install the same digest-verified CLI for CI or isolated local qualification."""
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
    return install_dapr_cli(archive, digest, directory), runtime


def setup_dapr(root: Path) -> None:
    """Install the verified pinned CLI and initialize its pinned runtime once."""
    directory = Path(tempfile.mkdtemp(prefix="hexalith-ci-dapr-", dir=os.environ.get("RUNNER_TEMP")))
    binary, runtime = download_dapr_cli(directory)
    # Subsequent hook actions and integration tests use this exact executable.
    export_path(directory)
    initialize_dapr(binary, runtime, root)


def additional_project_paths(root: Path, value: object) -> tuple[str, ...]:
    """Validate explicitly built projects before evaluating any browser tooling."""
    if (not isinstance(value, (list, tuple)) or any(not isinstance(item, str) for item in value)
            or len(value) != len(set(value))):
        raise PipelineError("additional-projects must be a unique array of project paths")
    for relative in value:
        path = contained_path(root, relative, kind="additional browser project")
        if not relative.endswith(".csproj") or not path.is_file():
            raise PipelineError("additional browser project must be an existing .csproj file")
        current = root
        for component in relative.split("/"):
            current /= component
            if current.is_symlink():
                raise PipelineError("additional browser project must not traverse a symbolic link")
    return tuple(value)


def playwright_tools(root: Path, solution: str, additional_projects: tuple[str, ...] = ()) -> tuple[Path, ...]:
    """Select built tooling from evaluated outputs, once per package version."""
    tools = {}
    additional = additional_project_paths(root, additional_projects)
    projects = tuple(dict.fromkeys((*solution_projects(root, solution), *additional)))
    for project in projects:
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


def validate_prerequisites(value: object) -> dict:
    """Validate action-boundary data independently of the delivered configuration."""
    if not isinstance(value, dict) or value.keys() - {
            "nodeVersion", "npmVersion", "apt", "tlsTrust", "daprProfile"}:
        raise PipelineError("prerequisites must contain only supported shared setup settings")
    for flag in ("apt", "tlsTrust"):
        if flag in value and type(value[flag]) is not bool:
            raise PipelineError(f"prerequisites.{flag} must be a boolean")
    if ("nodeVersion" in value) != ("npmVersion" in value):
        raise PipelineError("prerequisites requires nodeVersion and npmVersion together")
    for name in ("nodeVersion", "npmVersion"):
        if name in value and (not isinstance(value[name], str) or not EXACT_VERSION.fullmatch(value[name])):
            raise PipelineError(f"prerequisites.{name} requires an exact numeric version")
    if value.get("daprProfile", "catalog") != "catalog":
        raise PipelineError("prerequisites.daprProfile is not an admitted Builds profile")
    return value


def setup_apt(root: Path) -> None:
    """Reuse the authenticated Builds Ubuntu mirror preparation, without module code."""
    run_command(["bash", str(BUILDS_ROOT / "Github/initialize-apt/initialize-apt.sh")], root)
    use_https_ubuntu_sources(root, (Path("/etc/apt/sources.list"), Path("/etc/apt/apt-mirrors.txt"),
                                  *sorted(Path("/etc/apt/sources.list.d").glob("*.list")),
                                  *sorted(Path("/etc/apt/sources.list.d").glob("*.sources"))))


def use_https_ubuntu_sources(root: Path, sources: tuple[Path, ...]) -> None:
    """Change only the canonical Azure archive transport, retaining signed policy."""
    # Preserve signed suites, components and keys while avoiding the hosted
    # Azure HTTP archive's stalled browser/media package downloads.
    for source in sources:
        if not source.is_file():
            continue
        if source.is_symlink():
            raise PipelineError("Ubuntu browser package source must be a regular file")
        try:
            contents = source.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as error:
            raise PipelineError("cannot inspect Ubuntu browser package source") from error
        if "azure.archive.ubuntu.com/ubuntu" in contents:
            run_command(["sudo", "sed", "-i", "-e",
                         r"s|http://azure\.archive\.ubuntu\.com/ubuntu|https://archive.ubuntu.com/ubuntu|g",
                         "-e", r"s|https://azure\.archive\.ubuntu\.com/ubuntu|https://archive.ubuntu.com/ubuntu|g",
                         str(source)], root)


def setup_tls_trust(root: Path) -> None:
    """Trust the job's .NET development certificate in both supported NSS databases."""
    inherited_certificates = os.environ.get("SSL_CERT_DIR", "")
    if any(ord(char) < 32 or 127 <= ord(char) <= 159 or char in "\u2028\u2029"
           for char in inherited_certificates):
        raise PipelineError("SSL_CERT_DIR must not contain control characters")
    run_command(["sudo", "apt-get", "-o", "APT::Update::Error-Mode=any", "update"], root)
    run_command(["sudo", "apt-get", "install", "-y", "--no-install-recommends", "libnss3-tools"], root)
    directories = [Path.home() / relative for relative in (".pki/nssdb", ".local/share/pki/nssdb")]
    trust = Path.home() / ".aspnet/dev-certs/trust"
    if any(path.is_symlink() for path in (trust, *trust.parents)):
        raise PipelineError("TLS certificate export must not traverse a symbolic link")
    for directory in directories:
        if any(path.is_symlink() for path in (directory, *directory.parents)):
            raise PipelineError("TLS trust database must not traverse a symbolic link")
        directory.mkdir(parents=True, exist_ok=True)
        if not (directory / "cert9.db").exists():
            run_command(["certutil", "-N", "-d", f"sql:{directory}", "--empty-password"], root)
    certificate_directories = os.pathsep.join(dict.fromkeys(
        (str(trust), "/usr/lib/ssl/certs", *filter(None, inherited_certificates.split(os.pathsep)))))
    if any(ord(char) < 32 or 127 <= ord(char) <= 159 or char in "\u2028\u2029"
           for char in certificate_directories):
        raise PipelineError("TLS certificate paths must not contain control characters")
    # The CLI requires this path during --trust to report full OpenSSL trust.
    # Publish it to later Actions steps only after every trust check succeeds.
    previous_certificates = os.environ.get("SSL_CERT_DIR")
    os.environ["SSL_CERT_DIR"] = certificate_directories
    try:
        run_command(["dotnet", "dev-certs", "https", "--trust"], root)
        certificates = sorted(trust.glob("*.pem"))
        if not certificates or any(path.is_symlink() or not path.is_file() or path.stat().st_size == 0
                                   for path in certificates):
            raise PipelineError("TLS trust requires the exported .NET development certificate PEM files")
        for certificate in certificates:
            run_command(["openssl", "x509", "-in", str(certificate), "-checkend", "0", "-noout"], root)
            for directory in directories:
                # The CLI uses the exported PEM basename as its NSS nickname.
                # NSS keeps that nickname when the same certificate is reimported.
                name = certificate.stem
                run_command(["certutil", "-A", "-d", f"sql:{directory}", "-n", name,
                             "-t", "P,,", "-i", str(certificate)], root)
                run_command(["certutil", "-L", "-d", f"sql:{directory}", "-n", name], root, capture=True)
        run_command(["dotnet", "dev-certs", "https", "--check", "--trust"], root)
        if os.environ.get("GITHUB_ENV"):
            with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8") as stream:
                stream.write(f"SSL_CERT_DIR={certificate_directories}\n")
    except BaseException:
        if previous_certificates is None:
            os.environ.pop("SSL_CERT_DIR", None)
        else:
            os.environ["SSL_CERT_DIR"] = previous_certificates
        raise


def require_node_versions(root: Path, prerequisites: dict, *, npm_required: bool) -> None:
    """Keep selected Node/npm pins exact; never silently accept a newer bundled npm."""
    if "nodeVersion" in prerequisites:
        node = run_command(["node", "--version"], root, capture=True).strip()
        npm = run_command(["npm", "--version"], root, capture=True).strip()
        if node != "v" + prerequisites["nodeVersion"] or npm != prerequisites["npmVersion"]:
            raise PipelineError("Node/npm runtime differs from the exact shared prerequisite pins")
    elif npm_required and re.fullmatch(r"v24\.[0-9]+\.[0-9]+\s*", run_command(
            ["node", "--version"], root, capture=True)) is None:
        raise PipelineError("npm preparation requires the trusted Node 24 runtime")


def setup_environment(workspace: Path, *, solution: str, module_root: str = "",
                      dapr: bool = False, browsers: bool = False, registry: bool = False,
                      npm_workspaces: list[str] | None = None, dotnet_tools: bool = False,
                      aspire: bool = False, prerequisites: dict | None = None,
                      additional_projects: list[str] | None = None) -> None:
    """Perform each enabled setup before hooks, with no disabled tool calls."""
    npm_workspaces = [] if npm_workspaces is None else npm_workspaces
    additional_projects = [] if additional_projects is None else additional_projects
    if not isinstance(additional_projects, list):
        raise PipelineError("additional-projects must be a JSON array")
    prerequisites = validate_prerequisites({} if prerequisites is None else prerequisites)
    if solution == "none":
        if npm_workspaces or dotnet_tools or prerequisites or additional_projects:
            raise PipelineError("npmWorkspaces, dotnetTools, prerequisites and additional projects must be disabled when solution is none")
        return
    if additional_projects:
        module = contained_path(workspace.resolve(), module_root, kind="module-root") if module_root else workspace.resolve()
        additional_project_paths(module, additional_projects)
    if not any((dapr, browsers, registry, npm_workspaces, dotnet_tools, aspire, prerequisites)):
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
    directories, manifest = preparation_inputs(root, npm_workspaces, dotnet_tools)
    require_node_versions(root, prerequisites, npm_required=bool(directories))
    if prerequisites.get("apt") or prerequisites.get("tlsTrust"):
        setup_apt(root)
    if prerequisites.get("tlsTrust"):
        setup_tls_trust(root)
    if directories:
        for directory in directories:
            run_command(["npm", "ci", "--ignore-scripts"], directory)
            run_command(["npm", "audit", "signatures"], directory)
    if manifest:
        run_command(["dotnet", "tool", "restore", "--tool-manifest", str(manifest),
                     "--configfile", str(NUGET_CONFIG)], root)
    if dapr:
        setup_dapr(root)
    if aspire:
        setup_aspire(root)
    if browsers:
        dotnet_browsers = playwright_tools(root, solution_path.relative_to(root).as_posix(), tuple(additional_projects))
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
    parser.add_argument("--phase", choices=("inputs", "setup"), default="setup")
    parser.add_argument("--solution", default=os.environ.get("CI_ENVIRONMENT_SOLUTION"), required=not os.environ.get("CI_ENVIRONMENT_SOLUTION"))
    for name in ("dapr", "browsers", "registry", "aspire"):
        parser.add_argument(f"--{name}", type=boolean, default=os.environ.get(f"CI_ENVIRONMENT_{name.upper()}", "false"))
    parser.add_argument("--dotnet-tools", type=boolean, default=os.environ.get("CI_ENVIRONMENT_DOTNET_TOOLS", "false"))
    parser.add_argument("--npm-workspaces", default=os.environ.get("CI_ENVIRONMENT_NPM_WORKSPACES", "[]"))
    parser.add_argument("--prerequisites", default=os.environ.get("CI_ENVIRONMENT_PREREQUISITES", "{}"))
    parser.add_argument("--additional-projects", default=os.environ.get("CI_ENVIRONMENT_ADDITIONAL_PROJECTS", "[]"))
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise PipelineError("Python 3.12 or later is required")
        prerequisites = validate_prerequisites(json.loads(args.prerequisites))
        workspaces = json.loads(args.npm_workspaces)
        if not isinstance(workspaces, list):
            raise PipelineError("npmWorkspaces must be a JSON array")
        additional = json.loads(args.additional_projects)
        if not isinstance(additional, list):
            raise PipelineError("additional-projects must be a JSON array")
        if additional:
            if args.solution == "none":
                raise PipelineError("additional projects must be disabled when solution is none")
            workspace = args.workspace.resolve()
            root = contained_path(workspace, args.module_root, kind="module-root") if args.module_root else workspace
            additional_project_paths(root, additional)
        if args.phase == "inputs":
            if args.solution == "none" and prerequisites:
                raise PipelineError("prerequisites must be disabled when solution is none")
            if "nodeVersion" in prerequisites:
                if args.registry and not all(os.environ.get(name) for name in (
                        "REGISTRY_PULL_USERNAME", "REGISTRY_PULL_PASSWORD")):
                    raise PipelineError("registry setup requires the dedicated pull-only username and password")
                if args.aspire and not args.dapr:
                    raise PipelineError("native Aspire setup requires dapr=true")
                workspace = args.workspace.resolve()
                root = contained_path(workspace, args.module_root, kind="module-root") if args.module_root else workspace
                solution = contained_path(workspace, args.solution, kind="solution")
                if not root.is_dir() or not solution.is_file() or not solution.is_relative_to(root):
                    raise PipelineError("environment solution must exist within the module root")
                preparation_inputs(root, workspaces, args.dotnet_tools)
            publish_outputs({"node-version": prerequisites.get("nodeVersion", "")})
            return 0
        setup_environment(args.workspace, solution=args.solution, module_root=args.module_root,
                          dapr=args.dapr, browsers=args.browsers, registry=args.registry, aspire=args.aspire,
                          npm_workspaces=workspaces, dotnet_tools=args.dotnet_tools, prerequisites=prerequisites,
                          additional_projects=additional)
        return 0
    except (ConfigError, ValueError) as error:
        print(error, file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    raise SystemExit(main())
