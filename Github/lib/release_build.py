#!/usr/bin/env python3
"""Produce a frozen, versioned, audited release payload without publishing it."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_pipeline import (NUGET_CONFIG, PipelineError, PreparedConfig, contained_path,
                         evaluated_properties, initialize_submodules, prepare_config,
                         publish_outputs, solution_projects, target_frameworks)
from ci_config import ConfigError
from oci_archive import OciError, combine_archives, inspect_archive
from release_state import SHA, STABLE

TARGETS = Path(__file__).resolve().parents[1] / "release-build/release.targets"
PROPERTIES = ("TargetFramework", "TargetFrameworks", "IsCrossTargetingBuild", "IsTestProject",
              "IsPackable", "PackageId", "HexalithPublishContainer", "ContainerRepository",
              "AssemblyName", "RuntimeIdentifier", "RuntimeIdentifiers", "ContainerRuntimeIdentifier",
              "ContainerRuntimeIdentifiers", "NETCoreSdkPortableRuntimeIdentifier")
AUDIT = re.compile(r"\b(?:warning|error)\s+(NU190[0345])\s*:", re.IGNORECASE)
PACKAGE_ID = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]*\Z")
CONTAINER_NAME = re.compile(r"[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\Z")


@dataclass(frozen=True)
class Project:
    path: str
    package: str
    container: str
    framework: str
    runtimes: tuple[str, ...]
    restore_runtimes: tuple[str, ...] = ()


def release_flags(version: str, sha: str, repository: str) -> list[str]:
    """Global audit/version settings cannot be demoted by imported product props."""
    if not STABLE.fullmatch("v" + version) or not SHA.fullmatch(sha):
        raise PipelineError("release requires a stable version and full commit SHA")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise PipelineError("invalid release repository")
    return ["-noAutoResponse", "-p:Configuration=Release", "-p:UseHexalithProjectReferences=false",
            "-warnaserror:NU1900;NU1903;NU1904;NU1905", "-p:NuGetAudit=true", "-p:NuGetAuditMode=all",
            "-p:NuGetAuditLevel=low", f"-p:RestoreConfigFile={NUGET_CONFIG.resolve()}",
            "-p:EnableNETAnalyzers=true", "-p:RunAnalyzers=true", "-p:RunAnalyzersDuringBuild=true",
            f"-p:Version={version}", f"-p:PackageVersion={version}", f"-p:RepositoryCommit={sha}",
            f"-p:SourceRevisionId={sha}", f"-p:RepositoryUrl=https://github.com/{repository}",
            "-p:RepositoryType=git", "-p:PublishRepositoryUrl=true", "-p:ContinuousIntegrationBuild=true",
            "-p:GeneratePackageOnBuild=false", "-p:IncludeSymbols=false", "-p:IncludeSource=false",
            f"-p:CustomAfterMicrosoftCommonTargets={TARGETS}",
            f"-p:CustomAfterMicrosoftCommonCrossTargetingTargets={TARGETS}",
            f"-p:ContainerImageTag={version}", "-p:ContainerImageTags=", "-p:AutoGenerateImageTag=false",
            "-p:EnableSdkContainerSupport=true", "-p:ContainerImageFormat=OCI",
            "-p:ContainerGenerateLabelsImageSource=false", "-p:ContainerGenerateLabelsImageRevision=false",
            "-p:ContainerGenerateLabelsImageVersion=false"]


def audited_command(arguments: list[str], root: Path, version: str) -> None:
    """Retain the product exit status and diagnose only actual SDK audit failures."""
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_UI_LANGUAGE="en-US", DOTNET_NOLOGO="1")
    try:
        with subprocess.Popen(arguments, cwd=root, env=environment, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as process:
            blocked = False
            for line in process.stdout:
                print(line, end="", flush=True)
                blocked = blocked or bool(AUDIT.search(line))
            code = process.wait()
    except OSError as error:
        raise PipelineError(f"cannot execute {arguments[0]}: {error}") from error
    if code:
        if blocked:
            print(f"v{version} blocked by a vulnerable package", file=sys.stderr, flush=True)
        raise PipelineError(f"release {arguments[1]} failed ({code})", code)


def platform_for_rid(rid: str) -> dict[str, str]:
    match = re.fullmatch(r"(linux(?:-musl)?|win|osx)-(x64|arm64|arm|x86)", rid)
    if not match:
        raise PipelineError(f"unsupported container runtime metadata: {rid!r}")
    system = {"linux": "linux", "linux-musl": "linux", "win": "windows", "osx": "darwin"}[match[1]]
    architecture = {"x64": "amd64", "arm64": "arm64", "arm": "arm", "x86": "386"}[match[2]]
    return {"os": system, "architecture": architecture, **({"variant": "v7"} if match[2] == "arm" else {})}


def runtime_ids(values: dict[str, str]) -> tuple[str, ...]:
    # ComputeContainerConfig has applied SDK defaults. Its explicit singular
    # ContainerRuntimeIdentifier (including one inherited from RuntimeIdentifier)
    # selects a single image even when plural properties are also populated.
    text = (values["ContainerRuntimeIdentifier"] or values["RuntimeIdentifier"]
            or values["ContainerRuntimeIdentifiers"] or values["RuntimeIdentifiers"])
    if not text:
        host = values["NETCoreSdkPortableRuntimeIdentifier"].split("-")
        if len(host) < 2:
            raise PipelineError("SDK container runtime metadata missing")
        text = "linux-" + host[-1]
    rids = tuple(part.strip() for part in text.split(";") if part.strip())
    platforms = [platform_for_rid(rid) for rid in rids]
    if not rids or len({json.dumps(platform, sort_keys=True) for platform in platforms}) != len(rids):
        raise PipelineError("duplicate or missing container platform metadata")
    return rids


def discover_projects(prepared: PreparedConfig, flags: list[str]) -> tuple[Project, ...]:
    """Evaluate imports and each inner TFM; reject ambiguous publish identities."""
    result, packages, containers = [], set(), set()
    for path in solution_projects(prepared.root, prepared.document["solution"]):
        outer = evaluated_properties(prepared.root, path, properties=PROPERTIES, flags=flags)
        frameworks = target_frameworks(outer)
        if outer["IsCrossTargetingBuild"].lower() == "true" and not frameworks:
            raise PipelineError(f"{path}: missing inner target frameworks")
        values = [evaluated_properties(prepared.root, path, framework=framework,
                  properties=PROPERTIES, flags=flags) for framework in frameworks] or [outer]
        for value in [outer, *values]:
            for name in ("IsPackable", "IsTestProject", "HexalithPublishContainer", "IsCrossTargetingBuild"):
                if value[name].lower() not in ("", "true", "false"):
                    raise PipelineError(f"{path}: invalid {name}")
        identities = [(value["IsPackable"].lower() == "true", value["PackageId"],
                       value["IsTestProject"].lower() == "true", value["HexalithPublishContainer"].lower() == "true",
                       value["ContainerRepository"] or value["AssemblyName"]) for value in values]
        if len(set(identities)) != 1:
            raise PipelineError(f"{path}: release identities differ across target frameworks")
        packable, package, test, container, name = identities[0]
        package = package if packable else ""
        enabled_container = container and not test
        name = ""
        if package and (not PACKAGE_ID.fullmatch(package) or package.casefold() in packages):
            raise PipelineError(f"{path}: invalid or duplicate package identity")
        if enabled_container and len(values) != 1:
            raise PipelineError(f"{path}: container requires one evaluated target framework")
        if enabled_container:
            # Repository normalization and runtime defaults belong to the SDK,
            # including its lower-case AssemblyName fallback and RID precedence.
            effective = evaluated_properties(prepared.root, path, framework=values[0]["TargetFramework"],
                properties=PROPERTIES, flags=flags, targets=("ComputeContainerConfig",))
            name = effective["ContainerRepository"]
            if not CONTAINER_NAME.fullmatch(name) or name in containers:
                raise PipelineError(f"{path}: invalid or duplicate container identity")
            rids = runtime_ids(effective)
        else:
            rids = ()
        restore_rids = tuple(dict.fromkeys([*rids, *(rid.strip() for value in values
            for property_name in ("RuntimeIdentifier", "RuntimeIdentifiers")
            for rid in value[property_name].split(";") if rid.strip())]))
        if any(not re.fullmatch(r"[A-Za-z0-9_.-]+", rid) for rid in restore_rids):
            raise PipelineError(f"{path}: invalid restore runtime metadata")
        if package:
            packages.add(package.casefold())
        if name:
            containers.add(name)
        result.append(Project(path, package, name, values[0]["TargetFramework"] if name else "", rids, restore_rids))
    return tuple(result)


def validate_package(path: Path, *, identity: str, version: str, sha: str) -> None:
    try:
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)):
                raise PipelineError("duplicate package member")
            nuspecs = [name for name in names if name.endswith(".nuspec") and "/" not in name]
            if len(nuspecs) != 1:
                raise PipelineError("package must have one root nuspec")
            tree = ET.fromstring(archive.read(nuspecs[0]))
            metadata = next(element for element in tree if element.tag.rsplit("}", 1)[-1] == "metadata")
            elements = {element.tag.rsplit("}", 1)[-1]: element for element in metadata}
            if (elements["id"].text != identity or elements["version"].text != version
                    or elements["repository"].get("commit") != sha):
                raise PipelineError("package identity/version/repository commit mismatch")
    except (OSError, zipfile.BadZipFile, ET.ParseError, KeyError, StopIteration) as error:
        raise PipelineError("invalid release package") from error


def build_release(prepared: PreparedConfig, output: Path, *, version: str, sha: str,
                  repository: str) -> dict:
    flags = release_flags(version, sha, repository)
    output = output.resolve()
    if output.exists():
        raise PipelineError("release payload directory already exists")
    output.mkdir(parents=True)
    manifest = {"sha": sha, "version": version, "packages": [], "containers": []}
    if prepared.solution != "none":
        initialize_submodules(prepared)
        projects = discover_projects(prepared, flags)
        solution = str(contained_path(prepared.root, prepared.document["solution"], kind="solution"))
        runtimes = sorted({runtime for project in projects for runtime in project.restore_runtimes})
        # Prepare every selected container's assets in the one audited restore;
        # SDK default/container-specific RIDs need not appear in RuntimeIdentifiers.
        # Literal quotes preserve a list through MSBuild's command-line parser.
        restore_flags = ['-p:RuntimeIdentifiers="' + ";".join(runtimes) + '"'] if runtimes else []
        audited_command(["dotnet", "restore", solution, *flags, *restore_flags], prepared.root, version)
        audited_command(["dotnet", "build", solution, "--no-restore", *flags], prepared.root, version)
        with tempfile.TemporaryDirectory(prefix="hexalith-release-publish-") as temporary:
            work = Path(temporary)
            for index, project in enumerate(projects):
                path = str(contained_path(prepared.root, project.path, kind="project"))
                if project.package:
                    package_dir = work / f"package-{index}"
                    audited_command(["dotnet", "pack", path, "--no-build", "--no-restore", "--output", str(package_dir),
                                     *flags], prepared.root, version)
                    payloads = list(package_dir.iterdir()) if package_dir.is_dir() else []
                    if len(payloads) != 1 or payloads[0].suffix != ".nupkg" or not payloads[0].is_file():
                        raise PipelineError("expected exactly one versioned package per selected project")
                    validate_package(payloads[0], identity=project.package, version=version, sha=sha)
                    destination = output / "packages" / f"{project.package}.{version}.nupkg"
                    destination.parent.mkdir(exist_ok=True)
                    destination.write_bytes(payloads[0].read_bytes())
                    manifest["packages"].append({"id": project.package, "path": destination.relative_to(output).as_posix()})
                if project.container:
                    archives = []
                    for runtime in project.runtimes:
                        destination = work / f"container-{index}-{runtime}.tar.gz"
                        audited_command(["dotnet", "publish", path, "--no-restore", "-t:PublishContainer", "--runtime", runtime,
                                         f"-p:TargetFramework={project.framework}", "-p:ContainerRuntimeIdentifiers=",
                                         f"-p:ContainerRuntimeIdentifier={runtime}",
                                         f"-p:ContainerArchiveOutputPath={destination}", *flags], prepared.root, version)
                        archives.append((destination, platform_for_rid(runtime)))
                    destination = output / "containers" / f"container-{index}.tar"
                    destination.parent.mkdir(exist_ok=True)
                    if destination.exists():
                        raise PipelineError("conflicting container archive filename")
                    combine_archives(archives, destination, revision=sha, version=version,
                                     repository=repository, name=project.container)
                    inspect_archive(destination, revision=sha, version=version, repository=repository, name=project.container)
                    manifest["containers"].append({"name": project.container, "path": destination.relative_to(output).as_posix()})
    manifest["packages"].sort(key=lambda value: value["id"])
    manifest["containers"].sort(key=lambda value: value["name"])
    expected = {value["path"] for kind in ("packages", "containers") for value in manifest[kind]}
    actual = {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file()}
    if actual != expected:
        raise PipelineError("release inventory does not match produced payload")
    (output / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--phase", choices=("prepare", "build"), default="build")
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise PipelineError("Python 3.12 or later is required")
        workspace = Path(os.environ["GITHUB_WORKSPACE"])
        repository, sha = os.environ["GITHUB_REPOSITORY"], os.environ["RELEASE_SHA"]
        # Checkout is explicitly frozen by the composite; never trust a moved workspace.
        actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=workspace, text=True).strip()
        if actual != sha:
            raise PipelineError("release checkout does not match frozen source SHA")
        prepared = prepare_config(workspace, repository=repository,
                                  module_root=os.environ.get("RELEASE_MODULE_ROOT", ""))
        if args.phase == "prepare":
            publish_outputs({"solution": prepared.solution, "global-json": prepared.global_json})
        else:
            build_release(prepared, Path(os.environ["RELEASE_OUTPUT"]), version=os.environ["RELEASE_VERSION"],
                          sha=sha, repository=repository)
        return 0
    except (ConfigError, OciError, OSError, KeyError, ValueError, subprocess.CalledProcessError) as error:
        print(error, file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    raise SystemExit(main())
