#!/usr/bin/env python3
"""Run and hash-bind the Stage 5 (review-remediated 0.0.0-stage5.7) packaged-tool qualification through installed commands only.

Usage: run-packaged-qualification.py <restored-consumer-dir> <package-dir>
Requires NUGET_PACKAGES (isolated cache) and HEXALITH_DAPR_HOME (isolated Dapr 1.18.0/1.18.2 home).
"""

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
EVIDENCE_REL = HERE.relative_to(ROOT).as_posix()
CAMPAIGN = os.environ.get("G4_CAMPAIGN", "live")
LIVE_REL = EVIDENCE_REL + "/" + CAMPAIGN
MANIFEST_REL = "test/fixtures/module/executable/hexalith.module-manifest.v1.json"
MANIFEST = ROOT / MANIFEST_REL
VERSION = "0.0.0-stage5.7"
SOURCES = (
    "src/libraries/Hexalith.Builds.Tooling",
    "src/libraries/Hexalith.Builds.Module.Cli",
    "src/libraries/Hexalith.Builds.Evidence.Cli",
    "src/hosts/Hexalith.Builds.Module.AppHost",
    "src/hosts/Hexalith.Builds.Module.EventStoreHost",
    "src/hosts/Hexalith.Builds.Module.UiHost",
    "test/fixtures/module/executable",
    "test/fixtures/evidence/acceptance",
)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_hashes() -> dict[str, str]:
    files = [Path(__file__).relative_to(ROOT)]
    for directory in SOURCES:
        files.extend(
            path.relative_to(ROOT)
            for path in (ROOT / directory).rglob("*")
            if path.is_file()
            and not {"bin", "obj", "TestResults", ".git"}.intersection(path.parts)
            and path.suffix in {".cs", ".csproj", ".json", ".slnx", ".props", ".trx", ".nupkg", ".snupkg", ".md"}
        )
    return {path.as_posix(): digest(ROOT / path) for path in sorted(set(files))}


def resources(run_id: object) -> dict[str, object]:
    containers: list[str] = []
    state_exists = False
    if isinstance(run_id, str) and len(run_id) == 32:
        scan = subprocess.run(
            ["docker", "ps", "--all", "--quiet", "--no-trunc", "--filter", f"label=hexalith.g4.run={run_id}"],
            capture_output=True, text=True, timeout=30, check=False)
        containers = scan.stdout.splitlines() if scan.returncode == 0 else ["scan-failed"]
        state_exists = (Path.home() / ".local/share/hexalith-builds/g4-runs" / f"{run_id}.json").exists()
    all_scan = subprocess.run(
        ["docker", "ps", "--all", "--quiet", "--no-trunc", "--filter", "label=hexalith.g4.run"],
        capture_output=True, text=True, timeout=30, check=False)
    apphosts_scan = subprocess.run(
        ["aspire", "ps", "--format", "Json", "--nologo", "--non-interactive"],
        capture_output=True, text=True, timeout=60, check=False)
    try:
        apphosts = json.loads(apphosts_scan.stdout) if apphosts_scan.returncode == 0 else ["scan-failed"]
    except ValueError:
        apphosts = ["scan-failed"]
    state_root = Path.home() / ".local/share/hexalith-builds/g4-runs"
    workspace_root = Path.home() / ".local/share/hexalith-builds/g4-workspaces"
    return {
        "retainedContainers": containers,
        "retainedRunState": state_exists,
        "allRunContainers": all_scan.stdout.splitlines() if all_scan.returncode == 0 else ["scan-failed"],
        # Only runner-owned AppHosts count; unrelated developer AppHosts on the machine are recorded but ignored.
        "runningAppHosts": [a for a in apphosts if not isinstance(a, dict) or "Hexalith.Builds.Module.AppHost" in str(a.get("appHostPath", ""))],
        "otherAppHostPaths": sorted(str(a.get("appHostPath")) for a in apphosts if isinstance(a, dict) and "Hexalith.Builds.Module.AppHost" not in str(a.get("appHostPath", ""))),
        "allRunStateFiles": sorted(path.name for path in state_root.glob("*.json")) if state_root.exists() else [],
        "allRunWorkspaces": sorted(path.name for path in workspace_root.iterdir() if path.is_dir()) if workspace_root.exists() else [],
    }


def invoke(consumer: Path, name: str, tool: str, args: list[str], environment: dict[str, str], cancel_after: float | None = None) -> dict[str, object]:
    command = ["dotnet", "tool", "run", tool, "--", *args]
    started = time.monotonic()
    if cancel_after is not None:
        process = subprocess.Popen(command, cwd=consumer, env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        time.sleep(cancel_after)
        cancelled_while_running = process.poll() is None
        if cancelled_while_running:
            os.killpg(os.getpgid(process.pid), signal.SIGINT)
        try:
            stdout, stderr = process.communicate(timeout=240)
        except subprocess.TimeoutExpired:
            os.killpg(os.getpgid(process.pid), signal.SIGKILL)
            stdout, stderr = process.communicate()
        code = process.returncode
    else:
        cancelled_while_running = None
        completed = subprocess.run(command, cwd=consumer, env=environment, capture_output=True, timeout=1500, check=False)
        stdout, stderr, code = completed.stdout, completed.stderr, completed.returncode
    (HERE / CAMPAIGN / f"{name}.stdout.json").write_bytes(stdout)
    (HERE / CAMPAIGN / f"{name}.stderr.log").write_bytes(stderr)
    try:
        payload = json.loads(stdout)
    except (ValueError, UnicodeDecodeError):
        payload = {}
    display = ["dotnet", "tool", "run", tool, "--", *[arg.replace(str(ROOT) + "/", "") for arg in args]]
    result: dict[str, object] = {
        "name": name,
        "command": display,
        "exitCode": code,
        "status": payload.get("status"),
        "ruleId": (payload.get("outcome") or {}).get("ruleId"),
        "diagnosticRuleIds": [d.get("ruleId") for d in payload.get("diagnostics", [])],
        "runId": payload.get("runId"),
        "elapsedSeconds": round(time.monotonic() - started, 2),
        "stdoutSha256": hashlib.sha256(stdout).hexdigest(),
        "stderrSha256": hashlib.sha256(stderr).hexdigest(),
    }
    if cancelled_while_running is not None:
        result["signalSentWhileRunning"] = cancelled_while_running
    if tool == "hexalith-module":
        result.update(resources(payload.get("runId")))
    return result


def bind_evidence(result: dict[str, object], evidence_rel: str, platform: str | None) -> None:
    evidence = ROOT / evidence_rel
    result["evidencePath"] = evidence_rel
    if not evidence.exists():
        result["evidenceSha256"] = None
        return
    document = json.loads(evidence.read_bytes())
    result["evidenceSha256"] = digest(evidence)
    result["evidenceFinalStatus"] = document["finalStatus"]
    result["evidenceExitCode"] = document["outcome"]["exitCode"]
    result["evidenceTestCounts"] = document["testCounts"]
    result["evidenceArtifactHashes"] = document["artifactHashes"]
    result["evidencePersistedAssertionCount"] = len(document.get("persistedAssertions", []))
    result["evidenceExpectedSequenceCount"] = len(document.get("expectedSequences", []))
    result["evidenceDirtyMarker"] = document["environment"]["repositoryDirtyMarker"]
    result["evidenceProfile"] = document["invocation"]["profile"]
    result["evidenceVolatileFields"] = document["volatileFields"]
    result["evidenceToolVersion"] = document["environment"]["toolVersion"]
    if platform is not None:
        report_rel = evidence_rel[: -len(".json")] + f".{platform}.trx"
        report = ROOT / report_rel
        result["reportPath"] = report_rel
        result["reportSha256"] = digest(report) if report.exists() else None
        if report.exists():
            text = report.read_text(encoding="utf-8")
            result["reportRedacted"] = not any(marker in text for marker in ("runUser", "computerName", "runDeploymentRoot", "<StdOut>", str(Path.home())))


def main() -> int:
    consumer = Path(sys.argv[1]).resolve()
    package_dir = Path(sys.argv[2]).resolve()
    live = HERE / CAMPAIGN
    if live.exists():
        raise SystemExit(f"{live} already exists; every campaign requires a fresh directory.")
    live.mkdir()
    environment = os.environ.copy()
    if not environment.get("NUGET_PACKAGES") or not environment.get("HEXALITH_DAPR_HOME"):
        raise SystemExit("NUGET_PACKAGES and HEXALITH_DAPR_HOME are required.")
    source_before = source_hashes()
    packages = {path.name: digest(path) for path in sorted(package_dir.glob("*.*nupkg"))}
    manifest = ["--manifest", str(MANIFEST)]
    results = []

    def module(name: str, args: list[str], env: dict[str, str] = environment, platform: str | None = None, cancel_after: float | None = None) -> dict[str, object]:
        evidence_rel = f"{LIVE_REL}/{name}.json"
        result = invoke(consumer, name, "hexalith-module", [*args, "--evidence", evidence_rel, "--output", "json"], env, cancel_after)
        bind_evidence(result, evidence_rel, platform)
        results.append(result)
        return result

    vstest = module("persisted-vstest", ["test", *manifest, "--profile", "full"], platform="vstest")
    module("persisted-mtp", ["test", *manifest, "--profile", "full-mtp"], platform="mtp")
    module("unsupported-live", ["test", *manifest, "--profile", "live"])
    unavailable = dict(environment)
    unavailable["HEXALITH_DAPR_HOME"] = "/nonexistent/hexalith-g4-stage5-missing-dapr-home"
    module("prerequisite-unavailable", ["test", *manifest, "--profile", "full"], env=unavailable)
    module("cancelled", ["test", *manifest, "--profile", "full"], cancel_after=40)
    down_run = str(vstest.get("runId"))
    module("down-idempotent", ["down", *manifest, "--run-id", down_run])

    expected = {
        "persisted-vstest": (0, "completed", None),
        "persisted-mtp": (0, "completed", None),
        "unsupported-live": (2, "unavailable", "HXR029"),
        "prerequisite-unavailable": (2, "unavailable", "HXR011"),
        "cancelled": (130, "cancelled", "HXC130"),
        "down-idempotent": (0, "completed", None),
    }
    failures = []
    for result in results:
        code, status, rule = expected[str(result["name"])]
        checks = {
            "exitCode": result["exitCode"] == code,
            "status": result["status"] == status,
            "ruleId": result["ruleId"] == rule,
            "evidence": result.get("evidenceExitCode") == code,
            "noResources": not result["retainedContainers"] and not result["retainedRunState"]
            and not result["allRunContainers"] and not result["runningAppHosts"]
            and not result["allRunStateFiles"] and not result["allRunWorkspaces"],
        }
        if str(result["name"]).startswith("persisted-"):
            checks["reportRedacted"] = result.get("reportRedacted") is True
            checks["volatileReportHash"] = "artifactHashes" in (result.get("evidenceVolatileFields") or [])
            counts = result.get("evidenceTestCounts") or {}
            hashes = result.get("evidenceArtifactHashes") or {}
            checks["nativeReport"] = (
                counts.get("reported") is True and counts.get("passed", 0) > 0 and counts.get("failed") == 0
                and result.get("reportSha256") is not None
                and hashes.get(result.get("reportPath"), "").lower() == result.get("reportSha256")
                and result.get("evidencePersistedAssertionCount") == 12
                and result.get("evidenceExpectedSequenceCount") == 2)
        if result["name"] == "cancelled":
            checks["signalSentWhileRunning"] = result.get("signalSentWhileRunning") is True
            checks["profileRecorded"] = result.get("evidenceProfile") == "full"
        result["checks"] = checks
        failures.extend(f"{result['name']}.{key}" for key, ok in checks.items() if not ok)

    validations = []
    corpus = ROOT / "test/fixtures/evidence/acceptance"
    cases = [corpus / "positive/p0-acceptance.json", *sorted(p for p in (corpus / "negative").glob("*.json") if not p.name.endswith(".expected.json"))]
    for case in cases:
        expectation = json.loads(case.with_name(case.name[: -len(".json")] + ".expected.json").read_bytes())
        result = invoke(consumer, "validate-" + case.parent.name + "-" + case.stem, "hexalith-evidence", ["validate", str(case), "--output", "json"], environment)
        result["expectedExitCode"] = expectation["exitCode"]
        result["expectedRuleIds"] = expectation["ruleIds"]
        result["matched"] = result["exitCode"] == expectation["exitCode"] and result["diagnosticRuleIds"] == expectation["ruleIds"]
        validations.append(result)
        if not result["matched"]:
            failures.append("validate." + case.stem)

    revision = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    by_name = {str(r["name"]): r for r in results}

    def run_entry(key: str, name: str, platform: str | None) -> dict[str, object]:
        r = by_name[name]
        return {
            "key": key,
            "command": "dotnet tool run " + " ".join(str(part) for part in r["command"][3:] if part != "--"),
            "exitCode": r["exitCode"],
            "finalStatus": r.get("evidenceFinalStatus"),
            "evidencePath": r["evidencePath"],
            "evidenceSha256": r.get("evidenceSha256"),
            "reportPath": r.get("reportPath") if platform else None,
            "reportSha256": r.get("reportSha256") if platform else None,
            "reportPlatform": platform,
        }

    candidate = {
        "schema": "hexalith.g4-p0-acceptance.v1",
        "status": "candidate",
        "sourceRevision": revision,
        "platformPins": {"eventStoreVersion": "3.106.0", "daprRuntimeVersion": "1.18.2", "daprSdkVersion": "1.18.8", "frontComposerVersion": "4.5.0"},
        "fixture": {"manifestPath": MANIFEST_REL, "manifestSha256": digest(MANIFEST)},
        "packages": [
            {"id": package_id, "version": VERSION, "feed": "unpublished-local",
             "nupkgPath": (package_dir / f"{package_id}.{VERSION}.nupkg").relative_to(ROOT).as_posix(),
             "nupkgSha256": packages.get(f"{package_id}.{VERSION}.nupkg"),
             "snupkgPath": (package_dir / f"{package_id}.{VERSION}.snupkg").relative_to(ROOT).as_posix(),
             "snupkgSha256": packages.get(f"{package_id}.{VERSION}.snupkg")}
            for package_id in ("Hexalith.Builds.Evidence.Cli", "Hexalith.Builds.Module.Cli")],
        "runs": [
            run_entry("persisted-vstest", "persisted-vstest", "vstest"),
            run_entry("persisted-mtp", "persisted-mtp", "mtp"),
            run_entry("prerequisite-unavailable", "prerequisite-unavailable", None),
            run_entry("cancelled", "cancelled", None),
        ],
        "cleanup": {"status": "passed", "command": "dotnet tool run hexalith-module down --manifest " + MANIFEST_REL + " --run-id " + down_run,
                    "artifactPath": f"{LIVE_REL}/down-idempotent.json", "artifactSha256": by_name["down-idempotent"].get("evidenceSha256")},
        "rollback": {"status": "not-run", "command": "Stage 6 rollback drill not run", "artifactPath": None, "artifactSha256": None},
        "approvals": [],
    }
    candidate_path = live / "candidate-acceptance.json"
    candidate_path.write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8")
    candidate_result = invoke(consumer, "validate-candidate-acceptance", "hexalith-evidence", ["validate", str(candidate_path), "--output", "json"], environment)
    candidate_result["candidateSha256"] = digest(candidate_path)
    candidate_result["expectedFailClosed"] = candidate_result["exitCode"] == 6 and candidate_result["ruleId"] == "HXE202"
    if not candidate_result["expectedFailClosed"]:
        failures.append("candidate.failClosed")

    # The installed tool must never build inside its package folder.
    store = Path(environment["NUGET_PACKAGES"]) / "hexalith.builds.module.cli" / VERSION / "tools/net10.0/any/g4-host/projects"
    store_build_outputs = sorted(path.relative_to(store).as_posix() for path in store.glob("*/*") if path.is_dir())
    if not store.exists() or store_build_outputs:
        failures.append("toolStoreBuildOutputs")

    source_after = source_hashes()
    if source_before != source_after:
        failures.append("sourceChanged")
    report = {
        "schema": "hexalith.g4-stage5-packaged-qualification.v1",
        "status": "passed" if not failures else "failed",
        "failures": failures,
        "version": VERSION,
        "sourceRevision": revision,
        "repositoryDirty": True,
        "packagesSha256": packages,
        "sourceFilesSha256": source_before,
        "sourceBundleSha256": hashlib.sha256(json.dumps(source_before, sort_keys=True).encode()).hexdigest(),
        "sourceUnchanged": source_before == source_after,
        "runs": results,
        "validations": validations,
        "candidateAcceptance": candidate_result,
        "toolStoreProjectsDirectory": str(store),
        "toolStoreBuildOutputs": store_build_outputs,
    }
    (live / "packaged-qualification.json").write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "failures": failures}, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
