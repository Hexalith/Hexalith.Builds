#!/usr/bin/env python3
"""Run the same bounded EventStore liveness smoke for both OCI child digests."""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

from oci_registry_validator import (
    ValidationError,
    _parse_image,
    validated_image_reference,
    workspace_output_directory,
)


REQUIRED_PLATFORMS = ("linux/amd64", "linux/arm64")
SHA256_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
EMULATION_FAILURE_PATTERN = re.compile(r"exec format|qemu|binfmt", re.IGNORECASE)
SAFE_COMMAND_ARGUMENT_PATTERN = re.compile(r"^[A-Za-z0-9_./:@+=,-]+$")
EMULATION_SETUP_FAILURE = "environment/emulation-setup-failure"
SMOKE_EXECUTABLES = {"curl", "docker"}


class SmokeFailure(Exception):
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
        return subprocess.run(
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
        evidence = json.loads(path.read_text(encoding="utf-8"))
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
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def _write_summary(evidence_directory, summary, log_paths):
    summary_path = evidence_directory / "smoke-results.json"
    summary_path.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    all_paths = [*log_paths, summary_path]
    hashes = []
    for path in sorted(all_paths, key=lambda item: item.name):
        hashes.append(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}")
    (evidence_directory / "smoke-sha256.txt").write_text("\n".join(hashes) + "\n", encoding="utf-8")


def _preflight(evidence_directory):
    try:
        result = _run(["docker", "buildx", "inspect", "--bootstrap"])
    except SmokeFailure:
        result = None
    passed = result is not None and result.returncode == 0 and "linux/arm64" in result.stdout
    outcome = "pass" if passed else EMULATION_SETUP_FAILURE
    log_path = _write_log(
        evidence_directory,
        "smoke-preflight.log",
        ["check=linux/arm64-runtime-capability", f"outcome={outcome}"],
    )
    return passed, log_path


def _smoke_platform(repository, platform, digest, evidence_directory, timeout_seconds, interval_seconds):
    architecture = platform.split("/", 1)[1]
    container_name = f"hexalith-eventstore-smoke-{architecture}-{os.getpid()}"
    immutable_image = f"{repository}@{digest}"
    log_lines = [f"platform={platform}", f"image={immutable_image}"]
    outcome = "image-start-failure"
    attempts = 0
    try:
        start = _run(
            [
                "docker",
                "run",
                "--detach",
                "--rm",
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
        if start.returncode != 0:
            if EMULATION_FAILURE_PATTERN.search(start.stderr or "") is not None:
                outcome = EMULATION_SETUP_FAILURE
            log_lines.append(f"outcome={outcome}")
            return outcome, _write_log(evidence_directory, f"smoke-linux-{architecture}.log", log_lines)

        port_result = _run(["docker", "port", container_name, "8080/tcp"])
        port_match = re.search(r"127\.0\.0\.1:(\d+)", port_result.stdout if port_result.returncode == 0 else "")
        if port_match is None:
            log_lines.append("outcome=image-start-failure")
            return "image-start-failure", _write_log(
                evidence_directory,
                f"smoke-linux-{architecture}.log",
                log_lines,
            )

        url = f"http://127.0.0.1:{port_match.group(1)}/alive"
        deadline = time.monotonic() + timeout_seconds
        while True:
            attempts += 1
            liveness = _run(
                ["curl", "--fail", "--silent", "--show-error", "--max-time", "5", url],
                timeout=10,
            )
            if liveness.returncode == 0:
                outcome = "pass"
                break
            if time.monotonic() >= deadline:
                outcome = "liveness-timeout"
                break
            time.sleep(interval_seconds)
        log_lines.extend((f"attempts={attempts}", f"outcome={outcome}"))
        return outcome, _write_log(evidence_directory, f"smoke-linux-{architecture}.log", log_lines)
    finally:
        _run(["docker", "rm", "--force", container_name])


def run_smoke(image, evidence_directory, timeout_seconds, interval_seconds):
    """Run the preflight and both digest-pinned platform smokes."""

    evidence_directory = Path(evidence_directory)
    evidence_directory.mkdir(parents=True, exist_ok=True)
    children = _load_children(evidence_directory)
    repository = _repository_without_tag(image)
    preflight_passed, preflight_log = _preflight(evidence_directory)
    if not preflight_passed:
        summary = {
            "result": EMULATION_SETUP_FAILURE,
            "image_repository": repository,
            "platforms": [],
        }
        _write_summary(evidence_directory, summary, [preflight_log])
        return summary

    platform_results = []
    log_paths = [preflight_log]
    overall_result = "pass"
    for platform in REQUIRED_PLATFORMS:
        outcome, log_path = _smoke_platform(
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
            {"platform": platform, "digest": children[platform], "outcome": outcome}
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
    if number <= 0:
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
