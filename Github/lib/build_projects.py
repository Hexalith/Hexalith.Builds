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


def validate_project(root, project, visited=None):
    """Check committed case/import/reference inputs before SDK evaluation."""
    from module_preparation import _git, _real
    visited = set() if visited is None else visited
    try:
        path = _real(root / project)
    except OSError as error:
        raise ConfigError("declared project/import is unavailable") from error
    if not path.is_file() or not path.is_relative_to(root) or project.startswith(("references/", ".git/", ".github/")):
        raise ConfigError("declared project/import is unsafe or foreign")
    if project in visited:
        return
    visited.add(project)
    row = _git(root, "ls-tree", "-z", "HEAD", "--", project)
    if not re.fullmatch(r"100(?:644|755) blob [0-9a-f]{40,64}\t" + re.escape(project) + "\x00", row):
        raise ConfigError("declared project/import must be a committed regular file")
    _git(root, "diff", "--exit-code", "HEAD", "--", project)
    try:
        tree = ET.fromstring(path.read_bytes())
    except ET.ParseError as error:
        raise ConfigError("invalid declared project XML") from error
    for element in tree.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if any(name.lower() == "treataslocalproperty" for name in element.attrib):
            raise ConfigError("declared project cannot demote global build policy")
        if tag in ("MSBuild", "Exec", "CallTarget", "AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties", "SetConfiguration", "SetPlatform"):
            raise ConfigError("declared project contains hidden execution or global policy metadata")
        if tag == "PropertyGroup":
            for prop in element:
                if POLICY.fullmatch(prop.tag.rsplit("}", 1)[-1]):
                    raise ConfigError("declared project cannot override build/import/source/audit policy")
        if tag in ("Import", "ProjectReference"):
            name = element.get("Project" if tag == "Import" else "Include", "")
            if not name or name.startswith("/") or re.match(r"[A-Za-z]:", name) or any(char in name for char in "$%*?;@") or "\\" in name:
                raise ConfigError("declared project has an unsupported indirect import/reference")
            try:
                target = _real(path.parent / name)
            except OSError as error:
                raise ConfigError("declared project import/reference is unavailable") from error
            if not target.is_relative_to(root):
                raise ConfigError("declared project has a foreign import/reference")
            if tag == "ProjectReference" and (any(key in element.attrib for key in ("AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties")) or any(child.tag.rsplit("}", 1)[-1] in ("AdditionalProperties", "GlobalPropertiesToRemove", "UndefineProperties") for child in element)):
                raise ConfigError("declared project reference changes global policy")
            validate_project(root, target.relative_to(root).as_posix(), visited)


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
        command(["dotnet", "build", project, "--no-restore", *selected], prepared.root)
        verify_environment(prepared, environment.authority, environment)
