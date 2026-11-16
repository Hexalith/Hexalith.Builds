#!/usr/bin/env python3
"""Validate already-built packages and isolated consumers without rebuilding a module.

Product IDs, archive assertions and consumer sources belong to the module.
This runner owns packing, temporary feeds, restore isolation and execution.
The interface is fail closed and deliberately supports one explicit framework.
"""

from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


NUGET_CONFIG = Path(__file__).resolve().parents[2] / "NuGet.ci.Config"
IDENTIFIER = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]*\Z")
VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\Z")
FRAMEWORK = re.compile(r"net[0-9]+\.[0-9]+\Z")
PROPERTIES = ("PackageId", "PackageVersion", "IsPackable", "TargetFramework", "TargetFrameworks", "TargetPath", "AssemblyName")
FLAGS = ("-noAutoResponse", "-p:Configuration=Release", "-p:UseHexalithProjectReferences=false",
         "-p:ShouldUnsetParentConfigurationAndPlatform=false", "-warnaserror")


class ValidationError(ValueError):
    """A package contract or product command failed."""


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValidationError(f"duplicate JSON key: {key!r}")
        result[key] = value
    return result


def read_json(path: Path) -> dict:
    result = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    if not isinstance(result, dict):
        raise ValidationError(f"expected JSON object: {path}")
    return result


def source_path(root: Path, value: object, suffix: str) -> Path:
    """Reject computed, escaping and symlinked module inputs."""
    if (not isinstance(value, str) or not value.endswith(suffix) or "\\" in value
            or any(ord(char) < 32 for char in value) or re.match(r"[A-Za-z]:", value)
            or any(part in ("", ".", "..") for part in value.split("/"))):
        raise ValidationError(f"invalid module source path: {value!r}")
    path = root / value
    if not path.resolve().is_relative_to(root) or any(item.is_symlink() for item in (path, *path.parents)):
        raise ValidationError(f"module source escapes or traverses a symlink: {value!r}")
    if not path.is_file():
        raise ValidationError(f"missing module source: {value}")
    return path


def load_contract(root: Path, relative: str) -> tuple[dict, list[dict]]:
    contract = read_json(source_path(root, relative, ".json"))
    if (set(contract) != {"schema", "manifest", "archiveValidator", "targetFramework", "consumers"}
            or contract["schema"] != "hexalith.product-validation.v1"
            or not isinstance(contract["targetFramework"], str)
            or not FRAMEWORK.fullmatch(contract["targetFramework"])):
        raise ValidationError("invalid product-validation contract")
    source_path(root, contract["archiveValidator"], ".py")
    manifest = read_json(source_path(root, contract["manifest"], ".json"))
    rows = manifest.get("packages")
    if set(manifest) != {"packages"} or not isinstance(rows, list) or not rows:
        raise ValidationError("manifest must contain a nonempty packages array")
    ids, projects = set(), set()
    for row in rows:
        if not isinstance(row, dict) or set(row) != {"id", "project"}:
            raise ValidationError("each package needs only id and project")
        identity = row["id"]
        if (not isinstance(identity, str) or not IDENTIFIER.fullmatch(identity)
                or identity.casefold() in ids or row["project"] in projects):
            raise ValidationError("invalid or duplicate package identity/project")
        source_path(root, row["project"], ".csproj")
        ids.add(identity.casefold())
        projects.add(row["project"])
    consumers = contract["consumers"]
    if (not isinstance(consumers, dict) or set(consumers) != {row["id"] for row in rows}):
        raise ValidationError("consumers must cover exactly the package manifest")
    for relative in consumers.values():
        source_path(root, relative, ".cs")
    source_path(root, "global.json", ".json")
    return contract, rows


def run_command(arguments: list[str], root: Path, environment: dict[str, str], *, capture: bool = False) -> str:
    """Use argv, propagate the first failure, and never invoke a shell."""
    result = subprocess.run(arguments, cwd=root, env=environment, check=False, text=True,
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.PIPE if capture else None)
    if result.returncode:
        if capture:
            print(result.stdout, end="", file=sys.stderr)
            print(result.stderr, end="", file=sys.stderr)
        raise ValidationError(f"{arguments[0]} {arguments[1]} failed ({result.returncode})")
    return result.stdout if capture else ""


def evaluated_package(root: Path, row: dict, framework: str, environment: dict[str, str]) -> dict:
    """Run version targets only; require the Release output from the existing build."""
    output = run_command(["dotnet", "msbuild", row["project"], "-target:GetAssemblyVersion",
                          "-getProperty:" + ",".join(PROPERTIES), *FLAGS], root, environment, capture=True)
    values = json.loads(output).get("Properties", {})
    if (set(values) != set(PROPERTIES) or any(not isinstance(value, str) for value in values.values())
            or values["PackageId"] != row["id"] or values["IsPackable"].casefold() != "true"
            or values["TargetFramework"] != framework or values["TargetFrameworks"]
            or not VERSION.fullmatch(values["PackageVersion"])
            or not IDENTIFIER.fullmatch(values["AssemblyName"])):
        raise ValidationError(f"invalid evaluated Release package: {row['id']}")
    target = Path(values["TargetPath"])
    if not target.is_absolute():
        target = root / target
    target = target.resolve()
    if (not target.is_relative_to(root) or not target.is_file()
            or not target.with_suffix(".pdb").is_file()):
        raise ValidationError(f"existing Release assembly/symbol output is missing: {row['id']}")
    values["TargetPath"] = str(target)
    return values


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fingerprints(directory: Path) -> dict[str, str]:
    if directory.is_symlink():
        raise ValidationError("candidate feed must not be a symlink")
    result = {}
    for path in directory.iterdir():
        if path.is_symlink() or not path.is_file():
            raise ValidationError("candidate feed contains a nonregular or symlink entry")
        result[path.name] = digest(path)
    return result


def require_unchanged(feed: Path, expected: dict[str, str], outputs: dict[Path, str]) -> None:
    if fingerprints(feed) != expected:
        raise ValidationError("candidate feed changed during validation")
    if any(digest(path) != original for path, original in outputs.items()):
        raise ValidationError("existing Release assembly/symbol outputs changed during validation")


def archive_inventory(feed: Path, rows: list[dict], version: str) -> None:
    """The product validator handles payload/dependency boundaries after this check."""
    expected = {row["id"] + "." + version + "." + suffix
                for row in rows for suffix in ("nupkg", "snupkg")}
    if {path.name for path in feed.iterdir()} != expected:
        raise ValidationError("package/symbol output differs from the manifest and built version")
    for path in sorted(feed.iterdir()):
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            if (len(names) != len(set(names)) or any(
                    name.startswith("/") or "\\" in name or ".." in name.split("/") for name in names)):
                raise ValidationError(f"unsafe package ZIP: {path.name}")
            specs = [name for name in names if name.endswith(".nuspec")]
            if len(specs) != 1:
                raise ValidationError(f"expected one nuspec: {path.name}")
            document = ET.fromstring(archive.read(specs[0]))
            for element in document.iter():
                element.tag = element.tag.rsplit("}", 1)[-1]
            metadata = document.findall("metadata")
            if document.tag != "package" or len(metadata) != 1:
                raise ValidationError(f"invalid package metadata: {path.name}")
            identity = metadata[0].findall("id")
            versions = metadata[0].findall("version")
            if (len(identity) != 1 or len(versions) != 1 or versions[0].text != version
                    or path.name != f"{identity[0].text}.{version}{path.suffix}"):
                raise ValidationError(f"package identity/version mismatch: {path.name}")
    (feed / "release-artifacts.sha256").write_text(
        "".join(f"{digest(path)}  {path.name}\n" for path in sorted(feed.iterdir())), encoding="utf-8")


def consumer_config(destination: Path, feed: Path, rows: list[dict], base: Path) -> None:
    """Keep the shared signature/trusted-signer policy and map only exact product IDs."""
    document = ET.parse(base)
    root = document.getroot()
    sources = root.find("packageSources")
    mappings = root.find("packageSourceMapping")
    if (sources is None or mappings is None or any(node.get("key") == "candidate" for node in sources)
            or root.find("./config/add[@key='signatureValidationMode'][@value='require']") is None):
        raise ValidationError("shared strict NuGet signature/source configuration is required")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    candidate = ET.SubElement(mappings, "packageSource", key="candidate")
    for row in rows:
        ET.SubElement(candidate, "package", pattern=row["id"])
    document.write(destination, encoding="utf-8", xml_declaration=True)


def restored_candidates(assets: dict, cache: Path, feed: Path, rows: list[dict], version: str, identity: str) -> None:
    """Require candidate versions, exact archive bytes, and the isolated feed source."""
    libraries = assets.get("libraries")
    if (not isinstance(libraries, dict) or identity + "/" + version not in libraries
            or any(not isinstance(value, dict) or value.get("type") != "package" for value in libraries.values())):
        raise ValidationError(f"consumer did not restore only packages including its exact candidate: {identity}")
    known = {row["id"].casefold(): row["id"] for row in rows}
    for key in libraries:
        package, restored_version = key.split("/", 1)
        if package.casefold() not in known:
            continue
        if restored_version != version:
            raise ValidationError(f"wrong restored candidate version: {key}")
        directory = cache / package.lower() / version.lower()
        restored = directory / f"{package.lower()}.{version.lower()}.nupkg"
        original = feed / f"{known[package.casefold()]}.{version}.nupkg"
        if digest(restored) != digest(original):
            raise ValidationError(f"restored candidate bytes differ: {key}")
        if read_json(directory / ".nupkg.metadata").get("source") != str(feed):
            raise ValidationError(f"restored candidate is not from the isolated feed: {key}")


def consumer_project(root: Path, source: str, identity: str, assembly: str, framework: str, version: str) -> None:
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {"OutputType": "Exe", "TargetFramework": framework, "ImplicitUsings": "enable",
                        "Nullable": "enable", "TreatWarningsAsErrors": "true", "NuGetAudit": "true",
                        "NuGetAuditMode": "all", "ManagePackageVersionsCentrally": "true"}.items():
        ET.SubElement(properties, name).text = value
    ET.SubElement(ET.SubElement(project, "ItemGroup"), "PackageReference", Include=identity)
    ET.ElementTree(project).write(root / "Consumer.csproj", encoding="utf-8")
    packages = ET.Element("Project")
    ET.SubElement(ET.SubElement(packages, "ItemGroup"), "PackageVersion", Include=identity, Version=f"[{version}]")
    ET.ElementTree(packages).write(root / "Directory.Packages.props", encoding="utf-8")
    (root / "Program.cs").write_text(source + f'''
var __hexalithProductAssembly = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("{assembly}"));
var __hexalithProductVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(__hexalithProductAssembly)?.InformationalVersion;
if (__hexalithProductVersion?.Split('+')[0] != "{version}")
    throw new System.InvalidOperationException($"Wrong compiled package informational version: {{__hexalithProductVersion}}");
''', encoding="utf-8")


def run(root: Path, contract_path: str, *, nuget_config: Path = NUGET_CONFIG) -> None:
    root = root.resolve()
    contract, rows = load_contract(root, contract_path)
    framework = contract["targetFramework"]
    environment = os.environ.copy()
    for key in list(environment):
        if key.casefold().startswith("hexalith") or key.casefold() in {
                "usehexalithprojectreferences", "usenugetdeps", "msbuildsdkspath", "msbuildextensionspath",
                "directorybuildpropspath", "directorybuildtargetspath", "directorypackagespropspath"}:
            environment.pop(key)
    environment.update(DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1", MSBUILDDISABLENODEREUSE="1")
    evaluated = [evaluated_package(root, row, framework, environment) for row in rows]
    versions = {values["PackageVersion"] for values in evaluated}
    if len(versions) != 1:
        raise ValidationError("built package versions differ")
    version = versions.pop()
    outputs = {path: digest(path) for values in evaluated
               for path in (Path(values["TargetPath"]), Path(values["TargetPath"]).with_suffix(".pdb"))}
    with tempfile.TemporaryDirectory(prefix="hexalith-product-validation-") as temporary:
        scratch = Path(temporary)
        if scratch.resolve().is_relative_to(root):
            raise ValidationError("consumer scratch must be outside the module repository")
        feed = scratch / "feed"
        feed.mkdir()
        for row in rows:
            run_command(["dotnet", "pack", row["project"], "--no-build", "--no-restore", "--output", str(feed),
                         "--include-symbols", "-p:SymbolPackageFormat=snupkg", f"-p:Version={version}",
                         f"-p:PackageVersion={version}", f"-p:MinVerVersionOverride={version}", *FLAGS], root, environment)
        if any(digest(path) != original for path, original in outputs.items()):
            raise ValidationError("packing changed the existing Release assembly/symbol outputs")
        archive_inventory(feed, rows, version)
        frozen = fingerprints(feed)
        run_command([sys.executable, str(source_path(root, contract["archiveValidator"], ".py")), str(feed)], root, environment)
        require_unchanged(feed, frozen, outputs)
        for number, (row, values) in enumerate(zip(rows, evaluated, strict=True)):
            consumer = scratch / ("consumer-" + str(number))
            consumer.mkdir()
            (consumer / "global.json").write_bytes((root / "global.json").read_bytes())
            consumer_project(consumer, source_path(root, contract["consumers"][row["id"]], ".cs").read_text(encoding="utf-8"),
                             row["id"], values["AssemblyName"], framework, version)
            config = consumer / "NuGet.Config"
            consumer_config(config, feed, rows, nuget_config)
            private = deepcopy(environment)
            private.update(NUGET_PACKAGES=str(consumer / "packages"), NUGET_HTTP_CACHE_PATH=str(consumer / "http-cache"),
                           NUGET_PLUGINS_CACHE_PATH=str(consumer / "plugins-cache"), DOTNET_CLI_HOME=str(consumer / "cli-home"))
            run_command(["dotnet", "restore", "Consumer.csproj", "--configfile", str(config), *FLAGS,
                         "-p:NuGetAudit=true", "-p:NuGetAuditMode=all"], consumer, private)
            require_unchanged(feed, frozen, outputs)
            restored_candidates(read_json(consumer / "obj/project.assets.json"), Path(private["NUGET_PACKAGES"]),
                                feed, rows, version, row["id"])
            run_command(["dotnet", "build", "Consumer.csproj", "--no-restore", *FLAGS,
                         "-p:NuGetAudit=true", "-p:NuGetAuditMode=all"], consumer, private)
            require_unchanged(feed, frozen, outputs)
            run_command(["dotnet", str(consumer / "bin/Release" / framework / "Consumer.dll")], consumer, private)
            require_unchanged(feed, frozen, outputs)
            print(f"{row['id']} {version}: isolated consumer passed", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path, default=os.environ.get("GITHUB_WORKSPACE", "."))
    parser.add_argument("--contract", required=True)
    arguments = parser.parse_args()
    try:
        run(arguments.workspace, arguments.contract)
        return 0
    except (ValueError, OSError, KeyError, TypeError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"Product validation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.dont_write_bytecode = True
    raise SystemExit(main())
