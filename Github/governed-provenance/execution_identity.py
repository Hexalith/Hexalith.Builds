#!/usr/bin/env python3
"""Bind recovery execution helpers to the Builds revision approved by source release.yml."""

import argparse
import hashlib
import json
import os

from governed_provenance import ClosureCollector, ProvenanceError, verify_git_blobs
import re
import subprocess
import sys
from pathlib import Path

HELPER_DIRECTORIES = ("Github/governed-provenance", "Github/publish-containers")


def workspace_file(workspace, value):
    """Confine recovery state and provenance files to the selected source workspace."""
    root = Path(workspace).resolve()
    path = (root / value).resolve()
    if path == root or not path.is_relative_to(root):
        raise ValueError("recovery file path escapes the selected source workspace")
    return path


def git(root, *arguments, binary=False):
    directory = Path(root).resolve(strict=True)
    if not directory.is_dir():
        raise ValueError("approved Git source must be a directory")
    result = subprocess.run(["git", *arguments], cwd=directory, capture_output=True)
    if result.returncode:
        raise ValueError("approved Git source could not be read: " + " ".join(arguments[:2]))
    return result.stdout if binary else result.stdout.decode("utf-8").strip()


def approved_builds_sha(workflow):
    """Read literal environment declarations from a closed indentation-based YAML subset."""
    stack = []
    values = []
    environments = set()
    mapping_keys = set()
    block_indent = None
    step_number = 0
    active_steps = set()
    for raw in workflow.splitlines():
        if not raw.strip() or raw.lstrip().startswith('#'):
            continue
        indent = len(raw) - len(raw.lstrip(' '))
        if '\t' in raw[:len(raw) - len(raw.lstrip())]:
            raise ValueError('source workflow uses unsupported tab indentation')
        if block_indent is not None:
            if indent > block_indent:
                continue
            block_indent = None
        line = raw[indent:]
        sequence = line.startswith('- ')
        if sequence:
            line = line[2:]
            indent += 2
            step_number += 1
        while stack and stack[-1][0] >= indent:
            stack.pop()
        match = re.fullmatch(r'([A-Za-z_][A-Za-z0-9_-]*):(?:[ ]+(.*))?', line)
        if match is None:
            raise ValueError('source workflow uses an unsupported mapping shape')
        key, value = match.groups()
        value = (value or '').split(' #', 1)[0].strip()
        path = [item[1] for item in stack]
        if stack:
            expected_indent = stack[-1][0] + (4 if path[-1] == 'steps' else 2)
            if indent != expected_indent:
                raise ValueError('source workflow uses unsupported mapping indentation')
        if sequence:
            if len(path) != 3 or path[0] != 'jobs' or path[-1] != 'steps':
                raise ValueError('source workflow uses an unsupported sequence')
            active_steps.add(tuple(path))
        if path and path[-1] == 'steps' and tuple(path) not in active_steps:
            raise ValueError('source workflow steps must be a sequence')
        context = tuple(path) + ((step_number,) if 'steps' in path else ())
        coordinate = context + (key,)
        if coordinate in mapping_keys:
            raise ValueError('source workflow has duplicate mapping keys')
        mapping_keys.add(coordinate)
        if (key in ('env', 'jobs', 'steps') or path == ['jobs']) and value:
            raise ValueError('source workflow uses an unsupported structural mapping: ' + key)
        if key == 'env':
            valid = not path or (len(path) == 2 and path[0] == 'jobs') or (
                len(path) == 3 and path[0] == 'jobs' and path[2] == 'steps')
            context = tuple(path) + ((step_number,) if 'steps' in path else ())
            if not valid or context in environments:
                raise ValueError('source workflow has an ambiguous environment declaration')
            environments.add(context)
        if path and path[-1] == 'env':
            if indent != stack[-1][0] + 2 or key == '<<':
                raise ValueError('source workflow has an unsupported environment declaration')
            if key == 'HEXALITH_BUILDS_EXECUTION_SHA':
                if value.startswith(("'", '"')) and value.endswith(value[0]):
                    value = value[1:-1]
                if not re.fullmatch(r'[0-9a-f]{40}', value):
                    raise ValueError('source workflow must approve an exact literal Builds SHA')
                values.append(value)
        if value.startswith(('|', '>')):
            block_indent = indent
        elif not value:
            stack.append((indent, key))
    if not values or len(set(values)) != 1:
        raise ValueError('source release workflow must declare one consistent exact Builds execution SHA')
    return values[0]


def identity(workspace, builds_root, source, declared, provenance=None):
    """Verify every executable shared-helper byte against the approved Git tree."""
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("source identity must be an exact lowercase commit SHA")
    workspace = Path(workspace).resolve()
    builds_root = Path(builds_root).resolve()
    if git(workspace, "rev-parse", "HEAD") != source:
        raise ValueError("checked-out source does not match the approved recovery source")
    workflow_bytes = git(workspace, "show", f"{source}:.github/workflows/release.yml", binary=True)
    if (workspace / ".github/workflows/release.yml").read_bytes() != workflow_bytes:
        raise ValueError("source release workflow bytes changed")
    approved = approved_builds_sha(workflow_bytes.decode("utf-8"))
    if declared != approved or git(builds_root, "rev-parse", "HEAD") != approved:
        raise ValueError("executed Builds revision differs from the source release workflow declaration")
    helpers = {}
    helper_payloads = {}
    for directory in HELPER_DIRECTORIES:
        tracked = git(builds_root, "ls-tree", "-r", "--name-only", approved, "--", directory).splitlines()
        if not tracked:
            raise ValueError(f"approved helper directory is absent: {directory}")
        for name in tracked:
            path = builds_root / name
            if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(builds_root):
                raise ValueError(f"shared helper is not an approved regular file: {name}")
            payload = path.read_bytes()
            helper_payloads[name] = payload
            helpers[name] = hashlib.sha256(payload).hexdigest()
        extras = git(builds_root, "ls-files", "--others", "--", directory).splitlines()
        if any("__pycache__" not in Path(name).parts and not name.endswith((".pyc", ".pyo")) for name in extras):
            raise ValueError(f"unapproved shared helper files exist in {directory}")
    verify_git_blobs(builds_root, approved, helper_payloads)
    result = {"schema": "hexalith.recovery-execution-identity.v1", "source": source,
              "builds_execution_sha": approved, "builds_root": str(builds_root),
              "source_workflow_sha256": hashlib.sha256(workflow_bytes).hexdigest(), "helpers": helpers}
    if provenance:
        payload = workspace_file(workspace, provenance).read_bytes()
        document = json.loads(payload)
        if (document.get("candidate") != source or document.get("workflow_mode") != "standalone"
                or document.get("builds_execution_sha") != approved or document.get("stage") != "release"):
            raise ValueError("standalone workflow provenance does not bind the recovery source and approved tooling")
        workflow = document.get("closure", {}).get("reusable", {})
        expected_repository = "github.com/" + os.environ.get("GITHUB_REPOSITORY", "").lower()
        if (workflow.get("repository") != expected_repository or workflow.get("commit") != source
                or workflow.get("workflow_path") != ".github/workflows/recover-partial-release.yml"):
            raise ValueError("standalone provenance workflow identity is unverifiable")
        collector = ClosureCollector(builds_root, approved, workflow_root=workspace,
                                     workflow_commit=source, workflow_repository=expected_repository)
        collector.visit_workflow(workflow["workflow_path"])
        if collector.projection(workflow["workflow_path"]) != document["closure"]:
            raise ValueError("standalone provenance closure changed or cannot be reproduced")
        result["provenance_sha256"] = hashlib.sha256(payload).hexdigest()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("capture", "verify", "resolve"))
    parser.add_argument("--workspace", default=".")
    parser.add_argument("--builds-root", default=".hexalith/builds-execution")
    parser.add_argument("--source", required=True)
    parser.add_argument("--declared", default="")
    parser.add_argument("--provenance")
    parser.add_argument("--state")
    args = parser.parse_args()
    try:
        if not re.fullmatch(r"[0-9a-f]{40}", args.source):
            raise ValueError("source identity must be an exact lowercase commit SHA")
        if args.mode == "resolve":
            print(approved_builds_sha(git(args.workspace, "show", f"{args.source}:.github/workflows/release.yml")))
            return 0
        if not args.state:
            raise ValueError("execution identity state path is required")
        path = workspace_file(args.workspace, args.state)
        current = identity(args.workspace, args.builds_root, args.source, args.declared, args.provenance)
        if args.mode == "verify":
            if json.loads(path.read_text()) != current:
                raise ValueError("recovery tooling or provenance changed after validation")
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(current, sort_keys=True, indent=2) + "\n")
    except (ValueError, OSError, ProvenanceError) as error:
        print(f"[execution-identity] {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
