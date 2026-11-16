"""Finite declared compatibility builds owned by the existing preparation lifetime."""
from __future__ import annotations
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import shutil
import sys
import xml.etree.ElementTree as ET

from ci_config import BUILD_VERSIONS, ConfigError

VARIABLE = "HexalithBuildProjects"
_MAPPINGS = {}
_ENVIRONMENTS = {}
# Cases may select catalog versions, never override restore, source, import or
# audit policy. Global flags remain authoritative; TreatAsLocalProperty is refused.
POLICY = re.compile(r"(?i)(?:NuGetAudit.*|Restore.*|.*Warnings.*|NoWarn|.*Analyzers.*|UseHexalithProjectReferences|.*FromSource|.*Targets|.*Props|.*Path|.*Output.*|IsTestProject|XunitTestProject|Configuration|Platform)")


def output_mapping(prepared, environment):
    if not prepared.document.get("buildProjects"):
        return {}
    if sys.platform != "linux" or environment.workspace is None:
        raise ConfigError("declared build projects require prepared Linux execution")
    directory = environment.workspace.parent / "build-projects"
    return {case["id"]: {"project": case["project"], "versions": case["versions"],
        "artifactsPath": str(directory / case["id"])} for case in prepared.document["buildProjects"]}


def mapping_value(prepared, environment):
    mapping = output_mapping(prepared, environment)
    if mapping:
        _MAPPINGS[str(prepared.root)] = mapping
        _ENVIRONMENTS[str(prepared.root)] = environment
    return json.dumps(mapping, sort_keys=True, separators=(",", ":")) if mapping else ""


@contextmanager
def output_scope(prepared, environment):
    value = mapping_value(prepared, environment)
    if not value:
        yield
        return
    previous = os.environ.get(VARIABLE)
    if previous:
        raise ConfigError("ambient declared-build output override is forbidden")
    os.environ[VARIABLE] = value
    directory = environment.workspace.parent / "build-projects"
    try:
        yield
    finally:
        _MAPPINGS.pop(str(prepared.root), None)
        _ENVIRONMENTS.pop(str(prepared.root), None)
        shutil.rmtree(directory, ignore_errors=True)
        if previous is None:
            os.environ.pop(VARIABLE, None)
        else:
            os.environ[VARIABLE] = previous


CATALOG_PACKAGES = {"HexalithEventStoreVersion": "hexalith.eventstore", "HexalithTenantsVersion": "hexalith.tenants", "HexalithMemoriesVersion": "hexalith.memories"}
CHILD_PROPERTIES = frozenset(("AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties", "SetConfiguration", "SetPlatform", "SetTargetFramework", "Properties", "Targets"))
DIAGNOSTIC_TASKS = frozenset(("Error", "Warning", "Message", "WriteLinesToFile"))


def _source_path(root, base, name, *, producer=False):
    from module_preparation import _real
    if not isinstance(name, str):
        raise ConfigError("unsupported declared input path")
    name = name.replace("$(MSBuildThisFileDirectory)", str(base) + "/").replace("$(MSBuildProjectDirectory)", str(base))
    tool = os.environ.get("HexalithBuildsPublishedToolRoot")
    if producer and tool:
        name = name.replace("$(HexalithBuildsPublishedToolRoot)", tool)
    if any(char in name for char in "$%*?;@") or "\\" in name:
        raise ConfigError("unsupported indirect declared input path")
    try:
        path = _real(base / name)
    except OSError as error:
        raise ConfigError("declared project/import is unavailable") from error
    if producer and tool and path in (Path(tool) / "Hexalith.Builds.Tooling.dll",
            Path(tool) / "g4-host/bin/EventStoreHost/Hexalith.Builds.Module.EventStoreHost.dll"):
        return path
    if not path.is_relative_to(root) or path.relative_to(root).parts[0] in ("references", ".git", ".github"):
        raise ConfigError("declared input is unsafe or foreign")
    _committed(root, path)
    return path


def _committed(root, path):
    from module_preparation import _git
    name = path.relative_to(root).as_posix()
    row = _git(root, "ls-tree", "-z", "HEAD", "--", name)
    if not path.is_file() or not re.fullmatch(r"100(?:644|755) blob [0-9a-f]{40,64}\t" + re.escape(name) + "\x00", row):
        raise ConfigError("declared project/import must be a committed regular file")
    _git(root, "diff", "--exit-code", "HEAD", "--", name)


def _implicit_files(root, project):
    for filename in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        for directory in (project.parent, *project.parent.parents):
            candidate = directory / filename
            if candidate.exists() or candidate.is_symlink():
                if not candidate.is_relative_to(root):
                    raise ConfigError("declared project has a foreign implicit import")
                yield candidate
                break


def validate_project(root, project, visited=None):
    """Validate finite authored case inputs before the SDK can execute them."""
    from module_preparation import _real
    root = _real(root)
    visited = set() if visited is None else visited
    path = _source_path(root, root, project)
    if path in visited:
        return
    visited.add(path)
    try:
        tree = ET.fromstring(path.read_bytes())
    except ET.ParseError as error:
        raise ConfigError("invalid declared project XML") from error
    if set(tree.attrib) - {"Sdk"}:
        raise ConfigError("declared project root has unsupported execution attributes")
    sdk = tree.get("Sdk")
    if sdk not in (None, "Microsoft.NET.Sdk") or (path.suffix == ".csproj" and sdk != "Microsoft.NET.Sdk"):
        raise ConfigError("declared consumers support only the unversioned Microsoft.NET.Sdk")
    for element in tree.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if any(name.lower() == "treataslocalproperty" for name in element.attrib):
            raise ConfigError("declared project cannot demote global build policy")
        if tag in ("Sdk", "UsingTask", "MSBuild", "Exec", "CallTarget", *CHILD_PROPERTIES):
            raise ConfigError("declared project contains hidden execution or child property metadata")
        if any(name in CHILD_PROPERTIES for name in element.attrib):
            raise ConfigError("declared input changes child global properties")
        if tag == "PropertyGroup":
            for prop in element:
                if POLICY.fullmatch(prop.tag.rsplit("}", 1)[-1]):
                    raise ConfigError("declared project cannot override build/import/source/audit policy")
        if tag == "Target":
            if any(child.tag.rsplit("}", 1)[-1] not in DIAGNOSTIC_TASKS for child in element):
                raise ConfigError("declared targets support only diagnostic/output observation tasks")
            for child in element:
                if child.tag.rsplit("}", 1)[-1] == "WriteLinesToFile" and re.fullmatch(r"\$\(ArtifactsPath\)/[A-Za-z0-9_-]+\.txt", child.get("File", "")) is None:
                    raise ConfigError("declared output observations must stay in case artifacts")
        if "$([" in ET.tostring(element, encoding="unicode"):
            raise ConfigError("declared inputs cannot execute property functions")
        if tag == "ProjectReference":
            if set(element.attrib) - {"Include", "Condition", "ReferenceOutputAssembly"} or any(child.tag.rsplit("}", 1)[-1] not in ("ReferenceOutputAssembly", "PrivateAssets") for child in element):
                raise ConfigError("unsupported child project property metadata")
        if tag in ("PackageReference", "PackageVersion"):
            if set(element.attrib) - {"Include", "Condition", "Version", "PrivateAssets"} or not re.fullmatch(r"[A-Za-z0-9_.-]+", element.get("Include", "")) or any(child.tag.rsplit("}", 1)[-1] not in ("Version", "PrivateAssets") for child in element):
                raise ConfigError("unsupported inline package version or asset metadata")
            if "VersionOverride" in element.attrib or any(child.tag.rsplit("}", 1)[-1] == "VersionOverride" for child in element):
                raise ConfigError("declared package version overrides are forbidden")
            identity = element.get("Include", "").lower()
            for prop, prefix in CATALOG_PACKAGES.items():
                if identity == prefix or identity.startswith(prefix + "."):
                    version = element.get("Version")
                    children = [child for child in element if child.tag.rsplit("}", 1)[-1] == "Version"]
                    if children:
                        if version is not None or len(children) != 1:
                            raise ConfigError("ambiguous declared package version")
                        version = children[0].text
                    if version not in (None, "$(" + prop + ")"):
                        raise ConfigError("declared package must consume its selected catalog version")
        if tag in ("Import", "ProjectReference"):
            name = element.get("Project" if tag == "Import" else "Include", "")
            if tag == "Import" and "Sdk" in element.attrib:
                raise ConfigError("declared imports cannot select another SDK")
            # Authenticated Builds imports are shared policy, never module callbacks.
            builds = root / "references/Hexalith.Builds"
            expanded = name.replace("$(MSBuildThisFileDirectory)", str(path.parent) + "/")
            candidate = path.parent / expanded
            if tag == "Import" and candidate.exists() and _real(candidate).is_relative_to(builds):
                _committed(builds, _real(candidate))
                continue
            target = _source_path(root, path.parent, name)
            validate_project(root, target.relative_to(root).as_posix(), visited)
        if tag in ("Compile", "Analyzer"):
            if set(element.attrib) - {"Include", "Condition", "Link"} or not element.get("Include"):
                raise ConfigError("declared source/analyzer input must be explicit and contained")
            _source_path(root, path.parent, element.get("Include"))
        if tag == "Reference":
            hints = [child.text for child in element if child.tag.rsplit("}", 1)[-1] == "HintPath"]
            if len(hints) != 1:
                raise ConfigError("declared assembly references require one contained HintPath")
            _source_path(root, path.parent, hints[0], producer=True)
    if path.suffix == ".csproj":
        for implicit in _implicit_files(root, path):
            validate_project(root, implicit.relative_to(root).as_posix(), visited)
        # The SDK's default Compile glob must not follow source symlinks or
        # consume uncommitted files. Explicit sibling files are handled above.
        for source in path.parent.rglob("*.cs"):
            if not {"bin", "obj"}.intersection(source.relative_to(path.parent).parts):
                _source_path(root, path.parent, source.relative_to(path.parent).as_posix())


def preflight(prepared):
    for case in prepared.document.get("buildProjects", []):
        validate_project(prepared.root, case["project"])


def verify_packages(artifacts, versions):
    """Check actual restored package bodies, including transitive family members."""
    found = set()
    assets = list(artifacts.rglob("project.assets.json"))
    if not assets:
        raise ConfigError("declared consumer produced no restored package assets")
    for path in assets:
        from module_preparation import _real
        _real(path)
        try:
            libraries = json.loads(path.read_text())["libraries"]
        except (OSError, ValueError, KeyError, TypeError) as error:
            raise ConfigError("invalid declared restored package assets") from error
        for identity, metadata in libraries.items():
            name, separator, version = identity.rpartition("/")
            if not separator:
                raise ConfigError("invalid restored package identity")
            for prop, selected in versions.items():
                prefix = CATALOG_PACKAGES[prop]
                if name.lower() == prefix or name.lower().startswith(prefix + "."):
                    if metadata.get("type") != "package" or version != selected:
                        raise ConfigError("restored package differs from the declared catalog selection")
                    found.add(prop)
    if found != set(versions):
        raise ConfigError("declared catalog selection has no actual restored package")


def build_declared(prepared, flags, command):
    cases = prepared.document.get("buildProjects", [])
    if not cases:
        return
    if sys.platform != "linux":
        raise ConfigError("declared build projects support Linux only")
    from ci_pipeline import evaluated_properties, target_frameworks
    from module_preparation import _real, verify_environment
    try:
        mapping = json.loads(os.environ[VARIABLE])
    except (KeyError, ValueError) as error:
        raise ConfigError("declared projects require their owning preparation output mapping") from error
    # Recompute the mapping from the private authenticated producer receipt/root;
    # local applied scopes and CI both publish it only after preparation.
    if mapping != _MAPPINGS.get(str(prepared.root)) or set(mapping) != {case["id"] for case in cases}:
        raise ConfigError("declared build output mapping differs from configuration")
    roots = []
    environment = _ENVIRONMENTS.get(str(prepared.root))
    if environment is None:
        raise ConfigError("declared project preparation lifetime is unavailable")
    for case in cases:
        verify_environment(prepared, environment.authority, environment)
        entry = mapping[case["id"]]
        if not isinstance(entry, dict) or set(entry) != {"project", "versions", "artifactsPath"} or entry["project"] != case["project"] or entry["versions"] != case["versions"]:
            raise ConfigError("declared build output mapping changed")
        path = Path(entry["artifactsPath"])
        # The parent is private preparation scratch, never module source or a
        # caller-selected output directory. It is authenticated on every reuse.
        if any(char in str(path) for char in "\r\n;%$") or not path.is_absolute() or path.name != case["id"] or path.parent.name != "build-projects" or path.is_relative_to(prepared.root):
            raise ConfigError("unsafe declared build output path")
        if path in roots:
            raise ConfigError("overlapping declared build outputs")
        roots.append(path)
        validate_project(prepared.root, case["project"])
        path.mkdir(parents=True, exist_ok=True)
        _real(path)
        selected = [*flags, "-p:UseArtifactsOutput=true", f"-p:ArtifactsPath={path}",
                    *(f"-p:{name}={value}" for name, value in sorted(case["versions"].items()))]
        policy = dict(flag[3:].split("=", 1) for flag in selected if flag.startswith("-p:"))
        properties = tuple(dict.fromkeys(("TargetFramework", "TargetFrameworks", "IsTestProject", "XunitTestProject", *sorted(BUILD_VERSIONS), *policy)))
        outer = evaluated_properties(prepared.root, case["project"], properties=properties, flags=selected)
        values = [outer, *(evaluated_properties(prepared.root, case["project"], framework=framework,
            properties=properties, flags=selected) for framework in target_frameworks(outer))]
        if any(value["IsTestProject"].lower() == "true" or value["XunitTestProject"].lower() == "true" for value in values):
            raise ConfigError("declared compatibility project cannot be a test project")
        if any(value[name] != version for value in values for name, version in policy.items()):
            raise ConfigError("declared catalog selection or shared build policy was not preserved")
        project = str(_real(prepared.root / case["project"]))
        command(["dotnet", "restore", project, *selected], prepared.root)
        verify_packages(path, case["versions"])
        command(["dotnet", "build", project, "--no-restore", *selected], prepared.root)
        verify_environment(prepared, environment.authority, environment)
