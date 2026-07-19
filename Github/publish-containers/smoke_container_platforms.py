#!/usr/bin/env python3
"""Run the same bounded EventStore liveness smoke for both OCI child digests."""

import argparse
import hashlib
import json
import math
import os
import re
import subprocess  # nosec B404 -- commands are validated against strict executable and argument allowlists.
import sys
import time
from pathlib import Path

from oci_registry_validator import (
    ValidationError,
    _parse_image,
    validated_image_reference,
    workspace_make_directory,
    workspace_output_directory,
    workspace_read_bytes,
    workspace_read_text,
    workspace_write_text,
)


ARM64_PLATFORM = "linux/arm64"
REQUIRED_PLATFORMS = ("linux/amd64", ARM64_PLATFORM)
SHA256_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
EMULATION_FAILURE_PATTERN = re.compile(r"exec format|qemu|binfmt", re.IGNORECASE)
SAFE_COMMAND_ARGUMENT_PATTERN = re.compile(r"^[A-Za-z0-9_./:@+=,%{}|-]+$")
EMULATION_SETUP_FAILURE = "environment/emulation-setup-failure"
REGISTRY_PULL_FAILURE = "registry-pull-failure"
CLEANUP_FAILURE = "cleanup-failure"
SMOKE_EXECUTABLES = {"curl", "docker"}
PULL_TIMEOUT_SECONDS = 120
DIAGNOSTIC_LIMIT = 2048


class SmokeFailure(Exception):  # noqa: D203,D211
    """A support-safe smoke orchestration failure."""


def _run(command, timeout=30):
    if (
        not isinstance(command, (list, tuple))
        or not command
        or command[0] not in SMOKE_EXECUTABLES
        or any(
            not isinstance(argument, str) or SAFE_COMMAND_ARGUMENT_PATTERN.fullmatch(argument) is None
            for argument in command
        )
    ):
        raise SmokeFailure("Smoke command contains an unsupported argument.")
    try:
        return subprocess.run(  # nosec B603 -- command and arguments passed strict allowlists above.
            command,
            capture_output=True,
            text=True,
            check=False,
            timeout=timeout,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SmokeFailure("A required local smoke tool could not complete.") from error


def _load_children(evidence_directory):
    path = evidence_directory / "oci-validation.json"
    try:
        evidence = json.loads(workspace_read_text(path))
    except (OSError, json.JSONDecodeError) as error:
        raise SmokeFailure("Validated OCI evidence is required before smoke execution.") from error
    if evidence.get("result") != "pass" or evidence.get("platforms") != list(REQUIRED_PLATFORMS):
        raise SmokeFailure("OCI evidence does not contain the exact validated platform set.")
    children = evidence.get("children")
    if not isinstance(children, list) or len(children) != 2:
        raise SmokeFailure("OCI evidence does not contain exactly two child descriptors.")
    result = {}
    for child in children:
        if not isinstance(child, dict):
            raise SmokeFailure("OCI child evidence is malformed.")
        platform = child.get("platform")
        digest = child.get("digest")
        if platform not in REQUIRED_PLATFORMS or not isinstance(digest, str):
            raise SmokeFailure("OCI child evidence is malformed.")
        if SHA256_PATTERN.fullmatch(digest) is None or platform in result:
            raise SmokeFailure("OCI child evidence has an invalid or duplicate digest binding.")
        result[platform] = digest
    if set(result) != set(REQUIRED_PLATFORMS):
        raise SmokeFailure("OCI child evidence is missing a required platform.")
    return result


def _repository_without_tag(image):
    try:
        registry, repository, _ = _parse_image(image)
    except ValidationError as error:
        raise SmokeFailure("Image must include registry, repository, and mutable tag.") from error
    return f"{registry}/{repository}"


def _write_log(evidence_directory, name, lines):
    path = evidence_directory / name
    workspace_write_text(path, "\n".join(lines) + "\n")
    return path


def _write_summary(evidence_directory, summary, log_paths):
    summary_path = evidence_directory / "smoke-results.json"
    workspace_write_text(summary_path, json.dumps(summary, indent=2, sort_keys=True) + "\n")
    all_paths = [*log_paths, summary_path]
    hashes = []
    for path in sorted(all_paths, key=lambda item: item.name):
        hashes.append(f"{hashlib.sha256(workspace_read_bytes(path)).hexdigest()}  {path.name}")
    workspace_write_text(evidence_directory / "smoke-sha256.txt", "\n".join(hashes) + "\n")


def _support_safe(value):
    bounded = (value or "")[:DIAGNOSTIC_LIMIT]
    bounded = re.sub(r"(?i)bearer\s+\S+", "Bearer [redacted]", bounded)
    bounded = re.sub(
        r"(?i)(authorization|token|password|secret|api[_-]?key)=\S+",
        r"\1=[redacted]",
        bounded,
    )
    return " | ".join(line.strip() for line in bounded.splitlines() if line.strip())


def _run_or_none(command, timeout=30):
    try:
        return _run(command, timeout=timeout)
    except SmokeFailure:
        return None


def _failed(result):
    return result is None or result.returncode != 0


def _pull_immutable(platform, immutable_image):
    result = _run_or_none(
        ["docker", "pull", "--platform", platform, immutable_image],
        timeout=PULL_TIMEOUT_SECONDS,
    )
    return REGISTRY_PULL_FAILURE if _failed(result) else "pass"


def _classify_start_failure(result):
    stderr = result.stderr if result is not None else ""
    if EMULATION_FAILURE_PATTERN.search(stderr or "") is not None:
        return EMULATION_SETUP_FAILURE
    return "image-start-failure"


def _preflight(repository, arm64_digest, evidence_directory):
    immutable_image = f"{repository}@{arm64_digest}"
    buildx = _run_or_none(["docker", "buildx", "inspect", "--bootstrap"])
    if _failed(buildx) or ARM64_PLATFORM not in buildx.stdout:
        outcome = EMULATION_SETUP_FAILURE
    else:
        outcome = _pull_immutable(ARM64_PLATFORM, immutable_image)
    if outcome == "pass":
        executable = _run_or_none(
            [
                "docker",
                "run",
                "--rm",
                "--platform",
                ARM64_PLATFORM,
                "--entrypoint",
                "/bin/uname",
                immutable_image,
                "-m",
            ]
        )
        if _failed(executable):
            outcome = _classify_start_failure(executable)
        elif executable.stdout.strip() not in {"aarch64", "arm64"}:
            outcome = EMULATION_SETUP_FAILURE
    log_path = _write_log(
        evidence_directory,
        "smoke-preflight.log",
        [
            f"check={ARM64_PLATFORM}-runtime-capability",
            f"image={immutable_image}",
            f"outcome={outcome}",
        ],
    )
    return outcome, log_path


def _start_container(platform, container_name, immutable_image):
    start = _run_or_none(
        [
            "docker",
            "run",
            "--detach",
            "--platform",
            platform,
            "--publish",
            "127.0.0.1::8080",
            "--env",
            "ASPNETCORE_URLS=http://+:8080",
            "--name",
            container_name,
            immutable_image,
        ]
    )
    return _classify_start_failure(start) if _failed(start) else "pass"


def _container_state(container_name):
    inspect = _run_or_none(
        ["docker", "inspect", "--format", "{{.State.Status}}|{{.State.ExitCode}}", container_name]
    )
    if _failed(inspect):
        return None
    return _support_safe(inspect.stdout)


def _liveness_result(url):
    return _run_or_none(
        [
            "curl",
            "--silent",
            "--show-error",
            "--max-time",
            "5",
            "--output",
            "/dev/null",
            "--write-out",
            "%{http_code}",
            url,
        ],
        timeout=10,
    )


def _poll_liveness(container_name, timeout_seconds, interval_seconds, log_lines):
    port_result = _run_or_none(["docker", "port", container_name, "8080/tcp"])
    port_output = port_result.stdout if not _failed(port_result) else ""
    port_match = re.search(r"127\.0\.0\.1:(\d+)", port_output)
    if port_match is None:
        return "image-start-failure", 0
    url = f"http://127.0.0.1:{port_match.group(1)}/alive"
    deadline = time.monotonic() + timeout_seconds
    attempts = 0
    while True:
        attempts += 1
        liveness = _liveness_result(url)
        if not _failed(liveness) and re.fullmatch(r"2\d\d", liveness.stdout.strip()):
            return "pass", attempts
        state = _container_state(container_name)
        if state:
            log_lines.append(f"container_state={state}")
            if not state.startswith("running|"):
                return "image-start-failure", attempts
        if time.monotonic() >= deadline:
            return "liveness-timeout", attempts
        time.sleep(interval_seconds)


def _append_runtime_evidence(container_name, outcome, log_lines):
    state = _container_state(container_name)
    log_lines.append(f"container_state={state}" if state else "container_state=unavailable")
    if outcome == "pass":
        return
    diagnostics = _run_or_none(["docker", "logs", "--tail", "50", container_name])
    if _failed(diagnostics):
        log_lines.append("diagnostics=unavailable")
        return
    safe_diagnostics = _support_safe(diagnostics.stdout + diagnostics.stderr)
    log_lines.append(f"diagnostic_sha256={hashlib.sha256(safe_diagnostics.encode()).hexdigest()}")
    if safe_diagnostics:
        log_lines.append(f"diagnostic_excerpt={safe_diagnostics}")


def _remove_container(container_name):
    removal = _run_or_none(["docker", "rm", "--force", container_name])
    return "failure" if _failed(removal) else "pass"


def _smoke_platform(repository, platform, digest, evidence_directory, timeout_seconds, interval_seconds):
    architecture = platform.split("/", 1)[1]
    container_name = f"hexalith-eventstore-smoke-{architecture}-{os.getpid()}"
    immutable_image = f"{repository}@{digest}"
    log_lines = [f"platform={platform}", f"image={immutable_image}"]
    outcome = _pull_immutable(platform, immutable_image)
    cleanup = "not-required"
    attempts = 0
    started = False
    if outcome == "pass":
        outcome = _start_container(platform, container_name, immutable_image)
        started = outcome == "pass"
        cleanup = "pending" if started else cleanup
    try:
        if outcome == "pass":
            outcome, attempts = _poll_liveness(
                container_name,
                timeout_seconds,
                interval_seconds,
                log_lines,
            )
    finally:
        if started:
            _append_runtime_evidence(container_name, outcome, log_lines)
            cleanup = _remove_container(container_name)
    if cleanup == "failure" and outcome == "pass":
        outcome = CLEANUP_FAILURE
    log_lines.extend((f"attempts={attempts}", f"cleanup={cleanup}", f"outcome={outcome}"))
    return outcome, _write_log(evidence_directory, f"smoke-linux-{architecture}.log", log_lines), cleanup


def run_smoke(image, evidence_directory, timeout_seconds, interval_seconds):
    """Run the preflight and both digest-pinned platform smokes."""
    evidence_directory = Path(evidence_directory)
    workspace_make_directory(evidence_directory)
    children = _load_children(evidence_directory)
    repository = _repository_without_tag(image)
    preflight_outcome, preflight_log = _preflight(repository, children[ARM64_PLATFORM], evidence_directory)
    if preflight_outcome != "pass":
        summary = {
            "result": preflight_outcome,
            "image_repository": repository,
            "platforms": [],
        }
        _write_summary(evidence_directory, summary, [preflight_log])
        return summary

    platform_results = []
    log_paths = [preflight_log]
    overall_result = "pass"
    for platform in REQUIRED_PLATFORMS:
        outcome, log_path, cleanup = _smoke_platform(
            repository,
            platform,
            children[platform],
            evidence_directory,
            timeout_seconds,
            interval_seconds,
        )
        if overall_result == "pass" and outcome != "pass":
            overall_result = outcome
        platform_results.append(
            {
                "platform": platform,
                "digest": children[platform],
                "outcome": outcome,
                "cleanup": cleanup,
            }
        )
        log_paths.append(log_path)
    summary = {
        "result": overall_result,
        "image_repository": repository,
        "platforms": platform_results,
    }
    _write_summary(evidence_directory, summary, log_paths)
    return summary


def _positive_number(value, name):
    try:
        number = float(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError(f"{name} must be a number.") from error
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError(f"{name} must be greater than zero.")
    return number


def main():
    parser = argparse.ArgumentParser(description="Smoke both immutable OCI child digests.")
    parser.add_argument("--image", required=True, type=validated_image_reference)
    parser.add_argument("--evidence-directory", required=True, type=workspace_output_directory)
    arguments = parser.parse_args()
    try:
        timeout_seconds = _positive_number(
            os.environ.get("HEXALITH_CONTAINER_SMOKE_TIMEOUT_SECONDS", "60"),
            "HEXALITH_CONTAINER_SMOKE_TIMEOUT_SECONDS",
        )
        interval_seconds = _positive_number(
            os.environ.get("HEXALITH_CONTAINER_SMOKE_INTERVAL_SECONDS", "2"),
            "HEXALITH_CONTAINER_SMOKE_INTERVAL_SECONDS",
        )
        summary = run_smoke(
            arguments.image,
            arguments.evidence_directory,
            timeout_seconds,
            interval_seconds,
        )
    except (SmokeFailure, argparse.ArgumentTypeError) as error:
        print(f"[container-smoke] configuration-failure: {error}", file=sys.stderr)
        return 1
    if summary["result"] != "pass":
        print(f"[container-smoke] {summary['result']}", file=sys.stderr)
        return 1
    print("[container-smoke] pass: linux/amd64,linux/arm64")
    return 0


if __name__ == "__main__":
    sys.exit(main())
