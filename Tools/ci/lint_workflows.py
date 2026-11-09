#!/usr/bin/env python3
"""Run integrity-checked workflow linters, retaining only exact frozen findings."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tarfile
import tempfile
import urllib.request


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def atomic_write(path: Path, data: bytes, mode: int) -> None:
    """Publish complete bytes without truncating a file another process is using."""
    descriptor, name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(data)
            os.fchmod(output.fileno(), mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def install_tool(name: str, pin: dict, cache: Path) -> Path:
    """Re-extract a verified archive; never trust an existing executable or download."""
    if not pin["url"].startswith("https://"):
        raise ValueError(f"{name}: download URL must use HTTPS")
    cache.mkdir(parents=True, exist_ok=True)
    archive_path = cache / f"{name}-{pin['version']}.tar.gz"
    if archive_path.exists():
        data = archive_path.read_bytes()
    else:
        with urllib.request.urlopen(pin["url"], timeout=120) as response:
            if not response.url.startswith("https://"):
                raise ValueError(f"{name}: redirected to a non-HTTPS URL")
            data = response.read()
    if hashlib.sha256(data).hexdigest() != pin["sha256"]:
        raise ValueError(f"{name}: SHA256 integrity check failed for {archive_path}")
    atomic_write(archive_path, data, 0o600)
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        members = [member for member in archive.getmembers()
                   if member.isfile() and Path(member.name).name == name]
        if len(members) != 1:
            raise ValueError(f"{name}: archive must contain exactly one executable")
        executable = archive.extractfile(members[0])
        if executable is None:
            raise ValueError(f"{name}: unreadable executable")
        target = cache / name
        atomic_write(target, executable.read(), 0o755)
    return target


def finding_key(finding: dict, root: Path) -> dict:
    """Bind every location, including its feature bytes, to the rule and repository."""
    locations = []
    for location in finding["locations"]:
        path = Path(location["symbolic"]["key"]["Local"]["verbatim_path"])
        relative = path.resolve().relative_to(root.resolve()).as_posix()
        concrete = location["concrete"]
        locations.append({
            "path": relative,
            "start": concrete["location"]["start_point"],
            "end": concrete["location"]["end_point"],
            "feature_sha256": hashlib.sha256(concrete["feature"].encode()).hexdigest(),
        })
    return {"rule": finding["ident"], "locations": locations}


def check_findings(findings: list, baseline: dict, root: Path) -> int:
    if baseline.get("version") != 1:
        raise ValueError("Unsupported frozen composite finding baseline")
    allowed = []
    for entry in baseline["findings"]:
        if not entry.get("rationale") or not entry["locations"]:
            raise ValueError("Frozen findings require locations and a rationale")
        if any(not location["path"].startswith("Github/")
               or not location["path"].endswith("/action.yml") for location in entry["locations"]):
            raise ValueError("Only frozen composite actions may use the external baseline")
        allowed.append({key: entry[key] for key in ("rule", "locations")})
    failures = 0
    ignored = 0
    for finding in findings:
        if finding["ignored"]:
            continue
        key = finding_key(finding, root)
        if key in allowed:
            ignored += 1
            continue
        failures += 1
        location = key["locations"][-1]
        start = location["start"]
        print(f"{location['path']}:{start['row'] + 1}:{start['column'] + 1}: "
              f"{key['rule']}: {finding['desc']}", file=sys.stderr)
    print(f"zizmor: {ignored} exact frozen composite findings; {failures} new findings")
    return failures


def run_linters(root: Path, binaries: dict[str, Path], baseline: dict) -> None:
    workflows = sorted(path for path in (root / ".github/workflows").iterdir()
                       if path.suffix in {".yml", ".yaml"} and path.is_file())
    actions = sorted((root / "Github").glob("**/action.yml"))
    if not workflows or not actions:
        raise ValueError("Expected both workflow YAML and Github/**/action.yml inputs")
    subprocess.run([str(binaries["actionlint"]), "-shellcheck=", "-pyflakes=",
                    "-config-file", str(root / ".github/actionlint.yaml"),
                    *map(str, workflows)], cwd=root, check=True)
    if any(os.environ.get(key) for key in ("ZIZMOR_OFFLINE", "ZIZMOR_NO_ONLINE_AUDITS")):
        raise ValueError("Online zizmor audits are required; remove offline environment overrides")
    if not any(os.environ.get(key) for key in ("GH_TOKEN", "GITHUB_TOKEN", "ZIZMOR_GITHUB_TOKEN")):
        raise ValueError("Online zizmor audits require GH_TOKEN, GITHUB_TOKEN or ZIZMOR_GITHUB_TOKEN")
    result = subprocess.run([
        str(binaries["zizmor"]), "--strict-collection", "--no-progress", "--format=json",
        "--config", str(root / ".github/zizmor.yml"), *map(str, workflows + actions),
    ], cwd=root, capture_output=True, text=True, check=False)
    print(result.stderr, end="", file=sys.stderr)
    # 11..14 are documented finding severities. Every other nonzero code is a
    # failed scan, even when stdout happens to contain a valid finding document.
    if result.returncode not in (0, 11, 12, 13, 14):
        raise RuntimeError(f"zizmor failed with exit code {result.returncode}")
    findings = json.loads(result.stdout)
    if not isinstance(findings, list):
        raise ValueError("zizmor did not return a complete JSON finding list")
    if check_findings(findings, baseline, root):
        raise RuntimeError("New zizmor findings must be fixed before merging")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install-only", action="store_true", help="Install pinned linters and uv")
    parser.add_argument("--export-path", action="store_true", help="Add verified tools to GITHUB_PATH")
    parser.add_argument("--cache", type=Path,
                        default=Path(tempfile.gettempdir()) / "hexalith-builds-ci-tools")
    args = parser.parse_args()
    try:
        if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
            raise ValueError("Committed tool archives target Linux x86_64 (the CI runner)")
        pins = json.loads((HERE / "tools.json").read_text())
        binaries = {name: install_tool(name, pin, args.cache) for name, pin in pins.items()}
        if args.export_path:
            with Path(os.environ["GITHUB_PATH"]).open("a", encoding="utf-8") as output:
                output.write(str(args.cache.resolve()) + "\n")
        if args.install_only:
            print(args.cache.resolve())
            return 0
        baseline = json.loads((HERE / "frozen-composite-findings.json").read_text())
        run_linters(ROOT, binaries, baseline)
        return 0
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.CalledProcessError,
            tarfile.TarError) as error:
        print(f"Workflow quality check failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
