#!/usr/bin/env python3
"""Read-only ownership, action, audit and trusted commit-policy guard."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError
import expected_state
import audit_lint
import guard_actions
import guard_commitlint
from guard_tools import Tools


def outputs(values):
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as stream:
            for key, value in values.items():
                stream.write(f"{key}={value}\n")


def run(workspace: str | Path, *, repository: str, module_root: str = "",
        expected_source: str = "", ref: str = "HEAD", transport=None, emit=outputs,
        event_name: str = "workflow_dispatch", event=None, event_path="", commit_range=None, tool_factory=Tools):
    emit({"drift": "false"})
    expected_state.validate_context(repository, module_root, expected_source)
    if ref == "index":
        raise expected_state.ExpectedStateError("guard comparison requires a committed Git tree, not the index")
    git = expected_state.Git(workspace)
    # Freeze content and dispatch identity together before any successor tools.
    frozen_head = git.run("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").decode().strip()
    comparison = expected_state.compare_repository(git, repository=repository, ref=frozen_head,
        module_root=module_root, expected_source=expected_source, transport=transport)
    if comparison is not None:
        emit({"drift": str(bool(comparison.drift)).lower()})
        comparison.require_clean()
        actual, workspace_snapshot = comparison.actual, comparison.workspace
    else:
        actual = workspace_snapshot = expected_state.tree_snapshot(git, frozen_head)
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
    if comparison is not None:
        payload = {}
        if commit_range is None:
            payload = guard_commitlint.load_event(event_path) if event_path else ({} if event is None else event)
        # Validate event endpoints before a network install. A malformed event
        # never silently becomes dispatch/HEAD or a one-commit range.
        guard_commitlint.select(git, event_name=event_name, event=payload,
                                frozen_head=frozen_head, commit_range=commit_range)
        audit_lint.lint(actual)
        with tempfile.TemporaryDirectory(prefix="hexalith-guard-tools-") as directory:
            tools = tool_factory(Path(directory))
            guard_actions.lint(workspace_snapshot, tools)
            guard_commitlint.lint(git, tools, event_name=event_name, event=payload,
                                 frozen_head=frozen_head, commit_range=commit_range)
    print("CI/CD owned content and enabled actions match; guard policy passed.")
    return comparison


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", default="HEAD", help="pinned Git commit/tree to inspect")
    parser.add_argument("--commit-base", help="trusted explicit local full commit endpoint")
    parser.add_argument("--commit-head", help="trusted explicit local full commit endpoint")
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise expected_state.ExpectedStateError("Python 3.12 or later is required")
        if bool(args.commit_base) != bool(args.commit_head):
            raise expected_state.ExpectedStateError("explicit commit range requires both endpoints")
        event_name = os.environ.get("CI_EVENT_NAME", os.environ.get("GITHUB_EVENT_NAME", ""))
        event_path = os.environ.get("CI_EVENT_PATH") or os.environ.get("GITHUB_EVENT_PATH", "")
        # Read payload lazily: ownership failures retain precedence and Builds
        # root does not require event data or perform module lint work.
        run(os.environ.get("GITHUB_WORKSPACE", os.getcwd()),
            repository=os.environ.get("CI_REPOSITORY", ""),
            module_root=os.environ.get("CI_MODULE_ROOT", ""),
            expected_source=os.environ.get("CI_EXPECTED_SOURCE", ""), ref=args.ref,
            event_name=event_name, event_path=event_path,
            commit_range=(args.commit_base, args.commit_head) if args.commit_base else None)
        return 0
    except (expected_state.ExpectedStateError, expected_state.perimeter.PerimeterError, OSError, UnicodeError) as error:
        if not isinstance(error, expected_state.ExpectedStateError):
            error = expected_state.ExpectedStateError(error)
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
