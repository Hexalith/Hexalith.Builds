"""Source-bound nonshipping build-order graphs under a verified managed lifetime."""
from __future__ import annotations

import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from ci_config import ConfigError

FIELDS = frozenset(("schema", "owner", "projects", "roots", "framework", "propertyPrefix", "properties", "props", "targets"))
FLAGS = ("IsTestProject", "IsPackable", "IsPublishable", "EnableDefaultCompileItems")


def config_path(value, label, suffix=""):
    if (not isinstance(value, str) or not value or value.startswith("/")
            or any(part in ("", ".", "..") for part in value.split("/"))
            or any(ord(character) < 32 or 127 <= ord(character) <= 159 for character in value)
            or any(character in value for character in '\\:;,%$@*?<>"|')
            or suffix and not value.endswith(suffix)):
        raise ConfigError("invalid normalized " + label + " path")


def declaration(prepared, case, environment):
    """Check the committed descriptor before executing any module project."""
    from build_projects import _source_path
    if (not prepared.document.get("moduleManifest") or prepared.document.get("publishedBuilds")
            or environment.workspace is None
            or environment.authority.get("kind") not in ("accepted-delivery", "candidate-qualification")):
        raise ConfigError("isolated builds require verified managed module preparation")
    root = prepared.root
    descriptor = _source_path(root, root, case["isolatedContract"])
    with descriptor.open("rb") as stream:
        payload = stream.read(1024 * 1024 + 1)
    if len(payload) > 1024 * 1024:
        raise ConfigError("isolated contract exceeds its size bound")
    from ci_config import _constant, _object
    try:
        value = json.loads(payload.decode("utf-8"), object_pairs_hook=_object, parse_constant=_constant)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise ConfigError("invalid isolated contract JSON") from error
    if not isinstance(value, dict) or set(value) != FIELDS or value["schema"] != "hexalith.isolated-build.v1":
        raise ConfigError("unsupported closed isolated build contract")
    if value["owner"] != case["project"]:
        raise ConfigError("isolated descriptor owner differs from the declared project")
    project = _source_path(root, root, case["project"])
    for name, limit in (("projects", 32), ("roots", 16)):
        values = value[name]
        if (not isinstance(values, list) or not 1 <= len(values) <= limit
                or any(not isinstance(item, str) for item in values)
                or len({item.casefold() for item in values}) != len(values)):
            raise ConfigError("isolated contract requires distinct bounded project/root paths")
        for item in values:
            config_path(item, "isolated/" + name, ".csproj" if name == "projects" else "")
            if item.startswith(("references/", ".github/", ".git/")) or any(char in item for char in ':;,%$@*?<>"|'):
                raise ConfigError("unsafe or foreign isolated input")
    roots = [root / name for name in value["roots"]]
    from module_preparation import _real
    roots = [_real(directory) for directory in roots]
    if not all(directory.is_dir() for directory in roots):
        raise ConfigError("isolated source roots must be directories")
    inputs = [descriptor, project, *(_source_path(root, root, name) for name in value["projects"])]
    for name in ("props", "targets"):
        config_path(value[name], "isolated/" + name)
        inputs.append(_source_path(root, root, value[name]))
    if not all(any(item.is_relative_to(directory) for directory in roots) for item in inputs):
        raise ConfigError("isolated declaration, owner, children and imports must stay in declared roots")
    if (not isinstance(value["framework"], str) or re.fullmatch(r"net[0-9]+\.0", value["framework"]) is None
            or not isinstance(value["propertyPrefix"], str)
            or re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{7,}", value["propertyPrefix"]) is None
            or not isinstance(value["properties"], dict) or len(value["properties"]) > 32
            or any(not isinstance(key, str) or not key.startswith(value["propertyPrefix"])
                   or not isinstance(item, str) or any(char in item for char in "$%\r\n;")
                   for key, item in value["properties"].items())):
        raise ConfigError("invalid isolated framework or literal property namespace")
    try:
        owner = ET.fromstring(project.read_bytes())
    except ET.ParseError as error:
        raise ConfigError("invalid isolated owner project XML") from error
    # This exception admits a closed build-order adapter, never another task
    # implementation or a replacement for the authenticated producer guards.
    if owner.tag != "Project" or owner.attrib:
        raise ConfigError("isolated owner requires explicit core SDK imports")
    imports = [(item.get("Project"), item.get("Sdk")) for item in owner.iter("Import")]
    if imports != [("Sdk.props", "Microsoft.NET.Sdk"), ("Sdk.targets", "Microsoft.NET.Sdk")]:
        raise ConfigError("isolated owner requires only the explicit core SDK props/targets imports")
    if any(item.tag in ("UsingTask", "Exec", "Sdk")
           or item.tag == "Target" and item.get("Name", "").casefold().startswith("hexalith")
           or any(name.lower() == "treataslocalproperty" for name in item.attrib)
           for item in owner.iter()):
        raise ConfigError("isolated owner cannot replace producer tasks, guards or global policy")
    return value


def preflight(prepared, case, environment, *, flags=None):
    """Bind evaluated adapter shape and invoke its authentic closed C# guard."""
    from ci_pipeline import build_flags, run_command
    from build_projects import _source_path
    value = declaration(prepared, case, environment)
    flags = build_flags() if flags is None else flags
    flags = [*flags, "-nodeReuse:false"]
    project = str(_source_path(prepared.root, prepared.root, case["project"]))
    output = run_command(["dotnet", "msbuild", project, *flags,
        "-getProperty:TargetFramework,HexalithSourceMappingApplies,HexalithSourceMappingHash," + ",".join(FLAGS),
        "-getItem:ProjectReference"], prepared.root, capture=True)
    try:
        evaluated = json.loads(output)
        properties = evaluated["Properties"]
        references = evaluated["Items"]["ProjectReference"]
        expected = {_source_path(prepared.root, prepared.root, name) for name in value["projects"]}
        actual = []
        if properties["TargetFramework"] != value["framework"] or any(properties[name].lower() != "false" for name in FLAGS):
            raise ValueError("adapter flags/framework")
        if (properties["HexalithSourceMappingApplies"] != "true"
                or properties["HexalithSourceMappingHash"] != environment.values["HEXALITH_SOURCE_MAPPING_HASH"]):
            raise ValueError("inactive or different authenticated source mapping")
        for reference in references:
            path = Path(reference["FullPath"])
            if (reference["ReferenceOutputAssembly"].lower() != "false"
                    or reference["HexalithIsolatedBuildContract"] != case["isolatedContract"]):
                raise ValueError("build-order reference contract")
            actual.append(path.resolve(strict=True))
        if len(actual) != len(expected) or set(actual) != expected:
            raise ValueError("exact declared build-order reference set")
    except (ValueError, KeyError, TypeError, OSError) as error:
        raise ConfigError("evaluated isolated adapter differs from its closed descriptor") from error
    # The released task verifies authenticated roots, exact globals, real child
    # imports, shipping/restore/warning policy and locked generated NuGet inputs.
    run_command(["dotnet", "msbuild", project, "-target:HexalithReconcileSourceMapping", *flags], prepared.root)
    return project


def build(prepared, case, environment, flags, command):
    """Use independent SDK processes and canonical module output directories."""
    project = preflight(prepared, case, environment, flags=flags)
    flags = [*flags, "-nodeReuse:false"]
    command(["dotnet", "restore", project, *flags], prepared.root)
    command(["dotnet", "build", project, "--no-restore", *flags], prepared.root)
