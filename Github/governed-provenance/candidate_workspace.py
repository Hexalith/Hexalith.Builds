#!/usr/bin/env python3
"""Register only the exact workflow-owned preparation checkout in ephemeral Git metadata."""
from __future__ import annotations

import os
from pathlib import Path
import re
import subprocess

OUTPUT = ".hexalith/builds-execution"


def git(root, *arguments):
    return subprocess.run(["git", "-C", str(root), *arguments], check=True, capture_output=True, text=True).stdout.strip()


def register(root, expected_commit):
    root = Path(root).resolve()
    if not re.fullmatch("[0-9a-f]{40}", expected_commit) or Path(git(root, "rev-parse", "--show-toplevel")).resolve() != root:
        raise ValueError("candidate-output-policy-unavailable")
    output = root / OUTPUT
    if (root / ".hexalith").is_symlink() or output.is_symlink() or not output.is_dir():
        raise ValueError("candidate-output-policy-unavailable")
    if git(root, "ls-files", "--", OUTPUT):
        raise ValueError("candidate-output-overlaps-tracked-inputs")
    if Path(git(output, "rev-parse", "--show-toplevel")).resolve() != output or git(output, "rev-parse", "HEAD") != expected_commit:
        raise ValueError("candidate-output-checkout-mismatch")
    if git(output, "remote", "get-url", "origin").removesuffix(".git") != "https://github.com/Hexalith/Hexalith.Builds":
        raise ValueError("candidate-output-checkout-mismatch")
    metadata = Path(git(root, "rev-parse", "--absolute-git-dir"))
    path = Path(git(root, "rev-parse", "--git-path", "info/exclude"))
    if not path.is_absolute():
        path = root / path
    if path.is_symlink() or path.parent.is_symlink() or not path.resolve().is_relative_to(metadata.resolve()):
        raise ValueError("candidate-output-policy-unavailable")
    path.parent.mkdir(parents=True, exist_ok=True)
    previous = path.read_text() if path.exists() else ""
    pattern = "/" + OUTPUT + "/"
    if pattern not in previous.splitlines():
        with path.open("a", encoding="utf-8") as stream:
            stream.write(("\n" if previous and not previous.endswith("\n") else "") + pattern + "\n")


if __name__ == "__main__":
    register(Path.cwd(), os.environ["CANDIDATE_BUILDS_WORKFLOW_SHA"])
