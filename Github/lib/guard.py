#!/usr/bin/env python3
"""Read-only owned-content guard, followed by enabled action availability."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError
import expected_state


def outputs(values):
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as stream:
            for key, value in values.items():
                stream.write(f"{key}={value}\n")


def run(workspace: str | Path, *, repository: str, module_root: str = "",
        expected_source: str = "", ref: str = "HEAD", transport=None, emit=outputs):
    emit({"drift": "false"})
    expected_state.validate_context(repository, module_root, expected_source)
    git = expected_state.Git(workspace)
    comparison = expected_state.compare_repository(git, repository=repository, ref=ref,
        module_root=module_root, expected_source=expected_source, transport=transport)
    if comparison is not None:
        emit({"drift": str(bool(comparison.drift)).lower()})
        comparison.require_clean()
        actual, workspace_snapshot = comparison.actual, comparison.workspace
    else:
        actual = workspace_snapshot = expected_state.tree_snapshot(git, ref)
    try:
        config = expected_state.load_ci(actual, repository=repository, module_root=module_root)
    except ConfigError as error:
        raise expected_state.ExpectedStateError(error) from error
    missing = []
    for action in sorted(set(config["hooks"]) | set(config["jobs"])):
        path = ".github/actions/" + action + "/action.yml"
        metadata = workspace_snapshot.items.get(path)
        if metadata is None or metadata.mode not in {"100644", "100755"}:
            missing.append(f"enabled action {action!r} has no regular committed workspace-root {path}")
    if missing:
        raise expected_state.ExpectedStateError(*missing)
    print("CI/CD owned content and enabled actions match.")
    return comparison


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", default="HEAD", help="pinned Git commit/tree to inspect")
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise expected_state.ExpectedStateError("Python 3.12 or later is required")
        run(os.environ.get("GITHUB_WORKSPACE", os.getcwd()),
            repository=os.environ.get("CI_REPOSITORY", ""),
            module_root=os.environ.get("CI_MODULE_ROOT", ""),
            expected_source=os.environ.get("CI_EXPECTED_SOURCE", ""), ref=args.ref)
        return 0
    except (expected_state.ExpectedStateError, expected_state.perimeter.PerimeterError, OSError, UnicodeError) as error:
        if not isinstance(error, expected_state.ExpectedStateError):
            error = expected_state.ExpectedStateError(error)
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
