"""Run blocking declared package integration and recover exact same-job cleanup."""

from __future__ import annotations

import argparse
import ctypes
import errno
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess  # nosec B404 -- fixed module tool, checked paths and allowlisted single-token option values.
import sys
import tempfile
import time
import uuid

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
EVIDENCE_FILES = frozenset((*ARTIFACTS, "invocation.json", "execution.json"))
# Every exact down attempt keeps its own record; a later attempt never replaces an earlier one.
CLEANUP_RECORD = "cleanup-{}.json"
MAX_CLEANUP_RECORDS = 99
PROCESS_SCHEMA = "hexalith.module.ci-owner-process.v1"
PROCESS_RECORD = "hexalith-ci-owner.process.json"
EXECUTION_SCHEMA = "hexalith.module.ci-integration-execution.v1"
CLEANUP_SCHEMA = "hexalith.module.ci-integration-cleanup.v1"
# The hosted runner signals only the step process: SIGINT, SIGTERM 7.5 s later, and a process-tree kill 2.5 s
# after that. The helper escalates inside that window and leaves exact cleanup to the always() finalizer.
CANCELLATION_ESCALATION_SECONDS = 6.0
CANCELLATION_FINAL_SECONDS = 8.5
# Every bounded wait on the timeout path. The helper timeout leaves room for all of them inside the integration step,
# and for them plus the always() finalizer step inside the job (domain-ci-v2.yml; the validator derives from these).
VERIFY_TOOL_SECONDS = 30
TIMEOUT_STOP_SECONDS = 120
POST_KILL_WAIT_SECONDS = 10
SESSION_SIGNAL_WAIT_SECONDS = 5.0
DOWN_TIMEOUT_SECONDS = 150
RESERVE_MARGIN_SECONDS = 30
FINALIZER_STEP_SECONDS = 240
JOB_SETUP_ALLOWANCE_SECONDS = 60
MINIMUM_TIMEOUT_SECONDS = 60
MAXIMUM_TIMEOUT_SECONDS = 86400
# Two escalating terminations (tool first, then the remaining selection), each SIGINT, SIGTERM and SIGKILL.
FINALIZE_RESERVE_SECONDS = 2 * 3 * int(SESSION_SIGNAL_WAIT_SECONDS) + VERIFY_TOOL_SECONDS + DOWN_TIMEOUT_SECONDS
STEP_RESERVE_SECONDS = (VERIFY_TOOL_SECONDS + TIMEOUT_STOP_SECONDS + POST_KILL_WAIT_SECONDS + FINALIZE_RESERVE_SECONDS
                        + RESERVE_MARGIN_SECONDS)
DEFAULT_STEP_TIMEOUT_MINUTES = 40
DEFAULT_TIMEOUT_SECONDS = DEFAULT_STEP_TIMEOUT_MINUTES * 60 - STEP_RESERVE_SECONDS
OWNER_RUN_TAG = "HEXALITH_G4_RUN_ID"
STDOUT_BYTES = 16 * 1024 * 1024
STDERR_EXCERPT_CHARACTERS = 4096
# The tool runs only after the helper durably recorded its new session; an orphaned gate exits without launching.
GATE = ["/bin/sh", "-c", 'IFS= read -r go || exit 125; [ "$go" = 1 ] || exit 125; exec "$@" </dev/null', "hexalith-ci-gate"]
SECRET_TEXT = re.compile(
    r"(?i)(?<![a-z])(authorization|bearer|password|passwd|secret|token|api[-_]?key|credential)s?(?![a-z])[^\n]*")
OPAQUE_TEXT = re.compile(r"[A-Za-z\d+/=_-]{40,}", re.ASCII)
FIXED_METADATA_BYTES = 4 * 1024 * 1024
JSON_DIAGNOSTIC_BYTES = 1024 * 1024
DIAGNOSTICS = {
    "lifecycle": ("lifecycle", ".json", JSON_DIAGNOSTIC_BYTES),
    "native-report": ("native", ".trx", 4 * 1024 * 1024),
    "functional-observations": ("functional-observations", ".json", JSON_DIAGNOSTIC_BYTES),
}


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


def open_directory(anchor: Path, directory: Path, *, create: bool = True) -> int:
    """Create/open each component relative to a held root without following links."""
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    held = os.open(anchor, flags)
    try:
        for part in directory.relative_to(anchor).parts:
            if create:
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
        timeout=VERIFY_TOOL_SECONDS,
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


def evidence_directory(directory: Path) -> int:
    """Hold the bounded workspace evidence directory without following links."""
    workspace = job_root("GITHUB_WORKSPACE")
    directory = bounded_path(directory, workspace)
    if not directory.is_relative_to(workspace / "TestResults"):
        raise ValueError("integration evidence must stay under workspace/TestResults")
    return open_directory(workspace, directory)


def publish_json(held: int, filename: str, value: dict, *, replace: bool) -> None:
    """Write complete fsynced private bytes, then atomically publish them; never follow, truncate or replace a link."""
    body = (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")
    try:
        existing = os.stat(filename, dir_fd=held, follow_symlinks=False)
    except FileNotFoundError:
        existing = None
    if existing is not None and not replace:
        raise FileExistsError(errno.EEXIST, "integration evidence record already exists")
    if existing is not None and (not stat.S_ISREG(existing.st_mode) or existing.st_nlink != 1):
        raise ValueError("integration evidence must be a private regular file")
    temporary = ".evidence-" + uuid.uuid4().hex + ".tmp"
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_NONBLOCK,
                         mode=0o600, dir_fd=held)
    try:
        write_complete_evidence(descriptor, body)
        if replace:
            os.replace(temporary, filename, src_dir_fd=held, dst_dir_fd=held)
        else:
            rename_without_replacement(held, temporary, filename)
    finally:
        try:
            os.unlink(temporary, dir_fd=held)
        except FileNotFoundError:
            pass


def write_complete_evidence(descriptor: int, body: bytes) -> None:
    """Finish and fsync a new private evidence snapshot before its name can change."""
    with os.fdopen(descriptor, "wb") as output:
        observed = os.fstat(output.fileno())
        if not stat.S_ISREG(observed.st_mode) or observed.st_nlink != 1 or observed.st_mode & 0o077:
            raise ValueError("integration evidence must be a private regular file")
        if output.write(body) != len(body):
            raise ValueError("integration evidence write was incomplete")
        output.flush()
        os.fsync(output.fileno())


def write_evidence(directory: Path, filename: str, value: dict) -> None:
    if filename not in EVIDENCE_FILES:
        raise ValueError("integration evidence filename is not allowlisted")
    held = evidence_directory(directory)
    try:
        publish_json(held, filename, value, replace=True)
    finally:
        os.close(held)


def write_cleanup_record(directory: Path, value: dict, filename: str | None = None) -> str:
    """Publish one exact-down attempt under its own name, or atomically complete that same attempt's record."""
    held = evidence_directory(directory)
    try:
        if filename is not None:
            if filename not in {CLEANUP_RECORD.format(number) for number in range(1, MAX_CLEANUP_RECORDS + 1)}:
                raise ValueError("cleanup evidence record name is not allowlisted")
            publish_json(held, filename, value, replace=True)
            return filename
        for number in range(1, MAX_CLEANUP_RECORDS + 1):
            candidate = CLEANUP_RECORD.format(number)
            try:
                publish_json(held, candidate, value, replace=False)
            except FileExistsError:
                continue
            return candidate
        raise ValueError("cleanup evidence attempt limit reached")
    finally:
        os.close(held)


def stderr_record(stderr) -> dict:
    """Retain bounded, redacted, printable stderr with its full size and digest; never raw credentials."""
    if stderr is None:
        body = b""
    elif isinstance(stderr, bytes):
        body = stderr
    else:
        body = str(stderr).encode("utf-8", "replace")
    text = body.decode("utf-8", "replace")
    # Redact the whole text first, so a secret that straddles the excerpt boundary cannot survive the slice.
    redacted = OPAQUE_TEXT.sub("<redacted>", SECRET_TEXT.sub(lambda match: match.group(1) + " <redacted>", text))
    tail = redacted[-STDERR_EXCERPT_CHARACTERS:]
    printable = "".join(character if character in "\n\t" or 32 <= ord(character) < 127 else "?" for character in tail)
    return {"bytes": len(body), "sha256": hashlib.sha256(body).hexdigest(), "excerpt": printable,
            "truncated": len(tail) < len(redacted), "redacted": True}


def parsed_output(output: str):
    """Parse tool JSON; non-JSON or non-object output is an explicit unverified observation, never an exception."""
    try:
        value = read_json(output)
    except (ValueError, TypeError, RecursionError):
        return None
    return value if isinstance(value, dict) else None


def read_private_artifact(directory: int, filename: str, maximum: int) -> bytes:
    """Read a bounded private regular file relative to an already held owner directory."""
    descriptor = os.open(filename, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
    with os.fdopen(descriptor, "rb") as stream:
        before = os.fstat(stream.fileno())
        if (
            not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_uid != os.geteuid()
            or before.st_mode & 0o077 or not 0 < before.st_size <= maximum
        ):
            raise ValueError("declared diagnostic is not a bounded private regular file")
        body = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
        if len(body) != before.st_size or any(
            getattr(before, key) != getattr(after, key)
            for key in ("st_dev", "st_ino", "st_mode", "st_nlink", "st_uid", "st_size", "st_mtime_ns", "st_ctime_ns")
        ):
            raise ValueError("declared diagnostic changed during collection")
        return body


def promote_diagnostic(directory: int, temporary: str, filename: str) -> None:
    """Atomically move complete diagnostic bytes without replacement or a two-hard-link interruption window."""
    rename_without_replacement(directory, temporary, filename)


def rename_without_replacement(directory: int, temporary: str, filename: str) -> None:
    """Linux renameat2(RENAME_NOREPLACE) within one held directory."""
    if sys.platform != "linux":
        raise OSError(errno.ENOSYS, "atomic diagnostic publication requires the supported Linux runner")
    try:
        rename = ctypes.CDLL(None, use_errno=True).renameat2
    except AttributeError:
        raise OSError(errno.ENOSYS, "atomic diagnostic publication is unavailable") from None
    rename.argtypes = (ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint)
    rename.restype = ctypes.c_int
    # Linux RENAME_NOREPLACE=1: https://man7.org/linux/man-pages/man2/rename.2.html
    if rename(directory, temporary.encode("ascii"), directory, filename.encode("ascii"), 1) != 0:
        raise OSError(ctypes.get_errno(), "atomic diagnostic publication refused")


def write_complete_diagnostic(descriptor: int, body: bytes) -> None:
    """Finish and fsync a new private snapshot before its name can become final."""
    with os.fdopen(descriptor, "wb") as output:
        observed = os.fstat(output.fileno())
        if not stat.S_ISREG(observed.st_mode) or observed.st_nlink != 1 or observed.st_mode & 0o077:
            raise ValueError("retained diagnostic is not a private regular file")
        if output.write(body) != len(body):
            raise ValueError("diagnostic write was incomplete")
        output.flush()
        os.fsync(output.fileno())


def write_diagnostic(evidence: Path, filename: str, body: bytes, maximum: int) -> None:
    """Publish immutable sanitized bytes; an existing identical private file is a retry."""
    if not any(re.fullmatch(prefix + r"-[a-f0-9]{64}" + re.escape(suffix), filename, re.ASCII)
               for prefix, suffix, _ in DIAGNOSTICS.values()):
        raise ValueError("diagnostic filename is not a published content-addressed form")
    workspace = job_root("GITHUB_WORKSPACE")
    evidence = bounded_path(evidence, workspace)
    if not evidence.is_relative_to(workspace / "TestResults"):
        raise ValueError("integration evidence must stay under workspace/TestResults")
    held = open_directory(workspace, evidence)
    temporary = ".diagnostic-" + uuid.uuid4().hex + ".tmp"
    created = False
    try:
        try:
            existing = read_private_artifact(held, filename, maximum)
        except FileNotFoundError:
            pass
        else:
            if existing != body:
                raise ValueError("retained diagnostic differs from its immutable content")
            return
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_NONBLOCK,
                             mode=0o600, dir_fd=held)
        created = True
        write_complete_diagnostic(descriptor, body)
        try:
            promote_diagnostic(held, temporary, filename)
        except FileExistsError:
            if read_private_artifact(held, filename, maximum) != body:
                raise ValueError("concurrent diagnostic publication differs from its immutable content")
    finally:
        try:
            try:
                if created:
                    os.unlink(temporary, dir_fd=held)
            except FileNotFoundError:
                pass
        finally:
            os.close(held)


def validate_diagnostic_owner(result: dict, owner: dict) -> None:
    """Require the published result's exact receipt-bound package owner."""
    bindings = result.get("bindings", {})
    if not isinstance(bindings, dict) or any(not isinstance(bindings.get(key), dict) for key in ("run", "environment")):
        raise ValueError("declared diagnostic bindings are unavailable")
    if (
        result.get("schema") != "hexalith.test-result.v1"
        or result.get("policyId") != "hexalith.platform.candidate-and-attachment-policy.v1"
        or any(bindings.get("run", {}).get(key) != owner[key] for key in ("runId", "attemptId", "environmentId"))
        or bindings["run"].get("ownerRunId") != owner["runId"] or bindings["run"].get("role") != "owner"
        or bindings["environment"].get("mode") != "package"
        or bindings.get("environment", {}).get("executionContext") != owner["executionContext"]
    ):
        raise ValueError("declared diagnostics belong to a foreign result")


def declared_diagnostic_reference(reference: dict, identities: set[str], filenames: set[str]) -> tuple[str, str, int, str]:
    """Validate one unique published redacted content-addressed reference."""
    if not isinstance(reference, dict) or set(reference) != {"id", "classification", "redacted", "reference", "contentIdentity"}:
        raise ValueError("declared diagnostic does not match the published fields")
    identity = reference["id"]
    if not isinstance(identity, str) or identity not in DIAGNOSTICS or identity in identities:
        raise ValueError("declared diagnostic identity is unsupported or duplicated")
    prefix, suffix, maximum = DIAGNOSTICS[identity]
    filename = reference["reference"]
    match = re.fullmatch(prefix + r"-([a-f0-9]{64})" + re.escape(suffix), filename, re.ASCII) if isinstance(filename, str) else None
    if (
        reference["classification"] != "internal" or reference["redacted"] is not True
        or match is None or reference["contentIdentity"] != "sha256:" + match[1] or filename in filenames
    ):
        raise ValueError("declared diagnostic classification, filename or digest is invalid")
    return identity, filename, maximum, match[1]


def declared_diagnostics(result: dict, owner: dict) -> list[tuple[str, str, int, str]]:
    """Accept only the published redacted diagnostic IDs bound to this receipt's owner."""
    references = result.get("diagnostics", [])
    if not isinstance(references, list) or len(references) > len(DIAGNOSTICS):
        raise ValueError("declared diagnostic count is invalid")
    if not references:
        return []
    validate_diagnostic_owner(result, owner)
    selected = []
    identities = set()
    filenames = set()
    for reference in references:
        identity, filename, maximum, digest = declared_diagnostic_reference(reference, identities, filenames)
        identities.add(identity)
        filenames.add(filename)
        selected.append((identity, filename, maximum, digest))
    return selected


def retain_artifacts(owner: dict, evidence: Path) -> None:
    directory = Path(owner["stateDirectory"]) / "diagnostics" / owner["runId"]
    try:
        held = open_directory(Path(directory.anchor), directory, create=False)
    except FileNotFoundError:
        return
    try:
        result = None
        for filename in ARTIFACTS:
            try:
                body = read_private_artifact(held, filename, FIXED_METADATA_BYTES)
            except FileNotFoundError:
                continue
            value = read_json(body.decode("utf-8"))
            write_evidence(evidence, filename, value)
            if filename == "test-result.json":
                result = value
        retained = []
        for identity, filename, maximum, digest in declared_diagnostics(result or {}, owner):
            body = read_private_artifact(held, filename, maximum)
            if hashlib.sha256(body).hexdigest() != digest:
                raise ValueError("declared diagnostic bytes differ from their content identity")
            if identity != "native-report" and read_json(body.decode("utf-8")).get("runId") != owner["runId"]:
                raise ValueError("declared diagnostic bytes belong to another owner")
            retained.append((filename, body, maximum))
        for filename, body, maximum in retained:
            write_diagnostic(evidence, filename, body, maximum)
    finally:
        os.close(held)


def cleanup_verified(completed: subprocess.CompletedProcess, result: dict, owner: dict) -> bool:
    if not isinstance(result, dict):
        return False
    remaining = result.get("remainingResources")
    outcome = result.get("outcome")
    inventories = ("containerIds", "processIds", "workspaceIds", "claimIds", "discoveryFailures")
    verified = (
        isinstance(remaining, dict)
        and remaining.get("unverified") is False
        and all(remaining.get(key) == [] for key in inventories)
    )
    return (
        completed.returncode == 0
        and result.get("runId") == owner["runId"]
        and isinstance(outcome, dict)
        and outcome.get("exitCode") == "Success"
        and verified
    )


def retained_safely(owner: dict, evidence: Path) -> bool:
    """Retain declared diagnostics after an exact down; a refusal is classified and never hides cleanup."""
    try:
        retain_artifacts(owner, evidence)
    except (OSError, ValueError, KeyError, TypeError, AttributeError, RecursionError) as error:
        print("run-module-integration: declared diagnostic retention refused after exact cleanup ("
              + type(error).__name__ + ")", file=sys.stderr)
        return False
    return True


def process_record_path(receipt: Path) -> Path:
    return receipt.with_name(PROCESS_RECORD)


def boot_identity() -> str:
    try:
        return Path("/proc/sys/kernel/random/boot_id").read_text(encoding="ascii").strip()
    except OSError:
        return ""


def process_identity(pid: int) -> dict | None:
    """Return the live process group, session and start time; exited and zombie processes have none."""
    try:
        text = Path(f"/proc/{pid}/stat").read_text(encoding="ascii", errors="replace")
    except (FileNotFoundError, ProcessLookupError, NotADirectoryError):
        return None
    except PermissionError:
        return None
    fields = text.rpartition(")")[2].split()
    if len(fields) < 20 or fields[0] in ("Z", "X"):
        return None
    return {"parent": int(fields[1]), "processGroup": int(fields[2]), "session": int(fields[3]), "startTime": fields[19]}


def write_process_record(receipt: Path, context: dict, pid: int) -> dict:
    """Durably bind the integration tool's new session to this exact job before the tool may run."""
    identity = process_identity(pid)
    if identity is None or identity["session"] != pid or identity["processGroup"] != pid:
        raise ValueError("integration tool did not start in its own session")
    record = {"schema": PROCESS_SCHEMA, "executionContext": context, "pid": pid, "processGroup": pid, "session": pid,
              "startTime": identity["startTime"], "bootId": boot_identity()}
    path = process_record_path(receipt)
    held = open_directory(job_root("RUNNER_TEMP"), path.parent)
    try:
        publish_json(held, path.name, record, replace=False)
    finally:
        os.close(held)
    return record


def read_process_record(path: Path, context: dict) -> dict:
    record = read_json(real_path(path).read_text(encoding="utf-8"))
    if (
        not isinstance(record, dict) or record.get("schema") != PROCESS_SCHEMA or record.get("executionContext") != context
        or any(not isinstance(record.get(key), int) or record[key] <= 1 for key in ("pid", "processGroup", "session"))
        or not record["pid"] == record["processGroup"] == record["session"]
        or not isinstance(record.get("startTime"), str) or not record["startTime"].isdigit()
    ):
        raise ValueError("integration process record belongs to another job or is malformed")
    return record


def session_identities(record: dict) -> dict[int, dict]:
    """Map every live member of the recorded tool session to its identity; a reused session or other boot has none."""
    if record.get("bootId") and record["bootId"] != boot_identity():
        return {}
    session = record["session"]
    leader = process_identity(session)
    if leader is not None and leader["session"] == session and leader["startTime"] != record["startTime"]:
        # The recorded session ended and its number now belongs to an unrelated session.
        return {}
    members = {}
    for entry in os.scandir("/proc"):
        if entry.name.isdigit():
            identity = process_identity(int(entry.name))
            if identity is not None and identity["session"] == session:
                members[int(entry.name)] = identity
    return members


def recorded_session_members(record: dict) -> dict[int, int]:
    """Map every live member of the recorded tool session to its process group."""
    return {pid: identity["processGroup"] for pid, identity in session_identities(record).items()}


def session_processes(record: dict) -> dict[int, str]:
    """Every live recorded-session process with its start time."""
    return {pid: identity["startTime"] for pid, identity in session_identities(record).items()}


def tool_processes(record: dict) -> dict[int, str]:
    """Return the tool's own processes: the live gated muxer (session leader) and the tool it runs (its direct child)."""
    # Composed processes (AppHost, DCP, the native test host, sidecars) are started by the tool, so they are its
    # descendants and never the leader's direct children. Signals sent here reach only the tool, which records
    # attachment withdrawal before it stops them.
    members = session_identities(record)
    leader = members.get(record["session"])
    if leader is None or leader["startTime"] != record["startTime"]:
        return {}
    return {pid: identity["startTime"] for pid, identity in members.items()
            if pid == record["session"] or identity["parent"] == record["session"]}


def owned_process(pid: int, run_id: str) -> bool:
    """A process started with the owner's run tag in its environment is an owned composed process."""
    try:
        environment = Path(f"/proc/{pid}/environ").read_bytes()
    except OSError:
        return False
    return (OWNER_RUN_TAG + "=" + run_id).encode("ascii") in environment.split(b"\0")


def unowned_survivors(record: dict, run_id: str) -> dict[int, str]:
    """Return the recorded-session processes that are neither owned composed processes nor their descendants."""
    # The exact down stops owned processes after it withdraws attachments; everything else (the tool, its orphans and
    # helpers that could allocate or hold provisioning.lock) must be gone before the finalizer decides.
    members = session_identities(record)
    owned = {pid for pid in members if owned_process(pid, run_id)}

    def protected(pid: int) -> bool:
        seen = set()
        while pid in members and pid not in seen:
            if pid in owned:
                return True
            seen.add(pid)
            pid = members[pid]["parent"]
        return False

    return {pid: identity["startTime"] for pid, identity in members.items() if not protected(pid)}


def signal_processes(processes: dict[int, str], session: int, number: int) -> None:
    """Signal each selected process individually; a departed or reused process identity is skipped."""
    for pid, start in sorted(processes.items()):
        identity = process_identity(pid)
        if identity is None or identity["session"] != session or identity["startTime"] != start:
            continue
        try:
            os.kill(pid, number)
        except ProcessLookupError:
            pass


def terminate_processes(select, session: int, wait: float | None = None) -> bool:
    """Interrupt, then terminate, then kill the currently selected processes; return True once none remains."""
    wait = SESSION_SIGNAL_WAIT_SECONDS if wait is None else wait
    for number in (signal.SIGINT, signal.SIGTERM, signal.SIGKILL):
        targets = select()
        if not targets:
            return True
        signal_processes(targets, session, number)
        deadline = time.monotonic() + wait
        while time.monotonic() < deadline:
            if not select():
                return True
            time.sleep(0.05)
    return not select()


def signal_groups(groups, number: int) -> None:
    for group in sorted(set(groups)):
        try:
            os.killpg(group, number)
        except ProcessLookupError:
            pass


def unverified_allocation(evidence: Path, context: dict, job_status: str, reason: str) -> None:
    """Record that this job's allocation could not be verified, so cleanup stays non-success."""
    write_cleanup_record(evidence, {"schema": CLEANUP_SCHEMA, "status": "allocation-unverified", "reason": reason,
                                    "executionContext": context, "jobStatus": job_status, "verified": False})


def recorded_process(receipt: Path, evidence: Path, context: dict, job_status: str):
    """Return the job's process record, None when no tool was launched, or False when the record cannot be trusted."""
    process = process_record_path(receipt)
    if not (process.exists() or process.is_symlink()):
        return None
    try:
        return read_process_record(process, context)
    except (OSError, ValueError, KeyError, TypeError, AttributeError, RecursionError):
        unverified_allocation(evidence, context, job_status, "integration-process-record-unverified")
        return False


def stopped(record: dict | None, select, evidence: Path, context: dict, job_status: str) -> bool:
    """Terminate the selected recorded-session processes; survivors leave the allocation unverified."""
    if record is None or terminate_processes(select, record["session"]):
        return True
    unverified_allocation(evidence, context, job_status, "integration-process-survived")
    return False


def finalize(root: Path, receipt: Path, evidence: Path, timeout: int = DOWN_TIMEOUT_SECONDS, job_status: str = "") -> int:
    context = ci_context()
    root, receipt, evidence = checked_paths(root, receipt, evidence)
    if job_status not in ("", "success", "failure", "cancelled"):
        raise ValueError("unsupported CI finalizer job status")
    record = recorded_process(receipt, evidence, context, job_status)
    # The tool could still write its receipt, allocate or hold provisioning.lock, so it always stops first.
    if record is False or not stopped(record, lambda: tool_processes(record), evidence, context, job_status):
        return 1
    if not receipt.exists():
        # Without a receipt nothing was allocated, so every other recorded-session process stops before the decision.
        if not stopped(record, lambda: session_processes(record), evidence, context, job_status):
            return 1
        write_cleanup_record(evidence, {"schema": CLEANUP_SCHEMA, "status": "not-allocated", "executionContext": context,
                                        "jobStatus": job_status, "verified": True})
        return 0
    owner = receipt_owner(receipt, context)
    # Owned composed processes stay for the exact down, which withdraws attachments before it stops them.
    if not stopped(record, lambda: unowned_survivors(record, owner["runId"]), evidence, context, job_status):
        return 1
    verify_tool(root)
    return exact_down(root, owner, evidence, context, job_status, timeout)


def record_cleanup(evidence: Path, value: dict, filename: str | None = None) -> str | None:
    """Best-effort cleanup evidence: a recording failure never prevents or hides the exact down."""
    try:
        return write_cleanup_record(evidence, value, filename)
    except (OSError, ValueError) as error:
        print(f"run-module-integration: cleanup evidence was not recorded ({type(error).__name__})", file=sys.stderr)
        return None


def run_down(command: list[str], *, cwd: Path, env: dict, timeout: int) -> subprocess.CompletedProcess:
    """Run one exact down like subprocess.run, in its own session; a timeout kills that whole session."""
    # Output is decoded with replacement, so non-UTF-8 bytes are an unverified observation rather than an exception.
    child = subprocess.Popen(
        command,  # nosec B603 # nosemgrep -- fixed tool/options; canonical 128-bit run identity only.
        cwd=cwd,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        start_new_session=True,
    )
    try:
        stdout, stderr = child.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        # No orphaned down may keep provisioning.lock against the always() finalizer's retry.
        signal_groups([child.pid], signal.SIGKILL)
        stdout, stderr = child.communicate()
        raise subprocess.TimeoutExpired(command, timeout, output=stdout, stderr=stderr) from None  # nosemgrep -- a record only.
    output, errors = stdout.decode("utf-8", "replace"), stderr.decode("utf-8", "replace")
    return subprocess.CompletedProcess(command, child.returncode, output, errors)  # nosemgrep -- a record, nothing executes.


def exact_down(root: Path, owner: dict, evidence: Path, context: dict, job_status: str, timeout: int) -> int:
    """Run one exact down for the receipt owner; its evidence is recorded around it but never gates it."""
    environment = os.environ.copy()
    environment["HEXALITH_CI_JOB_STATUS"] = job_status
    run_id = uuid.UUID(hex=owner["runId"]).hex
    command = TOOL + ["down", "--run-id=" + run_id, "--output=json"]
    attempt = {"schema": CLEANUP_SCHEMA, "status": "attempted", "runId": run_id, "executionContext": context,
               "jobStatus": job_status, "processExitCode": None, "outputParsed": False, "verified": False}
    record_name = record_cleanup(evidence, attempt)
    recorded = record_name is not None
    try:
        completed = run_down(command, cwd=root, env=environment, timeout=timeout)
    except subprocess.TimeoutExpired as expired:
        record_cleanup(evidence, {**attempt, "status": "timed-out", "timeoutSeconds": timeout,
                                  "stderr": stderr_record(expired.stderr)}, record_name)
        retained_safely(owner, evidence)
        return 1
    except OSError as error:
        # A missing or unexecutable tool is unverified cleanup; declared diagnostics are still retained.
        record_cleanup(evidence, {**attempt, "status": "tool-unavailable", "reason": type(error).__name__}, record_name)
        retained_safely(owner, evidence)
        return 1
    observed = {**attempt, "status": "completed", "processExitCode": completed.returncode,
                "stderr": stderr_record(completed.stderr)}
    record_name = record_cleanup(evidence, observed, record_name) or record_name
    result = parsed_output(completed.stdout)
    verified = result is not None and cleanup_verified(completed, result, owner)
    recorded = record_cleanup(evidence, {**observed, "outputParsed": result is not None, "result": result,
                                         "verified": verified}, record_name) is not None and recorded
    if not retained_safely(owner, evidence):
        return 1
    return 0 if verified and recorded else 1


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


def bounded_output(stream) -> str:
    stream.seek(0)
    return stream.read(STDOUT_BYTES + 1)[:STDOUT_BYTES].decode("utf-8", "replace")


def wait_for_child(child, timeout: float) -> bool:
    try:
        child.wait(timeout=max(timeout, 0))
    except subprocess.TimeoutExpired:
        return False
    return True


def execution_outcome(returncode: int | None, result: dict | None, receipt: Path) -> int:
    """A zero exit counts only with a receipt and a parsed Success outcome."""
    if returncode:
        return returncode
    outcome = result.get("outcome") if isinstance(result, dict) else None
    return 0 if returncode == 0 and receipt.exists() and isinstance(outcome, dict) and outcome.get("exitCode") == "Success" else 1


class ToolSession:
    """One gated tool session: records it before launch, signals only the tool's own processes, bounds every stop."""

    def __init__(self):
        """Start with no tool process, no observed signal and nothing released or sent."""
        self.child = None
        self.process = None
        self.signals = []
        self.released = False
        self.interrupted = False
        self.terminated = False

    def signal_tool(self, number: int) -> None:
        """Signal the gated muxer and the tool it runs, never the composed processes the tool started."""
        signal_processes(tool_processes(self.process), self.process["session"], number)

    def handle(self, signum, frame):
        """Record one runner signal and forward it at once."""
        self.signals.append({"signal": signum, "observedAt": time.monotonic()})
        self.forward()

    def forward(self) -> None:
        """Deliver the runner's cancellation to the released tool only, escalating to SIGTERM on a second signal."""
        if self.child is None or not self.released or self.child.poll() is not None:
            return
        if self.signals and not self.interrupted:
            self.interrupted = True
            self.signal_tool(signal.SIGINT)
        if len(self.signals) > 1:
            self.terminate()

    def terminate(self) -> None:
        """Send SIGTERM to the tool once."""
        if not self.terminated:
            self.terminated = True
            self.signal_tool(signal.SIGTERM)

    def launch(self, command: list[str], root: Path, receipt: Path, context: dict, output, errors) -> None:
        """Start the gate in a new session and release the tool only after its job-bound record is durable."""
        self.child = subprocess.Popen(
            GATE + command,  # nosec B603 # nosemgrep -- fixed gate and tool/verb; checked, equals-bound option values only.
            cwd=root,
            stdin=subprocess.PIPE,
            stdout=output,
            stderr=errors,
            start_new_session=True,
        )
        try:
            self.process = write_process_record(receipt, context, self.child.pid)
            if not self.signals:
                self.child.stdin.write(b"1\n")
                self.child.stdin.flush()
                self.released = True
        finally:
            self.child.stdin.close()

    def cancellation_settled(self) -> bool:
        """Escalate on the tool inside the runner grace; return once the final kill was sent."""
        # Composed processes are never signalled here: the always() finalizer's exact down withdraws attachments before
        # it stops whatever the tool left behind. Only the runner's own process-tree kill after its grace can precede that.
        self.forward()
        elapsed = time.monotonic() - self.signals[0]["observedAt"]
        if elapsed >= CANCELLATION_FINAL_SECONDS:
            if self.released:
                self.signal_tool(signal.SIGKILL)
            wait_for_child(self.child, 0.5)
            return True
        if elapsed >= CANCELLATION_ESCALATION_SECONDS:
            self.terminate()
        return False

    def wait(self, timeout: int) -> bool:
        """Wait for the tool; return True only when the helper's own timeout expired."""
        deadline = time.monotonic() + timeout
        while not wait_for_child(self.child, 0.1):
            if self.signals:
                if self.cancellation_settled():
                    return False
            elif time.monotonic() >= deadline:
                return True
        return False

    def stop_after_timeout(self) -> None:
        """A timeout is not a cancellation: stop the tool here; the helper's exact down then withdraws and cleans."""
        self.signal_tool(signal.SIGTERM)
        if not wait_for_child(self.child, TIMEOUT_STOP_SECONDS):
            self.signal_tool(signal.SIGKILL)
            wait_for_child(self.child, POST_KILL_WAIT_SECONDS)

    def record(self, evidence: Path, context: dict, timed_out: bool, output, errors):
        """Write execution evidence before parsing the tool output, then complete it with the parsed result."""
        observed = {"schema": EXECUTION_SCHEMA, "executionContext": context, "processExitCode": self.child.poll(),
                    "launched": self.released, "cancelled": bool(self.signals), "timedOut": timed_out,
                    "signals": [item["signal"] for item in self.signals], "outputParsed": False, "result": None,
                    "stderr": stderr_record(bounded_output(errors))}
        write_evidence(evidence, "execution.json", observed)
        result = parsed_output(bounded_output(output))
        write_evidence(evidence, "execution.json", {**observed, "outputParsed": result is not None, "result": result})
        return result


def execute_integration(command: list[str], root: Path, receipt: Path, evidence: Path, timeout: int,
                        context: dict | None = None) -> int:
    """Run the tool in its own recorded session; cancellation stays inside the runner grace, timeouts finalize here."""
    context = context or ci_context()
    session = ToolSession()
    previous = {sig: signal.signal(sig, session.handle) for sig in (signal.SIGINT, signal.SIGTERM)}
    temporary = job_root("RUNNER_TEMP")
    try:
        with tempfile.TemporaryFile(dir=temporary) as output, tempfile.TemporaryFile(dir=temporary) as errors:
            session.launch(command, root, receipt, context, output, errors)
            timed_out = session.wait(timeout)
            if timed_out:
                session.stop_after_timeout()
            result = session.record(evidence, context, timed_out, output, errors)
        if session.signals:
            # Exact down after cancellation belongs to the always() finalizer; nothing here outlasts the runner grace.
            return 130
        cleanup = finalize(root, receipt, evidence, job_status="")
        if timed_out:
            return 124
        return execution_outcome(session.child.returncode, result, receipt) or cleanup
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


def qualification_owners(owners: list[str]) -> list[str]:
    """Canonicalize the explicit additive reference-owner set before any effects."""
    if len(owners) > 3 or len(set(owners)) != len(owners) or any(
        owner not in {"eventstore", "tenants", "memories"} for owner in owners
    ):
        raise ValueError("qualification owners must be unique supported reference identities")
    return sorted(owners)


def run_integration(
    root: Path, manifest: str, profiles: list[str], receipt: Path, evidence: Path, timeout: int = DEFAULT_TIMEOUT_SECONDS,
    owners: list[str] | None = None,
) -> int:
    owners = qualification_owners(owners or [])
    if (not isinstance(timeout, int) or isinstance(timeout, bool)
            or not MINIMUM_TIMEOUT_SECONDS <= timeout <= MAXIMUM_TIMEOUT_SECONDS):
        raise ValueError("integration timeout must be a whole number of seconds from 60 to 86400")
    context = ci_context()
    root, receipt, evidence = checked_paths(root, receipt, evidence)
    manifest_path = selected_manifest(root, manifest, profiles)
    verify_tool(root)
    verify_preparation()
    if receipt.exists() or receipt.is_symlink() or process_record_path(receipt).exists() or process_record_path(receipt).is_symlink():
        raise ValueError("each CI integration invocation requires a fresh owner receipt")
    held = open_directory(job_root("RUNNER_TEMP"), receipt.parent)
    os.close(held)
    write_evidence(
        evidence,
        "invocation.json",
        {"executionContext": context, "profiles": profiles, "classification": "baseline-only", "timeoutSeconds": timeout,
         **({"qualificationOwners": owners} if owners else {})},
    )
    command = TOOL + [
        "test",
        "--manifest=" + str(manifest_path),
        "--mode=package",
        "--ci-owner-receipt=" + str(receipt),
        "--output=json",
    ]
    command += ["--profile=" + profile for profile in profiles]
    command += ["--with-owner=" + owner for owner in owners]
    return execute_integration(command, root, receipt, evidence, timeout, context)


def derived_timeout(step_minutes: int, job_minutes: int, job_started: int, now: float | None = None) -> int:
    """Fit the helper timeout and every bounded wait after it inside the step and the job's actual remaining time."""
    values = (step_minutes, job_minutes, job_started)
    if any(not isinstance(value, int) or isinstance(value, bool) or value <= 0 for value in values):
        raise ValueError("integration budget inputs must be positive whole numbers")
    now = time.time() if now is None else now
    if not 0 <= now - job_started <= job_minutes * 60:
        raise ValueError("the recorded job start is not within this job's timeout")
    step = step_minutes * 60 - STEP_RESERVE_SECONDS
    job = job_started - JOB_SETUP_ALLOWANCE_SECONDS + job_minutes * 60 - now - STEP_RESERVE_SECONDS - FINALIZER_STEP_SECONDS
    timeout = int(min(step, job, MAXIMUM_TIMEOUT_SECONDS))
    if timeout < MINIMUM_TIMEOUT_SECONDS:
        raise ValueError("the job's remaining time cannot hold the minimum integration timeout and its exact cleanup")
    return timeout


def integration_timeout(args) -> int:
    """Use the explicit timeout, or derive it from the step and job budgets; never both."""
    budget = (args.step_timeout_minutes, args.job_timeout_minutes, args.job_started_epoch)
    if all(value is None for value in budget):
        return DEFAULT_TIMEOUT_SECONDS if args.timeout_seconds is None else args.timeout_seconds
    if args.timeout_seconds is not None or any(value is None for value in budget):
        raise ValueError("derive the integration timeout from all of the step and job budget inputs, or pass it alone")
    return derived_timeout(*budget)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("run", "finalize"))
    parser.add_argument("--job-status", choices=("", "success", "failure", "cancelled"), default="")
    parser.add_argument("--manifest", default="")
    parser.add_argument("--profiles", default="")
    parser.add_argument("--owners", default="")
    parser.add_argument("--timeout-seconds", type=int, default=None)
    parser.add_argument("--step-timeout-minutes", type=int, default=None)
    parser.add_argument("--job-timeout-minutes", type=int, default=None)
    parser.add_argument("--job-started-epoch", type=int, default=None)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.operation == "finalize":
            if args.owners:
                raise ValueError("qualification owners apply only to a fresh test invocation")
            if any(value is not None for value in (args.timeout_seconds, args.step_timeout_minutes,
                                                   args.job_timeout_minutes, args.job_started_epoch)):
                raise ValueError("the integration timeout applies only to a fresh test invocation")
            return finalize(Path.cwd(), args.receipt, args.evidence, job_status=args.job_status)
        timeout = integration_timeout(args)
        return run_integration(Path.cwd(), args.manifest, args.profiles.split(), args.receipt, args.evidence,
                               timeout=timeout, owners=args.owners.split())
    except (OSError, ValueError, KeyError, TypeError, AttributeError, RecursionError, subprocess.SubprocessError) as error:
        print(f"run-module-integration: {type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
