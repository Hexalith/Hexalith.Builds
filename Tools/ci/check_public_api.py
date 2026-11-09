#!/usr/bin/env python3
# /// script
# requires-python = ">=3.12"
# dependencies = ["PyYAML==6.0.3"]
# ///
"""Reject public API removals without a breaking commit since the latest stable tag."""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path, PurePosixPath
import posixpath
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

import yaml


ROOT = Path(__file__).resolve().parents[2]
STABLE = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
BREAKING_HEADER = re.compile(r"^[a-z][a-z0-9-]*(?:\([^\r\n()]+\))?!: .+\Z")
BREAKING_FOOTER = re.compile(r"^BREAKING(?: CHANGE|-CHANGE):\s*\S", re.MULTILINE)


class WorkflowLoader(yaml.SafeLoader):
    """Use GitHub's YAML 1.2 boolean spelling and reject duplicate mapping keys."""

    yaml_implicit_resolvers = copy.deepcopy(yaml.SafeLoader.yaml_implicit_resolvers)

    def construct_mapping(self, node, deep=False):
        explicit = set()
        for key_node, value_node in node.value:
            if key_node.tag == "tag:yaml.org,2002:merge":
                continue
            key = self.construct_object(key_node, deep=deep)
            if key in explicit:
                raise ValueError(f"Duplicate YAML key: {key}")
            explicit.add(key)
        self.flatten_mapping(node)
        return super().construct_mapping(node, deep=deep)


for _letter, _resolvers in WorkflowLoader.yaml_implicit_resolvers.items():
    WorkflowLoader.yaml_implicit_resolvers[_letter] = [
        item for item in _resolvers if item[0] != "tag:yaml.org,2002:bool"]
WorkflowLoader.add_implicit_resolver("tag:yaml.org,2002:bool", re.compile(r"^(?:true|false)$"), list("tf"))


def git(root: Path, *args: str) -> str:
    result = subprocess.run(["git", "-C", str(root), *args], text=True,
                            capture_output=True, check=False)
    if result.returncode:
        raise ValueError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout


def revision(root: Path, value: str) -> str:
    return git(root, "rev-parse", "--verify", f"{value}^{{commit}}").strip()


def baseline_tag(root: Path, head: str) -> str | None:
    if git(root, "rev-parse", "--is-shallow-repository").strip() == "true":
        raise ValueError("Public API baseline requires full history and tags; fetch with fetch-depth: 0")
    tags = git(root, "tag", "--merged", head).splitlines()
    stable = [(tuple(map(int, match.groups())), tag) for tag in tags
              if (match := STABLE.fullmatch(tag))]
    return max(stable)[1] if stable else None


def document_mapping(value, path: str) -> dict:
    if not isinstance(value, dict):
        raise ValueError(f"{path}: expected a mapping")
    return value


def surface(root: Path, commit: str) -> set[str]:
    """Read Git objects, never working-tree bytes or repository workflow string patterns."""
    paths = set(git(root, "ls-tree", "-r", "--name-only", commit).splitlines())
    entries: set[str] = set()

    def read(path: str) -> str:
        return git(root, "show", f"{commit}:{path}")

    def names(value, path: str, group: str, excluded: tuple[str, ...] = ()) -> None:
        if value is None:
            return
        for name in document_mapping(value, path):
            if not isinstance(name, str):
                raise ValueError(f"{path}: {group} names must be strings")
            if name not in excluded:
                entries.add(f"{path}:{group}:{name}")

    for path in sorted(paths):
        parts = PurePosixPath(path).parts
        is_workflow = path.startswith(".github/workflows/") and len(parts) == 3 and path.endswith((".yml", ".yaml"))
        is_action = path.startswith("Github/") and path.endswith("/action.yml") and not {"tests", "test"}.intersection(parts)
        if not (is_workflow or is_action):
            continue
        doc = document_mapping(yaml.load(read(path), Loader=WorkflowLoader), path)
        if is_workflow:
            events = doc.get("on", {})
            if isinstance(events, str | list):
                if "workflow_call" in ([events] if isinstance(events, str) else events):
                    entries.add(f"{path}:workflow_call")
                continue
            events = document_mapping(events, path)
            if "workflow_call" not in events:
                continue
            entries.add(f"{path}:workflow_call")
            call = events["workflow_call"]
            if call is not None:
                call = document_mapping(call, path)
                for group in ("inputs", "secrets", "outputs"):
                    excluded = ("module-root", "expected-source") if group == "inputs" else ()
                    names(call.get(group), path, group, excluded)
        else:
            entries.add(f"{path}:action")
            names(doc.get("inputs"), path, "inputs")

    def schema_entries(value, prefix: str) -> None:
        if isinstance(value, bool):
            return
        document = document_mapping(value, prefix)

        def component(name: str) -> str:
            return name.replace("~", "~0").replace("/", "~1")

        for key, child in document.items():
            location = f"{prefix}/{key}"
            if key in ("properties", "$defs", "definitions", "patternProperties", "dependentSchemas"):
                for name, definition in document_mapping(child, location).items():
                    member = f"{location}/{component(name)}"
                    entries.add(member)
                    schema_entries(definition, member)
            elif key == "enum":
                if not isinstance(child, list):
                    raise ValueError(f"{prefix}: enum must be an array")
                entries.update(f"{location}/{component(json.dumps(item, sort_keys=True))}" for item in child)
            elif key in ("additionalProperties", "unevaluatedProperties", "propertyNames",
                         "contains", "additionalItems", "unevaluatedItems", "not", "if",
                         "then", "else", "contentSchema"):
                schema_entries(child, location)
            elif key in ("allOf", "anyOf", "oneOf", "prefixItems"):
                if not isinstance(child, list):
                    raise ValueError(f"{location}: expected an array of schemas")
                for index, definition in enumerate(child):
                    schema_entries(definition, f"{location}/{index}" if key == "prefixItems" else location)
            elif key in ("items", "extends"):
                if isinstance(child, list):
                    for index, definition in enumerate(child):
                        schema_entries(definition, f"{location}/{index}" if key == "items" else location)
                else:
                    schema_entries(child, location)
            elif key == "dependencies":
                for name, definition in document_mapping(child, location).items():
                    if isinstance(definition, dict | bool):
                        schema_entries(definition, f"{location}/{component(name)}")
            # Annotation and instance values (examples/default/const included),
            # and unknown vocabulary keywords, are deliberately opaque.

    for path in sorted(paths):
        if PurePosixPath(path).parts[0].lower() in ("schemas", "schema") and path.endswith(".json"):
            if {"tests", "test", "module-root", "expected-source"}.intersection(PurePosixPath(path).parts):
                continue
            document = json.loads(read(path))
            if not isinstance(document, dict | bool):
                raise ValueError(f"{path}: JSON Schema must be an object or boolean")
            entries.add(f"{path}:schema")
            schema_entries(document, path)

    visited: set[str] = set()

    def props(path: str) -> None:
        if path in visited:
            return
        visited.add(path)
        if path not in paths:
            raise ValueError(f"{commit}: imported public props file missing: {path}")
        entries.add(f"{path}:props")
        doc = ET.fromstring(read(path))
        if doc.tag != "Project":
            raise ValueError(f"{path}: expected an MSBuild Project document")
        for group in doc.iter("PropertyGroup"):
            for child in group:
                if not child.tag.startswith("_"):
                    entries.add(f"{path}:property:{child.tag}")
        for package in doc.iter("PackageVersion"):
            name = package.get("Include") or package.get("Update")
            if not name:
                raise ValueError(f"{path}: PackageVersion requires Include or Update")
            entries.add(f"{path}:PackageVersion:{name}")
        for imported in doc.iter("Import"):
            target = imported.get("Project")
            if not target:
                raise ValueError(f"{path}: Import requires Project")
            directory = posixpath.dirname(path)
            target = target.replace("\\", "/").replace("$(MSBuildThisFileDirectory)", directory + "/" if directory else "")
            if "$" in target or "*" in target:
                raise ValueError(f"{path}: cannot statically resolve public Import {target!r}")
            elif not imported.get("Project", "").startswith("$(MSBuildThisFileDirectory)"):
                target = posixpath.join(directory, target)
            target = posixpath.normpath(target)
            if target.startswith("../") or target.startswith("/"):
                raise ValueError(f"{path}: public Import escapes the repository")
            props(target)

    for path in ("Hexalith.Build.props", "Hexalith.Package.props", "Props/Directory.Packages.props"):
        # Synthetic/bootstrapping repositories may introduce these surfaces later.
        if path in paths:
            props(path)
    return entries


def breaking_commits(root: Path, baseline: str, head: str) -> list[str]:
    marked = []
    for commit in git(root, "rev-list", f"{baseline}..{head}").splitlines():
        message = git(root, "show", "-s", "--format=%B", commit)
        lines = message.splitlines()
        if lines and (BREAKING_HEADER.fullmatch(lines[0]) or BREAKING_FOOTER.search("\n".join(lines[1:]))):
            marked.append(commit)
    return marked


def check(root: Path, ref: str) -> int:
    head = revision(root, ref)
    tag = baseline_tag(root, head)
    current = surface(root, head)
    if tag is None:
        print(f"Public API bootstrap: no reachable stable v-semver tag at {head}; validated {len(current)} entries")
        return 0
    baseline = revision(root, tag)
    removed = sorted(surface(root, baseline) - current)
    print(f"Public API baseline: {tag} ({baseline}); checked revision: {head}")
    if not removed:
        print(f"Public API compatible: {len(current)} entries; no removals")
        return 0
    print("Removed public API entries:\n" + "\n".join(f"  {entry}" for entry in removed))
    markers = breaking_commits(root, baseline, head)
    if markers:
        print("Intentional breaking API change authorized by: " + ", ".join(markers))
        return 0
    print("Refusing unmarked API removal. Restore these entries or include a Conventional Commit ! "
          "or BREAKING CHANGE/BREAKING-CHANGE footer in baseline..head.", file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=ROOT)
    parser.add_argument("--ref", default="HEAD")
    args = parser.parse_args()
    try:
        return check(args.repository.resolve(), args.ref)
    except (OSError, ValueError, TypeError, yaml.YAMLError, ET.ParseError) as error:
        print(f"Public API check failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
