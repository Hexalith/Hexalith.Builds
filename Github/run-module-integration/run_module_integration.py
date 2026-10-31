"""Run blocking declared package integration and recover exact same-job cleanup."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess  # nosec B404 -- fixed module tool, checked paths and allowlisted single-token option values.
import sys

_spec = importlib.util.spec_from_file_location(
    "prepare_module_build", Path(__file__).parents[1] / "prepare-module-build" / "prepare_module_build.py"
)
_prepare = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_prepare)
read_json = _prepare.read_json
real_path = _prepare.real_path
SCHEMA = "hexalith.module.ci-owner-receipt.v1"
IDENTITY = re.compile(r"[\da-f]{32}", re.ASCII)
PROFILE = re.compile(r"[a-z][a-z\d]*(?:-[a-z\d]+)*", re.ASCII)
PATH_PART = re.compile(r"[A-Za-z\d_.-]+", re.ASCII)
TOOL = ["dotnet", "tool", "run", "hexalith-module", "--"]
ARTIFACTS = ("test-result.json", "attachments.json", "attachment-withdrawals.json")
CLEANUP_FILE = "cleanup.json"
EVIDENCE_FILES = frozenset((*ARTIFACTS, "invocation.json", "execution.json", CLEANUP_FILE))


def ci_context() -> dict:
    names = ("GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB")
    values = {name: os.environ.get(name, "") for name in names}
    if (
        os.environ.get("GITHUB_ACTIONS") != "true"
        or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
        or os.environ.get("RUNNER_OS") != "Linux"
        or not all(values.values())
        or not re.fullmatch(r"[1-9]\d*", values["GITHUB_RUN_ATTEMPT"], re.ASCII)
    ):
        raise ValueError("integration requires complete disposable Linux GitHub-hosted job identity")
    return {
        "kind": "ci",
        "repository": values["GITHUB_REPOSITORY"],
        "workflowRunId": values["GITHUB_RUN_ID"],
        "workflowAttempt": int(values["GITHUB_RUN_ATTEMPT"]),
        "jobId": values["GITHUB_JOB"],
        "runnerKind": "github-hosted",
        "operatingSystem": "Linux",
        "disposable": True,
    }


def job_root(name: str) -> Path:
    value = os.environ.get(name, "")
    if not value or not Path(value).is_absolute():
        raise ValueError("integration requires absolute job workspace and temporary roots")
    root = real_path(Path(value))
    if not root.is_dir():
        raise ValueError("integration requires existing job workspace and temporary roots")
    return root


def bounded_path(path: Path, anchor: Path) -> Path:
    if ".." in path.parts:
        raise ValueError("integration paths cannot traverse parent directories")
    absolute = path if path.is_absolute() else Path.cwd() / path
    if not absolute.is_relative_to(anchor):
        raise ValueError("integration path is outside its fixed job root")
    parts = absolute.relative_to(anchor).parts
    if any(PATH_PART.fullmatch(part) is None or part in (".", "..") for part in parts):
        raise ValueError("integration output path contains an unsupported component")
    if any(part.is_symlink() for part in (absolute, *absolute.parents)):
        raise ValueError("integration paths cannot traverse links")
    return absolute


def checked_paths(root: Path, receipt: Path, evidence: Path) -> tuple[Path, Path, Path]:
    workspace = job_root("GITHUB_WORKSPACE")
    root = real_path(root)
    if not root.is_relative_to(workspace):
        raise ValueError("integration tool must execute inside the checked-out workspace")
    temporary = job_root("RUNNER_TEMP")
    expected_receipt = temporary / "hexalith-ci-owner.json"
    if receipt != expected_receipt:
        raise ValueError("integration requires the fixed job-temporary ownership receipt")
    receipt = bounded_path(expected_receipt, temporary)
    evidence = bounded_path(evidence, workspace)
    if not evidence.is_relative_to(workspace / "TestResults"):
        raise ValueError("integration evidence must stay under workspace/TestResults")
    return root, receipt, evidence


def open_directory(anchor: Path, directory: Path) -> int:
    """Create/open each component relative to a held root without following links."""
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    held = os.open(anchor, flags)
    try:
        for part in directory.relative_to(anchor).parts:
            try:
                os.mkdir(part, mode=0o700, dir_fd=held)
            except FileExistsError:
                pass
            child = os.open(part, flags, dir_fd=held)
            os.close(held)
            held = child
        return held
    except BaseException:
        os.close(held)
        raise


def verify_tool(root: Path) -> None:
    manifest = real_path(root / ".config" / "dotnet-tools.json")
    document = read_json(manifest.read_text(encoding="utf-8"))
    tool = document.get("tools", {}).get("hexalith.builds.module.cli", {})
    version = tool.get("version", "")
    if (
        document.get("isRoot") is not True
        or tool.get("rollForward") is not False
        or tool.get("commands") != ["hexalith-module"]
        or not isinstance(version, str)
        or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[\dA-Za-z.-]+)?", version, re.ASCII)
    ):
        raise ValueError("integration requires an exact local module tool pin")
    completed = subprocess.run(
        TOOL + ["--version"],  # nosec B603 # nosemgrep -- fixed executable and literal version-query arguments.
        cwd=root,
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )
    if completed.returncode or completed.stdout.strip().split("+", 1)[0] != version:
        raise ValueError("executing module tool differs from the exact local pin")


def verify_preparation() -> None:
    environment = {key: os.environ.get(key, "") for key in _prepare.KEYS}
    if any(not value for value in environment.values()):
        raise ValueError("integration requires the verified package/Release build preparation")
    workspace = Path(environment["CustomAfterMicrosoftCommonProps"]).parent.parent
    result = {
        "status": "prepared",
        "outcome": {"exitCode": "Success"},
        "runId": None,
        "buildPreparation": {
            "workspace": str(workspace),
            "mode": "package",
            "mappingHash": environment["HEXALITH_SOURCE_MAPPING_HASH"],
            "environment": environment,
        },
    }
    _prepare.verified_environment(result, workspace)


def receipt_owner(receipt: Path, expected_context: dict) -> dict:
    owner = read_json(real_path(receipt).read_text(encoding="utf-8"))
    if owner.get("schema") != SCHEMA or owner.get("executionContext") != expected_context:
        raise ValueError("CI receipt belongs to another repository, run, attempt or job")
    identities = [owner.get(key) for key in ("runId", "attemptId", "environmentId")]
    if (
        any(not isinstance(value, str) or not IDENTITY.fullmatch(value) for value in identities)
        or len(set(identities)) != 3
    ):
        raise ValueError("CI receipt has invalid exact owner identities")
    state = Path(owner.get("stateDirectory", ""))
    expected = (
        Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local" / "share"))) / "hexalith-builds" / "g4-runs"
    )
    if not state.is_absolute() or real_path(state) != real_path(expected):
        raise ValueError("CI receipt uses an unexpected owner metadata root")
    for path in (state / (owner["runId"] + ".json"), state / "diagnostics" / owner["runId"] / "owner.json"):
        if path.exists():
            recorded = read_json(real_path(path).read_text(encoding="utf-8"))
            if any(
                recorded.get(key) != owner[key] for key in ("runId", "attemptId", "environmentId", "executionContext")
            ):
                raise ValueError("durable owner metadata differs from the receipt")
    return owner


def write_evidence(directory: Path, filename: str, value: dict) -> None:
    if filename not in EVIDENCE_FILES:
        raise ValueError("integration evidence filename is not allowlisted")
    workspace = job_root("GITHUB_WORKSPACE")
    directory = bounded_path(directory, workspace)
    if not directory.is_relative_to(workspace / "TestResults"):
        raise ValueError("integration evidence must stay under workspace/TestResults")
    held = open_directory(workspace, directory)
    try:
        flags = os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK
        descriptor = os.open(filename, flags, mode=0o600, dir_fd=held)
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            observed = os.fstat(output.fileno())
            if not stat.S_ISREG(observed.st_mode) or observed.st_nlink != 1:
                raise ValueError("integration evidence must be a private regular file")
            output.truncate(0)
            output.write(json.dumps(value, indent=2, sort_keys=True) + "\n")
    finally:
        os.close(held)


def retain_artifacts(owner: dict, evidence: Path) -> None:
    directory = Path(owner["stateDirectory"]) / "diagnostics" / owner["runId"]
    for filename in ARTIFACTS:
        path = directory / filename
        if path.exists():
            write_evidence(evidence, filename, read_json(real_path(path).read_text(encoding="utf-8")))


def cleanup_verified(completed: subprocess.CompletedProcess, result: dict, owner: dict) -> bool:
    remaining = result.get("remainingResources")
    inventories = ("containerIds", "processIds", "workspaceIds", "claimIds", "discoveryFailures")
    verified = (
        isinstance(remaining, dict)
        and remaining.get("unverified") is False
        and all(remaining.get(key) == [] for key in inventories)
    )
    return (
        completed.returncode == 0
        and result.get("runId") == owner["runId"]
        and result.get("outcome", {}).get("exitCode") == "Success"
        and verified
    )


def finalize(root: Path, receipt: Path, evidence: Path, timeout: int = 150, job_status: str = "") -> int:
    context = ci_context()
    root, receipt, evidence = checked_paths(root, receipt, evidence)
    if job_status not in ("", "success", "failure", "cancelled"):
        raise ValueError("unsupported CI finalizer job status")
    if not receipt.exists():
        write_evidence(evidence, CLEANUP_FILE, {"status": "not-allocated", "executionContext": context})
        return 0
    owner = receipt_owner(receipt, context)
    verify_tool(root)
    environment = os.environ.copy()
    environment["HEXALITH_CI_JOB_STATUS"] = job_status
    completed = subprocess.run(  # nosec B603 # nosemgrep -- fixed tool/options; strictly validated ASCII run id.
        TOOL + ["down", "--run-id=" + owner["runId"], "--output=json"],
        cwd=root,
        capture_output=True,
        text=True,
        timeout=timeout,
        check=False,
        stdin=subprocess.DEVNULL,
        env=environment,
    )
    result = read_json(completed.stdout)
    write_evidence(evidence, CLEANUP_FILE, result)
    retain_artifacts(owner, evidence)
    return 0 if cleanup_verified(completed, result, owner) else 1


def selected_manifest(root: Path, manifest: str, profiles: list[str]) -> Path:
    relative = Path(manifest)
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("integration manifest must stay in the checked-out repository")
    resolved = real_path(root / relative)
    if not resolved.is_relative_to(root) or not resolved.is_file():
        raise ValueError("integration manifest must be a checked-out repository file")
    if not profiles or len(set(profiles)) != len(profiles):
        raise ValueError("integration requires nonempty unique declared package suites")
    if any(len(name) > 63 or PROFILE.fullmatch(name) is None for name in profiles):
        raise ValueError("integration profiles must satisfy the ASCII module-test identifier contract")
    document = read_json(resolved.read_text(encoding="utf-8"))
    declared = {test["name"]: test for module in document.get("modules", []) for test in module.get("tests", [])}
    if any(name not in declared or declared[name].get("mode") != "package" for name in profiles):
        raise ValueError("integration requires declared package suites")
    return resolved


def child_result(child, timeout: int, receipt: Path, evidence: Path) -> int:
    output, _ = child.communicate(timeout=timeout)
    result = read_json(output)
    write_evidence(evidence, "execution.json", result)
    return child.returncode or (
        0 if receipt.exists() and result.get("outcome", {}).get("exitCode") == "Success" else 1
    )


def stop_timed_out_child(child, stop) -> None:
    stop(signal.SIGTERM, None)
    try:
        child.communicate(timeout=120)
    except subprocess.TimeoutExpired:
        child.kill()
        child.communicate(timeout=10)


def execute_integration(command: list[str], root: Path, receipt: Path, evidence: Path, timeout: int) -> int:
    cancelled = False
    child = None
    interrupt_forwarded = False

    def stop(signum, frame):
        nonlocal cancelled, interrupt_forwarded
        cancelled = True
        if child is not None and not interrupt_forwarded and child.poll() is None:
            interrupt_forwarded = True
            child.send_signal(signal.SIGINT)

    previous = {sig: signal.signal(sig, stop) for sig in (signal.SIGINT, signal.SIGTERM)}
    outcome = 1
    cleanup = 1
    try:
        child = subprocess.Popen(
            command,  # nosec B603 # nosemgrep -- fixed tool/verb and checked, equals-bound option values only.
            cwd=root,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            stdin=subprocess.DEVNULL,
        )
        if cancelled:
            stop(signal.SIGTERM, None)
        outcome = child_result(child, timeout, receipt, evidence)
    except subprocess.TimeoutExpired:
        stop_timed_out_child(child, stop)
        outcome = 124
    finally:
        try:
            cleanup = finalize(root, receipt, evidence, job_status="cancelled" if cancelled else "")
        finally:
            for sig, handler in previous.items():
                signal.signal(sig, handler)
    return 130 if cancelled else outcome or cleanup


def run_integration(
    root: Path, manifest: str, profiles: list[str], receipt: Path, evidence: Path, timeout: int = 3600
) -> int:
    context = ci_context()
    root, receipt, evidence = checked_paths(root, receipt, evidence)
    manifest_path = selected_manifest(root, manifest, profiles)
    verify_tool(root)
    verify_preparation()
    if receipt.exists():
        raise ValueError("each CI integration invocation requires a fresh owner receipt")
    held = open_directory(job_root("RUNNER_TEMP"), receipt.parent)
    os.close(held)
    write_evidence(
        evidence,
        "invocation.json",
        {"executionContext": context, "profiles": profiles, "classification": "baseline-only"},
    )
    command = TOOL + [
        "test",
        "--manifest=" + str(manifest_path),
        "--mode=package",
        "--ci-owner-receipt=" + str(receipt),
        "--output=json",
    ]
    command += ["--profile=" + profile for profile in profiles]
    return execute_integration(command, root, receipt, evidence, timeout)


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
        print(f"run-module-integration: {type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
