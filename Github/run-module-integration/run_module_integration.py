"""Run blocking declared package integration and recover exact same-job cleanup."""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys

_spec = importlib.util.spec_from_file_location("prepare_module_build", Path(__file__).parents[1] / "prepare-module-build" / "prepare_module_build.py")
_prepare = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_prepare)
read_json = _prepare.read_json
real_path = _prepare.real_path
SCHEMA = "hexalith.module.ci-owner-receipt.v1"
IDENTITY = re.compile(r"[0-9a-f]{32}")
TOOL = ["dotnet", "tool", "run", "hexalith-module", "--"]
ARTIFACTS = ("test-result.json", "attachments.json", "attachment-withdrawals.json")


def ci_context() -> dict:
    values = {name: os.environ.get(name, "") for name in ("GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB")}
    if (os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
            or os.environ.get("RUNNER_OS") != "Linux" or not all(values.values())
            or not re.fullmatch(r"[1-9][0-9]*", values["GITHUB_RUN_ATTEMPT"])):
        raise ValueError("integration requires complete disposable Linux GitHub-hosted job identity")
    return {"kind": "ci", "repository": values["GITHUB_REPOSITORY"], "workflowRunId": values["GITHUB_RUN_ID"],
            "workflowAttempt": int(values["GITHUB_RUN_ATTEMPT"]), "jobId": values["GITHUB_JOB"],
            "runnerKind": "github-hosted", "operatingSystem": "Linux", "disposable": True}


def verify_tool(root: Path) -> None:
    manifest = real_path(root / ".config" / "dotnet-tools.json")
    document = read_json(manifest.read_text(encoding="utf-8"))
    tool = document.get("tools", {}).get("hexalith.builds.module.cli", {})
    version = tool.get("version", "")
    if (document.get("isRoot") is not True or tool.get("rollForward") is not False or tool.get("commands") != ["hexalith-module"]
            or not isinstance(version, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version)):
        raise ValueError("integration requires an exact local module tool pin")
    # Preparation already restored the exact tool. Never select a global tool or restore another version at teardown.
    completed = subprocess.run(TOOL + ["--version"], cwd=root, capture_output=True, text=True, timeout=30, check=False)
    if completed.returncode or completed.stdout.strip().split("+", 1)[0] != version:
        raise ValueError("executing module tool differs from the exact local pin")


def verify_preparation() -> None:
    environment = {key: os.environ.get(key, "") for key in _prepare.KEYS}
    if any(not value for value in environment.values()):
        raise ValueError("integration requires the verified package/Release build preparation")
    workspace = Path(environment["CustomAfterMicrosoftCommonProps"]).parent.parent
    result = {"status": "prepared", "outcome": {"exitCode": "Success"}, "runId": None,
              "buildPreparation": {"workspace": str(workspace), "mode": "package",
                                   "mappingHash": environment["HEXALITH_SOURCE_MAPPING_HASH"], "environment": environment}}
    _prepare.verified_environment(result, workspace)


def receipt_owner(receipt: Path, expected_context: dict) -> dict:
    owner = read_json(real_path(receipt).read_text(encoding="utf-8"))
    if owner.get("schema") != SCHEMA or owner.get("executionContext") != expected_context:
        raise ValueError("CI receipt belongs to another repository, run, attempt or job")
    identities = [owner.get(key) for key in ("runId", "attemptId", "environmentId")]
    if any(not isinstance(value, str) or not IDENTITY.fullmatch(value) for value in identities) or len(set(identities)) != 3:
        raise ValueError("CI receipt has invalid exact owner identities")
    state = Path(owner.get("stateDirectory", ""))
    if not state.is_absolute() or real_path(state) != real_path(Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local" / "share"))) / "hexalith-builds" / "g4-runs"):
        raise ValueError("CI receipt uses an unexpected owner metadata root")
    # When state exists, cross-check its owner identities before invoking any destructive operation.
    for path in (state / (owner["runId"] + ".json"), state / "diagnostics" / owner["runId"] / "owner.json"):
        if path.exists():
            recorded = read_json(real_path(path).read_text(encoding="utf-8"))
            if any(recorded.get(key) != owner[key] for key in ("runId", "attemptId", "environmentId", "executionContext")):
                raise ValueError("durable owner metadata differs from the receipt")
    return owner


def write_evidence(directory: Path, filename: str, value: dict) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    if directory.is_symlink() or (directory / filename).is_symlink():
        raise ValueError("integration evidence cannot traverse links")
    (directory / filename).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def finalize(root: Path, receipt: Path, evidence: Path, timeout: int = 150, job_status: str = "") -> int:
    context = ci_context()
    if not receipt.exists():
        if receipt.is_symlink():
            raise ValueError("invalid receipt link")
        write_evidence(evidence, "cleanup.json", {"status": "not-allocated", "executionContext": context})
        return 0
    owner = receipt_owner(receipt, context)
    verify_tool(root)
    if job_status not in ("", "success", "failure", "cancelled"):
        raise ValueError("unsupported CI finalizer job status")
    environment = os.environ.copy()
    environment["HEXALITH_CI_JOB_STATUS"] = job_status
    completed = subprocess.run(TOOL + ["down", "--run-id", owner["runId"], "--output", "json"], cwd=root,
                               capture_output=True, text=True, timeout=timeout, check=False, stdin=subprocess.DEVNULL, env=environment)
    result = read_json(completed.stdout)
    write_evidence(evidence, "cleanup.json", result)
    # Copy only classified, redacted contract/attachment documents, never endpoint credentials or raw logs.
    directory = Path(owner["stateDirectory"]) / "diagnostics" / owner["runId"]
    for filename in ARTIFACTS:
        path = directory / filename
        if path.exists():
            write_evidence(evidence, filename, read_json(real_path(path).read_text(encoding="utf-8")))
    remaining = result.get("remainingResources")
    verified = (isinstance(remaining, dict) and remaining.get("unverified") is False
                and all(remaining.get(key) == [] for key in ("containerIds", "processIds", "workspaceIds", "claimIds", "discoveryFailures")))
    return 0 if (completed.returncode == 0 and result.get("runId") == owner["runId"]
                 and result.get("outcome", {}).get("exitCode") == "Success" and verified) else 1


def run_integration(root: Path, manifest: str, profiles: list[str], receipt: Path, evidence: Path, timeout: int = 3600) -> int:
    context = ci_context()
    verify_tool(root)
    verify_preparation()
    relative = Path(manifest)
    if relative.is_absolute() or not real_path(root / relative).is_relative_to(real_path(root)) or ".." in relative.parts:
        raise ValueError("integration manifest must stay in the checked-out repository")
    document = read_json((root / relative).read_text(encoding="utf-8"))
    declared = {test["name"]: test for module in document.get("modules", []) for test in module.get("tests", [])}
    if not profiles or len(set(profiles)) != len(profiles) or any(name not in declared or declared[name].get("mode") != "package" for name in profiles):
        raise ValueError("integration requires nonempty declared package suites")
    if receipt.exists() or receipt.is_symlink():
        raise ValueError("each CI integration invocation requires a fresh owner receipt")
    receipt.parent.mkdir(parents=True, exist_ok=True)
    write_evidence(evidence, "invocation.json", {"executionContext": context, "profiles": profiles, "classification": "baseline-only"})
    command = TOOL + ["test", "--manifest", manifest, "--mode", "package", "--ci-owner-receipt", str(receipt), "--output", "json"]
    for profile in profiles:
        command += ["--profile", profile]
    cancelled = False
    child = None
    interrupt_forwarded = False
    def stop(signum, frame):
        nonlocal cancelled, interrupt_forwarded
        cancelled = True
        if child is not None and not interrupt_forwarded and child.poll() is None:
            # Let the runner commit its first cancellation and use its own bounded resource cleanup.
            interrupt_forwarded = True
            child.send_signal(signal.SIGINT)
    previous = {sig: signal.signal(sig, stop) for sig in (signal.SIGINT, signal.SIGTERM)}
    outcome = 1
    cleanup = 1
    try:
        child = subprocess.Popen(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, stdin=subprocess.DEVNULL)
        if cancelled:
            # A signal can arrive inside Popen before its returned process is assigned above.
            stop(signal.SIGTERM, None)
        output, _ = child.communicate(timeout=timeout)
        result = read_json(output)
        write_evidence(evidence, "execution.json", result)
        outcome = child.returncode or (0 if receipt.exists() and result.get("outcome", {}).get("exitCode") == "Success" else 1)
    except subprocess.TimeoutExpired:
        stop(signal.SIGTERM, None)
        try:
            child.communicate(timeout=120)
        except subprocess.TimeoutExpired:
            child.kill()
            child.communicate(timeout=10)
        outcome = 124
    finally:
        try:
            cleanup = finalize(root, receipt, evidence, job_status="cancelled" if cancelled else "")
        finally:
            for sig, handler in previous.items():
                signal.signal(sig, handler)
    return 130 if cancelled else outcome or cleanup


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("run", "finalize"))
    parser.add_argument("--job-status", choices=("", "success", "failure", "cancelled"), default="")
    parser.add_argument("--manifest", default="")
    parser.add_argument("--profiles", default="")
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.operation == "finalize":
            return finalize(Path.cwd(), args.receipt, args.evidence, job_status=args.job_status)
        return run_integration(Path.cwd(), args.manifest, args.profiles.split(), args.receipt, args.evidence)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        # Errors identify the failing prerequisite; never echo child output or private credentials.
        print(f"run-module-integration: {type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
