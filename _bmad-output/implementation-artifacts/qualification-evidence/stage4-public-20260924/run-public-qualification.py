#!/usr/bin/env python3
"""Run and hash-bind the Stage 4 public-command qualification."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
CLI = ROOT / "src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll"
MANIFEST = "test/fixtures/module/executable/hexalith.module-manifest.v1.json"
PROFILE = "test/fixtures/module/executable/profiles/p0-two-module-full.fixture.json"
SOURCES = (
    "src/libraries/Hexalith.Builds.Tooling",
    "src/libraries/Hexalith.Builds.Module.Cli",
    "src/hosts/Hexalith.Builds.Module.AppHost",
    "src/hosts/Hexalith.Builds.Module.EventStoreHost",
    "test/fixtures/module/executable",
    "test/Hexalith.Builds.Module.Tests",
)
BINARIES = (
    CLI,
    ROOT / "src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Tooling.dll",
    ROOT / "src/hosts/Hexalith.Builds.Module.AppHost/bin/Debug/net10.0/Hexalith.Builds.Module.AppHost.dll",
    ROOT / "src/hosts/Hexalith.Builds.Module.EventStoreHost/bin/Debug/net10.0/Hexalith.Builds.Module.EventStoreHost.dll",
    ROOT / "artifacts/g4-fixture/bin/P0Fixture.Orders/P0Fixture.Orders.dll",
    ROOT / "artifacts/g4-fixture/bin/P0Fixture.Inventory/P0Fixture.Inventory.dll",
    ROOT / "artifacts/g4-fixture/bin/P0Fixture.Orders.Descriptor/P0Fixture.Orders.Descriptor.dll",
    ROOT / "artifacts/g4-fixture/bin/P0Fixture.Inventory.Descriptor/P0Fixture.Inventory.Descriptor.dll",
)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_hashes() -> dict[str, str]:
    files = [Path(MANIFEST), Path(PROFILE), Path(__file__).relative_to(ROOT)]
    for directory in SOURCES:
        files.extend(
            path.relative_to(ROOT)
            for path in (ROOT / directory).rglob("*")
            if path.is_file()
            and not {"bin", "obj", "TestResults", ".git"}.intersection(path.parts)
            and path.suffix in {".cs", ".csproj", ".json", ".slnx"}
        )
    return {str(path): digest(ROOT / path) for path in sorted(set(files))}


def run(name: str, args: list[str], environment: dict[str, str], cancel: bool = False) -> dict[str, object]:
    command = ["dotnet", str(CLI), "test", "--manifest", MANIFEST, "--profile", *args, "--output", "json"]
    started = time.monotonic()
    if cancel:
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        time.sleep(35)
        if process.poll() is None:
            process.send_signal(signal.SIGINT)
        try:
            stdout, stderr = process.communicate(timeout=180)
        except subprocess.TimeoutExpired:
            process.kill()
            stdout, stderr = process.communicate()
        code = process.returncode
    else:
        completed = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, timeout=420, check=False)
        stdout, stderr, code = completed.stdout, completed.stderr, completed.returncode
    (HERE / f"{name}.stdout.json").write_bytes(stdout)
    (HERE / f"{name}.stderr.log").write_bytes(stderr)
    try:
        payload = json.loads(stdout)
    except (ValueError, UnicodeDecodeError):
        payload = {}
    run_id = payload.get("runId")
    containers = []
    state_exists = False
    if isinstance(run_id, str) and len(run_id) == 32:
        scan = subprocess.run(
            ["docker", "ps", "--all", "--quiet", "--no-trunc", "--filter", f"label=hexalith.g4.run={run_id}"],
            cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
        )
        containers = scan.stdout.splitlines() if scan.returncode == 0 else ["scan-failed"]
        state_exists = (Path.home() / ".local/share/hexalith-builds/g4-runs" / f"{run_id}.json").exists()
    all_scan = subprocess.run(
        ["docker", "ps", "--all", "--quiet", "--no-trunc", "--filter", "label=hexalith.g4.run"],
        cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
    )
    all_containers = all_scan.stdout.splitlines() if all_scan.returncode == 0 else ["scan-failed"]
    apphosts_scan = subprocess.run(
        ["aspire", "ps", "--format", "Json", "--nologo", "--non-interactive"],
        cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
    )
    try:
        apphosts = json.loads(apphosts_scan.stdout) if apphosts_scan.returncode == 0 else ["scan-failed"]
    except ValueError:
        apphosts = ["scan-failed"]
    state_root = Path.home() / ".local/share/hexalith-builds/g4-runs"
    workspace_root = Path.home() / ".local/share/hexalith-builds/g4-workspaces"
    all_run_states = sorted(path.name for path in state_root.glob("*.json"))
    all_workspaces = sorted(path.name for path in workspace_root.iterdir() if path.is_dir()) if workspace_root.exists() else []
    return {
        "command": command,
        "exitCode": code,
        "status": payload.get("status"),
        "ruleId": payload.get("outcome", {}).get("ruleId"),
        "runId": run_id,
        "elapsedSeconds": round(time.monotonic() - started, 2),
        "stdoutSha256": hashlib.sha256(stdout).hexdigest(),
        "stderrSha256": hashlib.sha256(stderr).hexdigest(),
        "retainedContainers": containers,
        "retainedRunState": state_exists,
        "allRunContainers": all_containers,
        "runningAppHosts": apphosts,
        "allRunStateFiles": all_run_states,
        "allRunWorkspaces": all_workspaces,
    }


def main() -> int:
    source_before = source_hashes()
    binary_before = {str(path.relative_to(ROOT)): digest(path) for path in BINARIES}
    environment = os.environ.copy()
    environment["NUGET_PACKAGES"] = "/tmp/hexalith-g4-stage3-r17/nuget"
    environment["HEXALITH_DAPR_HOME"] = "/tmp/hexalith-g6.c8hQ7T"
    results = [run("full-public", ["full"], environment)]
    results.append(run("unsupported-live", ["live"], environment))
    unavailable = environment.copy()
    unavailable["HEXALITH_DAPR_HOME"] = "/tmp/hexalith-g4-missing-stage4-prerequisite"
    results.append(run("unavailable-prerequisite", ["full"], unavailable))
    results.append(run("cancelled-public", ["full"], environment, cancel=True))
    source_after = source_hashes()
    binary_after = {str(path.relative_to(ROOT)): digest(path) for path in BINARIES}
    expected = ((0, "passed", None), (2, "unavailable", "HXR029"), (2, "unavailable", None), (130, "cancelled", "HXC130"))
    passed = source_before == source_after and binary_before == binary_after
    for result, (code, status, rule) in zip(results, expected, strict=True):
        passed &= result["exitCode"] == code and result["status"] == status
        if rule is not None:
            passed &= result["ruleId"] == rule
        passed &= not result["retainedContainers"] and not result["retainedRunState"]
        passed &= not result["allRunContainers"] and not result["runningAppHosts"]
        passed &= not result["allRunStateFiles"] and not result["allRunWorkspaces"]
    report = {
        "schema": "hexalith.g4-stage4-public-qualification.v1",
        "status": "passed" if passed else "failed",
        "sourceFilesSha256": source_before,
        "sourceBundleSha256": hashlib.sha256(json.dumps(source_before, sort_keys=True).encode()).hexdigest(),
        "binaryFilesSha256": binary_before,
        "sourceUnchanged": source_before == source_after,
        "binariesUnchanged": binary_before == binary_after,
        "runs": results,
    }
    (HERE / "public-qualification.json").write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "runs": results}, indent=2))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
