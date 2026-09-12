#!/usr/bin/env python3
"""Fail-closed validator for the G-6 runtime/toolchain qualification packet."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


EXPECTED_TUPLE = {
    "dotnetSdk": "10.0.401",
    "aspireSdk": "13.5.3",
    "aspireCli": "13.5.3",
    "communityToolkitAspireDapr": "13.5.0-preview.1.260825-0345",
    "daprCli": "1.18.0",
    "daprRuntime": "1.18.2",
    "daprDotnetPackages": "1.18.7",
    "fluentUi": "5.0.0-rc.5-26219.1",
    "nSubstitute": "6.2.0",
    "fluxor": "6.11.0",
}
EXPECTED_CONTAINMENT = {
    "g4Approved": False,
    "g5Approved": False,
    "deploymentApproved": False,
    "releaseApproved": False,
}
PACKET_FIELDS = {
    "schema", "baseline", "capturedUtc", "repositories", "artifacts",
    "observedVersions", "testCounts", "topology", "lifecycle", "approval",
    "sentinelScan", "rollback", "containment", "status",
}
FORBIDDEN = (
    re.compile(r"(?i)bearer\s+[a-z0-9._~+/-]+"),
    re.compile(r'''(?ix)["']?(?:password|secret|token)["']?\s*[=:]\s*["']?[^\s,;"']+'''),
    re.compile(r"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"),
    re.compile(r"/home/[^/\s]+/"),
    re.compile(r"/Users/[^/\s]+/"),
    re.compile(r"(?i)[a-z]:\\+Users\\+[^\\\s]+\\+"),
)
SENTINEL_SCANNED_KINDS = {
    "source-state", "observed-versions", "command-record", "dispositions",
    "eventstore-observations", "eventstore-qualification-results",
    "eventstore-support-results", "eventstore-capture-validation",
    "failed-attempt-diagnostic",
}
EXPECTED_ARTIFACT_KINDS = {
    "source-state",
    "baseline-governance",
    "evidence-schema",
    "evidence-validator",
    "mutation-tests",
    "central-catalog",
    "observed-versions",
    "command-record",
    "dispositions",
    "eventstore-observations",
    "eventstore-qualification-results",
    "eventstore-support-results",
    "eventstore-capture-validation",
    "failed-attempt-diagnostic",
}
QUALIFICATION_IDENTITY = (
    "Hexalith.EventStore.Server.LiveSidecar.Tests.Actors.IdempotencyAdmissionOq8PostgresqlTests."
    "ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants"
)
REQUIRED_COMMAND_EXIT_CODES = {
    "install exact Dapr runtime tuple": 0,
    "observe exact tool versions": 0,
    "Builds focused module and evidence tests": 0,
    "G-6 mutation controls": 0,
    "real PostgreSQL two-sidecar stop/restart qualifier": 0,
    "21 exact deterministic support selectors": 0,
    "strict EventStore capture validation": 0,
    "root workflow pin contract": 0,
    "Projects restore and release build": 0,
    "Projects Integration tests": 0,
    "managed restart smoke credential preflight": 1,
    "changed owner AppHost consumption": 0,
    "changed owner pin assertions": 0,
    "shared workflow test-platform contracts": 0,
    "broad package-exception inventory outside G-6 affected set": 1,
}
STRUCTURAL_ERRORS = (KeyError, TypeError, AttributeError, IndexError)
RFC3339_UTC = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z")


class ValidationError(Exception):
    """Raised for one deterministic validation failure."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValidationError(message)


def exact_fields(value: Any, fields: set[str], label: str) -> dict[str, Any]:
    require(isinstance(value, dict), f"{label} must be an object")
    require(set(value) == fields, f"{label} fields drift: expected {sorted(fields)}")
    return value


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_reject_duplicates)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        raise ValidationError(f"Unable to read {path}: {error}") from error
    require(isinstance(value, dict), f"{path} must contain an object")
    return value


def _reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def manifest_digest(files: list[dict[str, str]]) -> str:
    canonical = json.dumps(files, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def require_utc_timestamp(value: Any, label: str) -> None:
    require(isinstance(value, str) and RFC3339_UTC.fullmatch(value) is not None,
            f"{label} must be a strict RFC3339 UTC timestamp")
    try:
        dt.datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as error:
        raise ValidationError(f"{label} is not a valid UTC timestamp") from error


def require_rollback(value: Any, label: str) -> None:
    rollback = exact_fields(value, {"requiresDomainDataMutation", "action"}, label)
    require(rollback["requiresDomainDataMutation"] is False, f"{label} may not mutate domain data")
    require(isinstance(rollback["action"], str) and rollback["action"].strip(),
            f"{label} action must be executable non-empty text")


def comment_free_text(path: Path) -> str:
    text = re.sub(r"<!--.*?-->", "", path.read_text(encoding="utf-8"), flags=re.DOTALL)
    if path.suffix.lower() not in {".yml", ".yaml", ".ps1", ".sh", ".bash"}:
        return text
    active_lines = []
    for line in text.splitlines():
        quote: str | None = None
        escaped = False
        for index, character in enumerate(line):
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character in {"'", '"'}:
                quote = None if quote == character else character if quote is None else quote
            elif character == "#" and quote is None:
                line = line[:index]
                break
        active_lines.append(line)
    return "\n".join(active_lines)


def result_summary(value: Any, label: str) -> dict[str, int]:
    summary = exact_fields(value, {"tests", "passed", "failed", "skipped"}, label)
    for field in ("tests", "passed", "failed", "skipped"):
        require(isinstance(summary[field], int) and not isinstance(summary[field], bool) and summary[field] >= 0,
                f"{label} {field} must be a non-negative integer")
    require(summary["tests"] == summary["passed"] + summary["failed"] + summary["skipped"],
            f"{label} totals do not balance")
    return summary


def resolve_artifact(workspace: Path, value: Any, label: str) -> Path:
    require(isinstance(value, str) and value and not Path(value).is_absolute(), f"{label} path must be repository-relative")
    candidate = (workspace / value).resolve()
    require(candidate.is_relative_to(workspace), f"{label} path escapes workspace")
    require(candidate.is_file(), f"{label} path does not exist: {value}")
    return candidate


def resolve_repository(workspace: Path, value: Any, label: str) -> Path:
    require(isinstance(value, str) and value and not Path(value).is_absolute(),
            f"{label} path must be repository-relative")
    candidate = (workspace / value).resolve()
    require(candidate.is_relative_to(workspace), f"{label} path escapes workspace")
    require(candidate.is_dir(), f"{label} path does not exist: {value}")
    return candidate


def git(repository: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            ["git", "-C", str(repository), *arguments],
            check=False,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except OSError as error:
        raise ValidationError(f"Unable to inspect Git repository {repository}: {error}") from error


def validate_repository_revision(workspace: Path, binding: dict[str, Any], label: str) -> None:
    revision = binding["revision"]
    require(isinstance(revision, str) and re.fullmatch(r"[0-9a-f]{40}", revision) is not None,
            f"{label} revision must be 40 lowercase hex")
    repository = resolve_repository(workspace, binding["path"], label)
    require(git(repository, "rev-parse", "--is-inside-work-tree").stdout.strip() == "true",
            f"{label} path is not a Git worktree")
    require(git(repository, "cat-file", "-e", f"{revision}^{{commit}}").returncode == 0,
            f"{label} revision does not resolve to a commit")
    require(git(repository, "merge-base", "--is-ancestor", revision, "HEAD").returncode == 0,
            f"{label} revision is not the current commit or an ancestor")


def _validate_baseline(workspace: Path, baseline_path: Path) -> dict[str, Any]:
    baseline = read_json(baseline_path)
    exact_fields(
        baseline,
        {"schema", "approvedBy", "approvedOn", "ownerRoles", "tuple", "dispositions", "rollback", "containment", "pinAudit"},
        "Baseline",
    )
    require(baseline["schema"] == "hexalith.runtime-toolchain-baseline.v1", "Baseline schema drift")
    require(baseline["approvedBy"] == "Jérôme Piquot", "Baseline approver drift")
    require(baseline["approvedOn"] == "2026-09-06", "Baseline approval date drift")
    require(baseline["ownerRoles"] == ["Builds", "Platform", "FrontComposer/Web"], "Baseline owner roles drift")
    require(baseline["tuple"] == EXPECTED_TUPLE, "Baseline tuple drift")
    dispositions = baseline["dispositions"]
    require(dispositions.get("dapr") == {
        "supportTableListed": False,
        "listedRuntime": "1.18.0",
        "listedDotnetSdk": "1.18.1",
        "decision": "approved-explicit-exception",
    }, "Dapr support-table exception drift")
    require(dispositions.get("communityToolkitAspireDapr") == "approved-prerelease-exception", "Toolkit prerelease exception missing")
    require(dispositions.get("fluentUi") == "approved-release-candidate-exception", "Fluent UI RC exception missing")
    require(dispositions.get("nSubstitute") == "stable" and dispositions.get("fluxor") == "stable", "Stable package disposition drift")
    require(dispositions.get("Dapr") == "catalog-only-not-activated", "Catalog-only Dapr classification drift")
    require(dispositions.get("Dapr.Workflow") == "catalog-only-unselected", "Dapr.Workflow classification drift")
    require_rollback(baseline["rollback"], "Baseline rollback")
    require(baseline["containment"] == EXPECTED_CONTAINMENT, "Baseline containment drift")

    audit = exact_fields(baseline["pinAudit"], {"globalJson", "appHostProjects", "literalPins"}, "Pin audit")
    for relative in audit["globalJson"]:
        path = resolve_artifact(workspace, relative, "global.json")
        document = read_json(path)
        require(document.get("sdk", {}).get("version") == EXPECTED_TUPLE["dotnetSdk"], f".NET SDK pin drift: {relative}")
    for relative in audit["appHostProjects"]:
        path = resolve_artifact(workspace, relative, "AppHost project")
        project = path.read_text(encoding="utf-8")
        require(f'Aspire.AppHost.Sdk/{EXPECTED_TUPLE["aspireSdk"]}' in project, f"Aspire AppHost SDK pin drift: {relative}")
    for item in audit["literalPins"]:
        exact_fields(item, {"path", "value"}, "Literal pin")
        path = resolve_artifact(workspace, item["path"], "literal pin")
        require(isinstance(item["value"], str) and item["value"].strip(), "Literal pin value is required")
        active_text = comment_free_text(path)
        target = item["value"].strip()
        matched = target in active_text if "\n" in target else target in {line.strip() for line in active_text.splitlines()}
        require(matched, f"Expected active literal pin missing: {item['path']}::{item['value']}")

    packages = resolve_artifact(workspace, "references/Hexalith.Builds/Props/Directory.Packages.props", "central package catalog").read_text(encoding="utf-8")
    package_expectations = {
        "CommunityToolkit.Aspire.Hosting.Dapr": EXPECTED_TUPLE["communityToolkitAspireDapr"],
        "Dapr.Client": EXPECTED_TUPLE["daprDotnetPackages"],
        "Dapr.Workflow": EXPECTED_TUPLE["daprDotnetPackages"],
        "Microsoft.FluentUI.AspNetCore.Components": EXPECTED_TUPLE["fluentUi"],
        "NSubstitute": EXPECTED_TUPLE["nSubstitute"],
        "Fluxor": EXPECTED_TUPLE["fluxor"],
    }
    for package, version in package_expectations.items():
        require(f'Include="{package}" Version="{version}"' in packages, f"Central package pin drift: {package}")
    require(re.search(r'<PackageVersion\s+Include="Dapr"(?:\s|/|>)', packages) is None, "Catalog-only Dapr must remain absent")
    return baseline


def validate_baseline(workspace: Path, baseline_path: Path) -> dict[str, Any]:
    try:
        return _validate_baseline(workspace, baseline_path)
    except ValidationError:
        raise
    except STRUCTURAL_ERRORS as error:
        raise ValidationError(f"Malformed baseline structure: {error}") from error


def _validate_packet(workspace: Path, packet_path: Path, baseline_path: Path, baseline: dict[str, Any]) -> None:
    packet = read_json(packet_path)
    exact_fields(packet, PACKET_FIELDS, "Packet")
    require(packet["schema"] == "hexalith.runtime-toolchain-evidence.v1", "Packet schema drift")
    require(packet["status"] == "accepted", "Packet is not accepted")
    require_utc_timestamp(packet["capturedUtc"], "Capture timestamp")

    baseline_ref = exact_fields(packet["baseline"], {"path", "sha256"}, "Baseline reference")
    referenced_baseline = resolve_artifact(workspace, baseline_ref["path"], "baseline")
    require(referenced_baseline == baseline_path.resolve(), "Packet references a different baseline")
    require(baseline_ref["sha256"] == sha256(baseline_path), "Baseline hash mismatch")
    require(packet["observedVersions"] == EXPECTED_TUPLE, "Observed tuple mismatch")
    require(packet["approval"] == {
        "approvedBy": baseline["approvedBy"],
        "approvedOn": baseline["approvedOn"],
        "ownerRoles": baseline["ownerRoles"],
        "supportTableListed": False,
        "decision": "approved-explicit-exception",
    }, "Approval record mismatch")

    repositories = packet["repositories"]
    require(isinstance(repositories, list) and repositories, "Repository bindings are required")
    repository_names: set[str] = set()
    repository_paths: set[str] = set()
    for repository in repositories:
        exact_fields(repository, {"name", "path", "revision", "diffSha256"}, "Repository binding")
        require(isinstance(repository["name"], str) and repository["name"] and repository["name"] not in repository_names,
                "Repository names must be unique non-empty strings")
        require(isinstance(repository["path"], str) and repository["path"] not in repository_paths,
                "Repository paths must be unique")
        repository_names.add(repository["name"])
        repository_paths.add(repository["path"])
        validate_repository_revision(workspace, repository, f"Repository {repository['name']}")
        require(isinstance(repository["diffSha256"], str)
                and re.fullmatch(r"[0-9a-f]{64}", repository["diffSha256"]) is not None,
                "Repository diff hash must be 64 lowercase hex")

    artifacts = packet["artifacts"]
    require(isinstance(artifacts, list) and artifacts, "Hashed artifacts are required")
    artifact_paths: dict[str, Path] = {}
    for artifact in artifacts:
        exact_fields(artifact, {"kind", "path", "sha256"}, "Artifact binding")
        kind = artifact["kind"]
        require(isinstance(kind, str), "Artifact kind must be a string")
        require(kind not in artifact_paths, f"Duplicate artifact kind: {kind}")
        path = resolve_artifact(workspace, artifact["path"], "artifact")
        require(artifact["sha256"] == sha256(path), f"Artifact hash mismatch: {artifact['path']}")
        if kind in SENTINEL_SCANNED_KINDS:
            text = path.read_text(encoding="utf-8", errors="replace")
            require(not any(pattern.search(text) for pattern in FORBIDDEN), f"Secret/private-path sentinel matched: {artifact['path']}")
        artifact_paths[kind] = path
    require(set(artifact_paths) == EXPECTED_ARTIFACT_KINDS,
            f"Artifact kinds drift: expected {sorted(EXPECTED_ARTIFACT_KINDS)}")

    versions = read_json(artifact_paths["observed-versions"])
    exact_fields(versions, {"schema", "observedUtc", *EXPECTED_TUPLE}, "Observed versions")
    require(versions["schema"] == "hexalith.runtime-toolchain-observed-versions.v1", "Observed-versions schema drift")
    require_utc_timestamp(versions["observedUtc"], "Observed-versions timestamp")
    artifact_tuple = {field: versions[field] for field in EXPECTED_TUPLE}
    require(artifact_tuple == packet["observedVersions"], "Observed-versions artifact does not match packet tuple")

    source_state = read_json(artifact_paths["source-state"])
    exact_fields(source_state, {"schema", "repositories"}, "Source state")
    require(source_state["schema"] == "hexalith.runtime-toolchain-source-state.v1", "Source-state schema drift")
    state_bindings = []
    for repository in source_state["repositories"]:
        exact_fields(repository, {"name", "path", "revision", "diffSha256", "files"}, "Source repository")
        validate_repository_revision(workspace, repository, f"Source repository {repository['name']}")
        files = repository["files"]
        require(isinstance(files, list) and files, f"Source files are required: {repository['name']}")
        for source in files:
            exact_fields(source, {"path", "sha256"}, "Source file")
            source_path = resolve_artifact(workspace, source["path"], "source file")
            require(source["sha256"] == sha256(source_path), f"Source file hash mismatch: {source['path']}")
        require(repository["diffSha256"] == manifest_digest(files), f"Source diff manifest hash mismatch: {repository['name']}")
        state_bindings.append({key: repository[key] for key in ("name", "path", "revision", "diffSha256")})
    require(repositories == state_bindings, "Packet repository bindings do not match source state")

    command_record = read_json(artifact_paths["command-record"])
    exact_fields(command_record, {"schema", "commands"}, "Command record")
    require(command_record["schema"] == "hexalith.runtime-toolchain-command-record.v1", "Command-record schema drift")
    commands = command_record["commands"]
    require(isinstance(commands, list), "Command record commands must be an array")
    commands_by_purpose: dict[str, dict[str, Any]] = {}
    for command in commands:
        exact_fields(command, {"purpose", "command", "exitCode", "outcome"}, "Command result")
        purpose = command["purpose"]
        require(isinstance(purpose, str) and purpose and purpose not in commands_by_purpose,
                "Command purposes must be unique non-empty strings")
        require(isinstance(command["command"], str) and command["command"].strip(),
                f"Command text is required: {purpose}")
        require(isinstance(command["outcome"], str) and command["outcome"].strip(),
                f"Command outcome is required: {purpose}")
        require(isinstance(command["exitCode"], int) and not isinstance(command["exitCode"], bool),
                f"Command exit code must be an integer: {purpose}")
        commands_by_purpose[purpose] = command
    require(set(commands_by_purpose) == set(REQUIRED_COMMAND_EXIT_CODES), "Acceptance-critical command set drift")
    for purpose, expected_exit_code in REQUIRED_COMMAND_EXIT_CODES.items():
        require(commands_by_purpose[purpose]["exitCode"] == expected_exit_code,
                f"Required command exit status mismatch: {purpose}")
    version_command = commands_by_purpose["observe exact tool versions"]
    for invocation in ("dotnet --version", "aspire --version", "dapr --version"):
        require(invocation in version_command["command"], f"Tool-version command does not invoke {invocation}")
    for version in (
        EXPECTED_TUPLE["dotnetSdk"], EXPECTED_TUPLE["aspireCli"],
        EXPECTED_TUPLE["daprCli"], EXPECTED_TUPLE["daprRuntime"],
    ):
        require(version in version_command["outcome"], f"Tool-version observation does not ground {version}")
    require("22 scenarios passed" in commands_by_purpose["G-6 mutation controls"]["outcome"],
            "Mutation command result count drift")
    require("TEST_USER_PASSWORD was absent" in commands_by_purpose["managed restart smoke credential preflight"]["outcome"],
            "Credential-preflight expected failure is not explicit")
    require("pre-existing drift" in commands_by_purpose["broad package-exception inventory outside G-6 affected set"]["outcome"],
            "Unrelated-inventory expected failure is not explicit")

    observations = read_json(artifact_paths["eventstore-observations"])

    counts = packet["testCounts"]
    require(counts == {
        "qualification": {"selectors": 1, "total": 1, "passed": 1, "failed": 0, "skipped": 0},
        "support": {"selectors": 21, "total": 33, "passed": 33, "failed": 0, "skipped": 0},
    }, "Qualification/support counts mismatch")

    qualification = read_json(artifact_paths["eventstore-qualification-results"])
    require(qualification.get("schemaVersion") == 1, "Qualification-result schema drift")
    qualification_summary = result_summary(qualification.get("summary"), "Qualification-result summary")
    qualification_counts = {
        "selectors": 1,
        "total": qualification_summary["tests"],
        "passed": qualification_summary["passed"],
        "failed": qualification_summary["failed"],
        "skipped": qualification_summary["skipped"],
    }
    require(qualification_counts == counts["qualification"], "Qualification result does not match packet counts")
    qualification_test = qualification.get("test")
    require(isinstance(qualification_test, dict) and qualification_test.get("status") == "passed",
            "Retained qualification test result is not passed")
    require(qualification_test.get("name") == QUALIFICATION_IDENTITY,
            "Retained qualification test identity drift")
    require(qualification_summary["passed"] == qualification_summary["tests"]
            and qualification_summary["failed"] == 0 and qualification_summary["skipped"] == 0,
            "Qualification result must be all-pass with zero skips")

    support = read_json(artifact_paths["eventstore-support-results"])
    require(support.get("schemaVersion") == 1, "Support-result schema drift")
    selectors = support.get("selectors")
    require(isinstance(selectors, list) and all(isinstance(item, str) and item for item in selectors),
            "Support selectors must be non-empty strings")
    require(len(selectors) == len(set(selectors)), "Support selectors must be unique")
    support_summary = result_summary(support.get("summary"), "Support-result summary")
    support_counts = {
        "selectors": len(selectors),
        "total": support_summary["tests"],
        "passed": support_summary["passed"],
        "failed": support_summary["failed"],
        "skipped": support_summary["skipped"],
    }
    require(support_counts == counts["support"], "Support result does not match packet counts")
    require(support_summary["passed"] == support_summary["tests"]
            and support_summary["failed"] == 0 and support_summary["skipped"] == 0,
            "Support result must be all-pass with zero skips")
    methods = support.get("methods")
    require(isinstance(methods, list), "Support method results are required")
    method_identities: list[str] = []
    observed_support_cases = 0
    for method in methods:
        exact_fields(method, {"identity", "expectedCases", "observedCases", "passedCases"}, "Support method result")
        require(isinstance(method["identity"], str) and method["identity"], "Support method identity is required")
        require(isinstance(method["expectedCases"], int) and not isinstance(method["expectedCases"], bool)
                and method["expectedCases"] > 0, "Support expected case count must be positive")
        require(method["observedCases"] == method["expectedCases"]
                and method["passedCases"] == method["expectedCases"],
                f"Support method did not pass every retained case: {method['identity']}")
        method_identities.append(method["identity"])
        observed_support_cases += method["observedCases"]
    require(method_identities == selectors, "Support methods do not match selectors")
    require(observed_support_cases == support_summary["tests"], "Support method cases do not match summary")
    retained_oracles = observations.get("observations", {}).get("authority_change", {}).get("deterministicSupportOracles")
    require(selectors == retained_oracles, "Support selectors do not match retained observation oracles")

    capture_validation = read_json(artifact_paths["eventstore-capture-validation"])
    exact_fields(capture_validation, {
        "schemaVersion", "validation", "observationsSha256", "testResultsSha256", "deterministicSupportSha256",
    }, "Capture validation")
    require(capture_validation["schemaVersion"] == 1 and capture_validation["validation"] == "passed",
            "Capture validation is not passed")
    require(capture_validation["observationsSha256"] == sha256(artifact_paths["eventstore-observations"]),
            "Capture validation observations hash mismatch")
    require(capture_validation["testResultsSha256"] == sha256(artifact_paths["eventstore-qualification-results"]),
            "Capture validation qualification-result hash mismatch")
    require(capture_validation["deterministicSupportSha256"] == sha256(artifact_paths["eventstore-support-results"]),
            "Capture validation support-result hash mismatch")

    dispositions = read_json(artifact_paths["dispositions"])
    exact_fields(dispositions, {
        "schema", "daprSupportDisposition", "communityToolkitAspireDapr", "fluentUi", "nSubstitute", "fluxor",
        "Dapr", "Dapr.Workflow", "attempts", "containment", "rollback",
    }, "Dispositions")
    require(dispositions["schema"] == "hexalith.runtime-toolchain-dispositions.v1", "Dispositions schema drift")
    require(dispositions["daprSupportDisposition"] == "approved-explicit-exception-not-support-table-listed",
            "Dapr disposition drift")
    for field in ("communityToolkitAspireDapr", "fluentUi", "nSubstitute", "fluxor", "Dapr", "Dapr.Workflow"):
        require(dispositions[field] == baseline["dispositions"][field], f"Disposition drift: {field}")
    attempts = dispositions["attempts"]
    require(isinstance(attempts, list) and attempts, "Qualification attempts are required")
    accepted_attempts = []
    attempt_numbers = set()
    for attempt in attempts:
        exact_fields(attempt, {"attempt", "accepted", "result", "reason", "disposition"}, "Qualification attempt")
        require(isinstance(attempt["attempt"], int) and not isinstance(attempt["attempt"], bool)
                and attempt["attempt"] > 0 and attempt["attempt"] not in attempt_numbers,
                "Qualification attempt numbers must be unique positive integers")
        attempt_numbers.add(attempt["attempt"])
        require(isinstance(attempt["accepted"], bool), "Qualification attempt acceptance must be boolean")
        require(attempt["result"] in {"passed", "failed"}, "Qualification attempt result must be passed or failed")
        require(attempt["accepted"] == (attempt["result"] == "passed"),
                f"Qualification attempt acceptance/result mismatch: {attempt['attempt']}")
        if attempt["accepted"]:
            accepted_attempts.append(attempt)
    require(len(accepted_attempts) == 1, "Exactly one passed qualification attempt must be accepted")
    require(dispositions["containment"] == packet["containment"] == EXPECTED_CONTAINMENT,
            "Disposition containment does not match packet")
    require_rollback(dispositions["rollback"], "Disposition rollback")
    require(packet["topology"] == {
        "eventStoreProcesses": 2,
        "eventStoreSidecars": 2,
        "independentProcessIdentities": True,
        "sharedStateStore": "state.postgresql",
    }, "Two-instance topology mismatch")
    expected_lifecycle = {
        "singleExecution": True,
        "duplicateWork": 0,
        "ownerStopped": True,
        "survivorReplayExact": True,
        "ownerRestarted": True,
        "restartedReplayExact": True,
        "authorityUnchanged": True,
        "persistedStateExact": True,
    }
    require(packet["lifecycle"] == expected_lifecycle, "Stop/survivor/restart lifecycle mismatch")
    require(packet["sentinelScan"] == {"passed": True, "matches": 0, "rawDiagnosticsRetained": False}, "Sentinel scan mismatch")
    require_rollback(packet["rollback"], "Packet rollback")
    require(packet["containment"] == EXPECTED_CONTAINMENT, "Packet containment drift")

    topology = observations.get("topology", {})
    retained_observations = observations.get("observations", {})
    writers = retained_observations.get("writers_failover", {})
    authority = retained_observations.get("authority_change", {})
    capture = retained_observations.get("capture", {})
    before = capture.get("before", {})
    after = capture.get("after", {})
    require(topology.get("eventStoreProcessCount") == 2 and topology.get("eventStoreSidecarCount") == 2, "Observed two-sidecar topology mismatch")
    require(topology.get("independentProcessIdentities") is True, "Observed identities are not independent")
    require(observations.get("profile", {}).get("stateStoreType") == "state.postgresql", "Observed state store is not PostgreSQL")
    require(observations.get("runtime", {}).get("dapr") == EXPECTED_TUPLE["daprRuntime"], "Observed Dapr runtime mismatch")
    require(writers.get("canonicalExecutionIdentities") == 1 and writers.get("sampleExecutions") == 1, "Observed execution identity/count mismatch")
    require(writers.get("ownerStoppedAtTerminalBoundary") is True, "Owner stop was not observed")
    require(writers.get("failoverReplayExact") is True and writers.get("restartedNodeReplayExact") is True, "Survivor/restart replay mismatch")
    require(writers.get("nonExecuteAdditionalWork") == 0, "Replay performed duplicate work")
    authority_unchanged = (
        authority.get("rotationReplayExact") is True
        and authority.get("canonicalAuthorityCount") == 1
        and authority.get("retiredReaderReplayExact") is True
    )
    require(packet["lifecycle"]["authorityUnchanged"] == authority_unchanged,
            "Packet authority acceptance contradicts retained observations")
    persisted_state_exact = (
        writers.get("restartedNodeReplayExact") is True
        and isinstance(before.get("schemaSha256"), str)
        and before.get("schemaSha256") == after.get("schemaSha256")
        and all(
            isinstance(before.get(field), int)
            and not isinstance(before.get(field), bool)
            and after.get(field) == before.get(field) + 4
            for field in ("aggregateSequenceTotal", "aggregateEventRows", "aggregateMetadataRows")
        )
        and before.get("protectedSentinelMatches") == 0
        and after.get("protectedSentinelMatches") == 0
        and capture.get("protectedSentinelMatches") == 0
        and capture.get("committedProjectionContainsIdentifiers") is False
    )
    require(packet["lifecycle"]["persistedStateExact"] == persisted_state_exact,
            "Packet persisted-state acceptance contradicts retained observations")


def validate_packet(workspace: Path, packet_path: Path, baseline_path: Path, baseline: dict[str, Any]) -> None:
    try:
        _validate_packet(workspace, packet_path, baseline_path, baseline)
    except ValidationError:
        raise
    except STRUCTURAL_ERRORS as error:
        raise ValidationError(f"Malformed packet structure: {error}") from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--packet", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        workspace = arguments.workspace.resolve()
        baseline_path = arguments.baseline.resolve()
        baseline = validate_baseline(workspace, baseline_path)
        validate_packet(workspace, arguments.packet.resolve(), baseline_path, baseline)
    except ValidationError as error:
        print(f"G6-EVIDENCE-INVALID: {error}", file=sys.stderr)
        return 1
    print("G6-EVIDENCE-VALID")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
