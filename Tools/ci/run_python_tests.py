#!/usr/bin/env python3
# /// script
# requires-python = ">=3.12"
# dependencies = ["PyYAML==6.0.3", "jsonschema==4.26.0"]
# ///
"""Run each retained Python test group with this exact interpreter, failing closed."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[2]


def commands(root: Path) -> list[tuple[Path, list[str]]]:
    groups = []
    # Explicit files retain hyphenated names and assertion-based __main__ suites.
    for path in sorted(set(root.glob("Tools/test-*.py")) | set(root.glob("Tools/test_*.py"))):
        groups.append((root, [sys.executable, str(path)]))
    # Separate discovery processes preserve imports and each suite's local cwd.
    for directory in sorted(root.glob("Github/*/tests")) + [root / "test/qualification", root / "Tools/ci/tests"]:
        if not directory.is_dir():
            raise ValueError(f"Missing Python test group: {directory}")
        groups.append((directory, [sys.executable, "-m", "unittest", "discover", "-s", str(directory), "-v"]))
    if not groups:
        raise ValueError("No Python test groups were found")
    return groups


def run(root: Path) -> int:
    if sys.version_info < (3, 12):
        raise ValueError("Python 3.12 or later is required")
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    # Extracted scripts that invoke python3 must use the selected interpreter too.
    environment["PATH"] = str(Path(sys.executable).parent) + os.pathsep + environment.get("PATH", "")
    groups = commands(root)
    failures = []
    print(f"Python suite: {sys.executable} ({sys.version.split()[0]}), {len(groups)} groups", flush=True)
    for directory, command in groups:
        print(f"\nRunning {command[-1]} in {directory.relative_to(root) or '.'}", flush=True)
        result = subprocess.run(command, cwd=directory, env=environment, check=False)
        if result.returncode:
            failures.append((command, result.returncode))
    for command, code in failures:
        print(f"FAILED ({code}): {' '.join(command)}", file=sys.stderr)
    print(f"Python suite: {len(groups) - len(failures)}/{len(groups)} groups passed", flush=True)
    return 1 if failures else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    try:
        return run(ROOT)
    except (OSError, ValueError) as error:
        print(f"Python suite failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
