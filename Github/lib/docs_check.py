#!/usr/bin/env python3
"""Check README length and local GitHub workflow badges without network access."""

from __future__ import annotations

import argparse
import html
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote

BADGE = re.compile(
    r"https://github\.com/([\w.-]+/[\w.-]+)/actions/workflows/([^/\s?#()<>]+)/badge\.svg\b",
    re.IGNORECASE,
)
FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")
ORIGIN = re.compile(r"(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)"
                    r"([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?\Z")


def readmes(root: Path):
    yield root / "README.md"
    libraries = root / "src/libraries"
    if (root / "src").is_symlink() or libraries.is_symlink() or not libraries.is_dir():
        return
    for directory, children, files in os.walk(libraries):
        children[:] = sorted(name for name in children if name not in {"bin", "obj", ".git"})
        if "README.md" in files and any(name.endswith(".csproj") for name in files):
            yield Path(directory) / "README.md"


def inspect(root: Path, repository: str):
    workflows = root / ".github/workflows"
    names = set()
    if not (root / ".github").is_symlink() and not workflows.is_symlink() and workflows.is_dir():
        names = {p.name for p in workflows.iterdir()
                 if p.suffix in (".yml", ".yaml") and not p.is_symlink() and p.is_file()}
    for path in readmes(root):
        relative = path.relative_to(root).as_posix()
        try:
            if path.is_symlink() or not path.is_file():
                raise ValueError("missing or irregular file")
            lines = path.read_text(encoding="utf-8-sig").splitlines()
        except (OSError, ValueError):
            yield relative, 1, "D-README", "Supply a regular, readable UTF-8 README.md."
            continue
        if len(lines) > 150:
            yield relative, 151, "D-LENGTH", f"Move detail into linked pages to reduce {len(lines)} lines to 150."
        fence = ""
        for number, line in enumerate(lines, 1):
            marker = FENCE.match(line)
            if fence:
                if marker and marker[1].startswith(fence) and not marker[2].strip():
                    fence = ""
                continue
            if marker:
                fence = marker[1]
                continue
            for match in BADGE.finditer(line):
                if match[1].casefold() == repository.casefold() and unquote(match[2]) not in names:
                    yield relative, number, "D-BADGE", "Point the badge at a current file in .github/workflows/."


def check(root: Path, repository: str, mode: str, *, summary: Path | None = None) -> bool:
    """Return whether the requested mode permits continuing."""
    findings = list(inspect(root, repository))
    records = [json.dumps(dict(file=file, line=line, rule=rule, hint=hint), ensure_ascii=True)
               for file, line, rule, hint in findings]
    for record in records:
        print(record, flush=True)
    result = f"README check ({mode}): {len(findings)} finding(s)"
    print(result, flush=True)
    if summary:
        with summary.open("a", encoding="utf-8") as stream:
            print(f"### {result}\n", file=stream)
            if records:
                print("<pre>" + html.escape("\n".join(records)) + "</pre>", file=stream)
    return mode == "warn" or not findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--mode", choices=("warn", "fail"), default="warn")
    parser.add_argument("--repository", help="GitHub owner/name when origin does not identify the repository")
    args = parser.parse_args()
    if not args.root.is_dir():
        parser.error("--root must be an existing directory")
    repository = args.repository
    if not repository:
        origin = subprocess.run(["git", "-C", str(args.root), "config", "--local", "--get", "remote.origin.url"],
                                text=True, capture_output=True)
        match = ORIGIN.fullmatch(origin.stdout.strip())
        if not match:
            parser.error("provide --repository owner/name for this checkout")
        repository = match[1]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        parser.error("--repository must be a GitHub owner/name")
    return 0 if check(args.root, repository, args.mode) else 1


if __name__ == "__main__":
    raise SystemExit(main())
