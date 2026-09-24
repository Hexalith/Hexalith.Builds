#!/usr/bin/env python3
"""Retain fail-closed, source-bound evidence for consecutive live G-4 suites."""

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parents[1]
EVIDENCE_ROOT = ROOT / "_bmad-output/implementation-artifacts/qualification-evidence"
JWT = re.compile(rb"eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}")
SUITE_SUMMARY = re.compile(r"Total: (\d+), Errors: (\d+), Failed: (\d+), Skipped: (\d+)")
RUN_TAG = b"HEXALITH_G4_RUN_ID="


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def fingerprint(paths):
    files = {str(path.relative_to(ROOT)): sha256(path) for path in sorted(paths)}
    serialized = json.dumps(files, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return {"sha256": hashlib.sha256(serialized).hexdigest(), "files": files}


def source_paths():
    listing = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT, capture_output=True, check=True,
    ).stdout
    paths = []
    for name in listing.split(b"\0"):
        if not name:
            continue
        relative = os.fsdecode(name)
        if not (relative.startswith(("src/", "test/", "Tools/", "schemas/", "Props/"))
                or ("/" not in relative and (relative.endswith((".props", ".targets", ".slnx", ".json"))
                    or relative in (".editorconfig", ".gitattributes")))):
            continue
        path = ROOT / relative
        if not path.is_file():
            raise FileNotFoundError(f"source input is not a readable file: {relative}")
        paths.append(path)
    return paths


def binary_paths():
    roots = [
        ROOT / "src/libraries/Hexalith.Builds.Tooling/bin/Debug/net10.0",
        ROOT / "src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0",
        ROOT / "test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0",
        ROOT / "artifacts/g4-fixture/bin",
        ROOT / "src/hosts",
    ]
    suffixes = {".dll", ".pdb", ".json"}
    files = [path for root in roots for path in root.rglob("*")
             if path.is_file() and path.suffix in suffixes
             and ("/bin/Debug/net10.0/" in str(path) or "artifacts/g4-fixture/bin/" in str(path))]
    required = [
        ROOT / "test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll",
        ROOT / "src/libraries/Hexalith.Builds.Tooling/bin/Debug/net10.0/Hexalith.Builds.Tooling.dll",
        ROOT / "src/hosts/Hexalith.Builds.Module.AppHost/bin/Debug/net10.0/Hexalith.Builds.Module.AppHost.dll",
    ]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise FileNotFoundError("required live binaries missing: " + ", ".join(missing))
    return files


def snapshot():
    return {"source": fingerprint(source_paths()), "binaries": fingerprint(binary_paths())}


def probe_containers():
    command = ["docker", "ps", "-aq", "--filter", "label=hexalith.g4.run"]
    try:
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        errors = result.stderr.strip().splitlines()
        verified = result.returncode == 0 and not errors
        return {"command": shlex.join(command), "exit": result.returncode,
                "count": len(result.stdout.splitlines()) if verified else None,
                "errors": errors, "status": "verified" if verified else "unverified"}
    except OSError as error:
        return {"command": shlex.join(command), "exit": None, "count": None,
                "errors": [f"{type(error).__name__}: {error}"], "status": "unverified"}


def boot_ticks():
    return int(time.clock_gettime(time.CLOCK_BOOTTIME) * os.sysconf("SC_CLK_TCK"))


def probe_processes(proc_root=Path("/proc"), since_ticks=0):
    outcome = {"command": f"inspect same-UID {proc_root}/*/environ for HEXALITH_G4_RUN_ID where startticks >= {since_ticks}",
               "exit": 0, "count": 0, "inaccessible": 0, "vanished": 0,
               "preexisting": 0, "inspected": 0, "errors": [], "status": "verified"}
    try:
        entries = list(proc_root.iterdir())
    except OSError as error:
        entries = []
        outcome["errors"].append(f"{type(error).__name__}: {error}")
    for proc in entries:
        if not proc.name.isdigit():
            continue
        try:
            if proc.stat().st_uid != os.getuid():
                continue
            stat = (proc / "stat").read_text(encoding="ascii")
            start_ticks = int(stat.rsplit(") ", 1)[1].split()[19])
            if start_ticks < since_ticks:
                outcome["preexisting"] += 1
                continue
            environment = (proc / "environ").read_bytes()
            outcome["inspected"] += 1
            if RUN_TAG in environment:
                outcome["count"] += 1
        except FileNotFoundError as error:
            try:
                proc.stat()
            except FileNotFoundError:
                outcome["vanished"] += 1
            except OSError as stat_error:
                outcome["inaccessible"] += 1
                outcome["errors"].append(f"pid {proc.name}: {type(stat_error).__name__}: {stat_error}")
            else:
                outcome["inaccessible"] += 1
                outcome["errors"].append(f"pid {proc.name}: {type(error).__name__}: {error}")
        except ProcessLookupError:
            outcome["vanished"] += 1
        except (OSError, ValueError, IndexError) as error:
            outcome["inaccessible"] += 1
            outcome["errors"].append(f"pid {proc.name}: {type(error).__name__}: {error}")
    if outcome["errors"]:
        outcome.update(exit=1, count=None, status="unverified")
    return outcome


def probe_workspaces(temp_root=Path("/tmp")):
    try:
        with os.scandir(temp_root) as entries:
            count = sum(entry.name.startswith("hexalith-g4-it-") and entry.is_dir()
                        for entry in entries)
        return {"command": f"scan {temp_root}/hexalith-g4-it-*", "exit": 0,
                "count": count, "errors": [], "status": "verified"}
    except OSError as error:
        return {"command": f"scan {temp_root}/hexalith-g4-it-*", "exit": 1,
                "count": None, "errors": [f"{type(error).__name__}: {error}"], "status": "unverified"}


def probe_redaction(log_dir):
    outcome = {"command": f"scan readable logs under {log_dir} for JWT-shaped values",
               "exit": 0, "count": 0, "scanned": 0, "errors": [], "status": "verified"}
    def scan_error(error):
        outcome["errors"].append(f"{type(error).__name__}: {error}")

    for directory, _, names in os.walk(log_dir, onerror=scan_error):
        for name in names:
            if not name.endswith(".log"):
                continue
            path = Path(directory) / name
            try:
                outcome["scanned"] += 1
                if JWT.search(path.read_bytes()):
                    outcome["count"] += 1
            except OSError as error:
                outcome["errors"].append(f"{path}: {type(error).__name__}: {error}")
    if outcome["scanned"] == 0:
        outcome["errors"].append("no log files were inspected")
    if outcome["errors"]:
        outcome.update(exit=1, count=None, status="unverified")
    return outcome


def qualifies(exit_code, suite, before, after, baseline, probes):
    return (exit_code == 0 and suite == (11, 0, 0, 0)
            and before == baseline and after == baseline
            and all(probe["status"] == "verified" and probe["count"] == 0
                    for probe in probes.values()))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-dir", type=Path, required=True)
    parser.add_argument("--dapr-home", type=Path, required=True)
    parser.add_argument("--count", type=int, default=10)
    parser.add_argument("--nuget-packages", type=Path)
    args = parser.parse_args()
    evidence_dir = args.evidence_dir.resolve()
    if not evidence_dir.is_relative_to(EVIDENCE_ROOT) or args.count < 1:
        parser.error("evidence directory must be Builds-owned qualification evidence and count must be positive")
    if not args.dapr_home.is_dir():
        parser.error("Dapr home does not exist")
    evidence_dir.mkdir(parents=True, exist_ok=False)
    try:
        baseline = snapshot()
    except (OSError, subprocess.CalledProcessError) as error:
        write_json(evidence_dir / "qualification.json", {"status": "unverified",
                    "error": f"baseline fingerprint failed: {type(error).__name__}: {error}"})
        return 1
    write_json(evidence_dir / "fingerprints.json", baseline)
    results = []
    for index in range(1, args.count + 1):
        run_dir = evidence_dir / f"run-{index:02d}"
        run_dir.mkdir()
        try:
            before = snapshot()
        except (OSError, subprocess.CalledProcessError) as error:
            failure = {"run": index, "status": "unverified", "error":
                       f"before-suite fingerprint failed: {type(error).__name__}: {error}"}
            write_json(run_dir / "result.json", failure)
            results.append(failure)
            write_json(evidence_dir / "results.json", results)
            break
        log_path = run_dir / "test.log"
        command = ["timeout", "--signal=TERM", "--kill-after=30s", "900s", "dotnet",
                   "test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll",
                   "-noColor"]
        environment = os.environ.copy()
        environment.update(HEXALITH_G4_LIVE="1", HEXALITH_DAPR_HOME=str(args.dapr_home.resolve()),
                           HEXALITH_G4_LOG_DIR=str(run_dir))
        if args.nuget_packages:
            environment["NUGET_PACKAGES"] = str(args.nuget_packages.resolve())
        since_ticks = boot_ticks()
        started = utc_now()
        try:
            with log_path.open("wb") as stream:
                exit_code = subprocess.run(command, cwd=ROOT, env=environment, stdout=stream,
                                           stderr=subprocess.STDOUT, check=False).returncode
            execution_error = None
        except OSError as error:
            exit_code = None
            execution_error = f"{type(error).__name__}: {error}"
        ended = utc_now()
        probes = {"containers": probe_containers(), "processes": probe_processes(since_ticks=since_ticks),
                  "workspaces": probe_workspaces(), "redaction": probe_redaction(run_dir)}
        try:
            after = snapshot()
            fingerprint_error = None
        except (OSError, subprocess.CalledProcessError) as error:
            after = None
            fingerprint_error = f"after-suite fingerprint failed: {type(error).__name__}: {error}"
        log_text = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
        summaries = SUITE_SUMMARY.findall(log_text)
        suite = tuple(map(int, summaries[-1])) if len(summaries) == 1 else None
        unchanged = before == baseline and after == baseline
        passed = qualifies(exit_code, suite, before, after, baseline, probes)
        result = {"run": index, "start": started, "end": ended, "status": "passed" if passed else "failed",
                  "exit": exit_code, "executionError": execution_error, "fingerprintError": fingerprint_error,
                  "suiteSummary": suite,
                  "command": shlex.join(command),
                  "environment": {key: environment.get(key) for key in
                                  ("HEXALITH_G4_LIVE", "HEXALITH_DAPR_HOME", "HEXALITH_G4_LOG_DIR", "NUGET_PACKAGES")},
                  "sourceSha256Before": before["source"]["sha256"],
                  "sourceSha256After": after["source"]["sha256"] if after else None,
                  "binarySha256Before": before["binaries"]["sha256"],
                  "binarySha256After": after["binaries"]["sha256"] if after else None,
                  "unchanged": unchanged, "probes": probes,
                  "testLogSha256": sha256(log_path) if log_path.exists() else None}
        write_json(run_dir / "result.json", result)
        results.append(result)
        write_json(evidence_dir / "results.json", results)
        print(f"run-{index:02d} {result['status']} suite={suite} unchanged={unchanged} "
              + " ".join(f"{key}={value['status']}:{value['count']}" for key, value in probes.items()), flush=True)
    successful = len(results) == args.count and all(result["status"] == "passed" for result in results)
    write_json(evidence_dir / "qualification.json", {"status": "passed" if successful else "failed",
                "requestedSuites": args.count, "passedSuites": sum(result["status"] == "passed" for result in results),
                "sourceSha256": baseline["source"]["sha256"], "binarySha256": baseline["binaries"]["sha256"]})
    return 0 if successful else 1


if __name__ == "__main__":
    sys.exit(main())
