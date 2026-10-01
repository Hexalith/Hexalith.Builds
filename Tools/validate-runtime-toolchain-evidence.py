#!/usr/bin/env python3
"""Fail-closed validator for the G-6 runtime/toolchain qualification packet."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


# Exact tuple values, the approval date, and Dapr support-table facts are owned by
# the --baseline file, so every approved baseline is checked against its own values
# instead of one hard-coded revision. Approval authority is not baseline-owned: the
# approver and the G-6 owner roles must come from the allowlists below, the approval
# date may not follow the packet capture, a support-table-listed Dapr pair must be
# the tuple's own pair with an approval decision, every audited literal pin must set
# a tuple CLI/runtime role and equal that role's tuple value, and the tuple's Aspire
# SDK must be one of the audited AppHost SDK pins.
G6_APPROVERS = ("Jérôme Piquot",)
G6_OWNER_ROLES = ("Builds", "Platform", "FrontComposer/Web")
# An unlisted Dapr tuple needs the explicit exception; a support-table-listed tuple still
# needs a recorded approval, never a rejection or other free text.
DAPR_EXPLICIT_EXCEPTION = "approved-explicit-exception"
DAPR_APPROVAL_DECISIONS = ("approved", DAPR_EXPLICIT_EXCEPTION)
LITERAL_PIN_TUPLE_FIELDS = ("daprCli", "daprRuntime", "aspireCli")
# Every audited literal pin sets exactly one tuple CLI/runtime role, identified by one form:
# the whole pin must match the form's pattern, and the captured version must equal the tuple
# value of the form's role exactly. Most forms name their role in their own text. The YAML
# keys `default:` and `version:` name none, so they are classified by the enclosing context of
# every active line they occupy: a `default:` belongs to the workflow input whose key encloses
# it under `inputs:`, and a `version:` to the `with:` block of a step that uses a Dapr CLI
# setup action. A pin, or any occurrence of it, whose form or context sets no role is
# rejected, so a swapped pair of `default:` inputs or an unrelated pin cannot pass as audited.
YAML_DEFAULT_PIN = re.compile(r"default:\s*'(?P<version>[^'\s]+)'")
YAML_VERSION_PIN = re.compile(r"version:\s*'(?P<version>[^'\s]+)'")
DAPR_CLI_SETUP_ACTION = re.compile(
    r"dapr/setup-dapr@[0-9a-f]{40}"
    r"|\./references/Hexalith\.Builds/Github/dapr-init"
    r"|Hexalith/Hexalith\.Builds/Github/dapr-init@[0-9a-f]{40}"
)
LITERAL_PIN_FORMS = (
    # (form, tuple field, whole-pin pattern, enclosing context: None, ("input", name) or ("action", pattern))
    ("dapr-version input", "daprCli", re.compile(r"dapr-version:\s*'(?P<version>[^'\s]+)'"), None),
    ("DAPR_CLI_VERSION variable", "daprCli", re.compile(r"DAPR_CLI_VERSION:\s*'(?P<version>[^'\s]+)'"), None),
    ("dapr-runtime-version input", "daprRuntime", re.compile(r"dapr-runtime-version:\s*'(?P<version>[^'\s]+)'"), None),
    ("runtime-version input", "daprRuntime", re.compile(r"runtime-version:\s*'(?P<version>[^'\s]+)'"), None),
    ("DAPR_RUNTIME_VERSION variable", "daprRuntime", re.compile(r"DAPR_RUNTIME_VERSION:\s*'(?P<version>[^'\s]+)'"), None),
    ("dapr init --runtime-version command", "daprRuntime", re.compile(r"dapr init --runtime-version (?P<version>\S+)"), None),
    ("$DaprRuntimeVersion parameter", "daprRuntime", re.compile(r'\[string\]\$DaprRuntimeVersion = "(?P<version>[^"\s]+)",'), None),
    ("Aspire CLI install step", "aspireCli",
     re.compile(r"run: dotnet tool install --global Aspire\.Cli --version (?P<version>\S+)"), None),
    ("ASPIRE_CLI_INSTALL variable", "aspireCli",
     re.compile(r"ASPIRE_CLI_INSTALL: dotnet tool install --global Aspire\.Cli --version (?P<version>\S+)"), None),
    ("default: of the dapr-version workflow input", "daprCli", YAML_DEFAULT_PIN, ("input", "dapr-version")),
    ("default: of the dapr-runtime-version workflow input", "daprRuntime", YAML_DEFAULT_PIN, ("input", "dapr-runtime-version")),
    ("version: of a Dapr CLI setup action", "daprCli", YAML_VERSION_PIN, ("action", DAPR_CLI_SETUP_ACTION)),
)
# The mutation-controls command must record exactly the self-test's final output line with every
# count this revision's self-test reports, not a paraphrase, a line with other text around it, or
# a line with any other count. The self-test asserts that it prints exactly this line.
MUTATION_SCENARIOS_PER_BASELINE = 30
MUTATION_CONTROLS_BASELINES = 3
MUTATION_CONTROLS_BASELINE_DRIFT = 48
MUTATION_CONTROLS_AUTHORITY = 165
MUTATION_CONTROLS_HISTORICAL_PINS = 2
MUTATION_CONTROLS_RESULT = re.compile(
    r"G6-EVIDENCE-MUTATIONS-PASSED: (?P<scenarios>[0-9]+) scenarios for each of (?P<baselines>[0-9]+) baselines; "
    r"(?P<drift>[0-9]+) baseline-drift controls; (?P<authority>[0-9]+) authority controls; "
    r"(?P<pins>[0-9]+) historical baseline SHA-256 pins"
)
MUTATION_CONTROLS_RESULT_LINE = (
    f"G6-EVIDENCE-MUTATIONS-PASSED: {MUTATION_SCENARIOS_PER_BASELINE} scenarios for each of "
    f"{MUTATION_CONTROLS_BASELINES} baselines; {MUTATION_CONTROLS_BASELINE_DRIFT} baseline-drift controls; "
    f"{MUTATION_CONTROLS_AUTHORITY} authority controls; {MUTATION_CONTROLS_HISTORICAL_PINS} historical baseline SHA-256 pins"
)
TUPLE_FIELDS = (
    "dotnetSdk",
    "aspireSdk",
    "aspireCli",
    "communityToolkitAspireDapr",
    "daprCli",
    "daprRuntime",
    "daprDotnetPackages",
    "fluentUi",
    "nSubstitute",
    "fluxor",
)
CATALOG_TUPLE_FIELDS = {
    "CommunityToolkit.Aspire.Hosting.Dapr": "communityToolkitAspireDapr",
    "Dapr.Client": "daprDotnetPackages",
    "Dapr.Workflow": "daprDotnetPackages",
    "Microsoft.FluentUI.AspNetCore.Components": "fluentUi",
    "NSubstitute": "nSubstitute",
    "Fluxor": "fluxor",
}
DAPR_DISPOSITION_FIELDS = {"supportTableListed", "listedRuntime", "listedDotnetSdk", "decision"}
VERSION_TEXT = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?")
APPROVAL_DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
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
    """Hash retained text with Git-portable line endings."""
    digest = hashlib.sha256()
    digest.update(path.read_bytes().replace(b"\r\n", b"\n"))
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


def require_version(value: Any, label: str) -> str:
    require(isinstance(value, str) and VERSION_TEXT.fullmatch(value) is not None,
            f"{label} must be an exact version")
    return value


def require_text(value: Any, label: str) -> str:
    require(isinstance(value, str) and value.strip() == value and value != "", f"{label} must be non-empty text")
    return value


def _indent(line: str) -> int:
    return len(line) - len(line.lstrip(" "))


def _parent_line(lines: list[str], index: int) -> int | None:
    """Return the index of the nearest earlier non-blank line indented less than lines[index]."""
    indent = _indent(lines[index])
    for candidate in range(index - 1, -1, -1):
        if lines[candidate].strip() and _indent(lines[candidate]) < indent:
            return candidate
    return None


def _yaml_key(line: str) -> str | None:
    """Return the key of a YAML line that opens a nested mapping (`name:` with no value)."""
    match = re.fullmatch(r"(?:- )?(?P<key>[A-Za-z0-9_.-]+):", line.strip())
    return match["key"] if match is not None else None


def _enclosing_input(lines: list[str], index: int) -> str | None:
    """Return the workflow input name whose key encloses lines[index] directly under `inputs:`."""
    input_line = _parent_line(lines, index)
    if input_line is None or _yaml_key(lines[input_line]) is None:
        return None
    inputs_line = _parent_line(lines, input_line)
    if inputs_line is None or _yaml_key(lines[inputs_line]) != "inputs":
        return None
    return _yaml_key(lines[input_line])


def _enclosing_action(lines: list[str], index: int) -> str | None:
    """Return the `uses:` action of the step whose `with:` block directly encloses lines[index]."""
    with_line = _parent_line(lines, index)
    if with_line is None or _yaml_key(lines[with_line]) != "with":
        return None
    indent = _indent(lines[with_line])
    step_keys = []
    # The step's own keys sit at the `with:` indentation; its first key may share the `- ` line.
    for direction in (range(with_line - 1, -1, -1), range(with_line + 1, len(lines))):
        for candidate in direction:
            line = lines[candidate]
            if not line.strip():
                continue
            if _indent(line) < indent:
                if direction.step < 0 and line.strip().startswith("- ") and _indent(line) + 2 == indent:
                    step_keys.append(line.strip()[2:])
                break
            if _indent(line) == indent:
                step_keys.append(line.strip())
    actions = [re.sub(r"^uses:\s*", "", item).strip() for item in step_keys if item.startswith("uses:")]
    return actions[0] if len(actions) == 1 else None


def literal_pin_roles(value: str, active_text: str) -> list[tuple[str, str, str]]:
    """Return (form, tuple field, version) for one audited literal pin, one entry per classified use.

    A pin whose own text names its role yields one entry. A `default:` or `version:` pin yields one
    entry for each active line it occupies, classified by that line's enclosing YAML context.
    Raises ValidationError when the pin, or any active occurrence of it, sets no tuple role.
    """
    target = value.strip()
    text_forms = [(form, field, pattern.fullmatch(target)) for form, field, pattern, context in LITERAL_PIN_FORMS
                  if context is None]
    text_matches = [(form, field, match["version"]) for form, field, match in text_forms if match is not None]
    if text_matches:
        require(len(text_matches) == 1, f"Literal pin matches more than one form: {target}")
        return text_matches
    context_forms = [(form, field, pattern.fullmatch(target), context) for form, field, pattern, context in LITERAL_PIN_FORMS
                     if context is not None]
    context_forms = [(form, field, match["version"], context) for form, field, match, context in context_forms
                     if match is not None]
    require(bool(context_forms), f"Literal pin sets no Dapr CLI, Dapr runtime or Aspire CLI role: {target}")
    lines = active_text.splitlines()
    roles = []
    for index, line in enumerate(lines):
        if line.strip() != target:
            continue
        classified = []
        for form, field, version, (kind, expected) in context_forms:
            if kind == "input" and _enclosing_input(lines, index) == expected:
                classified.append((form, field, version))
            elif kind == "action":
                action = _enclosing_action(lines, index)
                if action is not None and expected.fullmatch(action) is not None:
                    classified.append((form, field, version))
        require(len(classified) == 1,
                f"Literal pin sets no Dapr CLI, Dapr runtime or Aspire CLI role at line {index + 1}: {target}")
        roles.extend(classified)
    require(bool(roles), f"Expected active literal pin missing: {target}")
    return roles


def baseline_approval_date(baseline: dict[str, Any]) -> dt.date:
    """Return the baseline approval date after checking its exact YYYY-MM-DD form."""
    approved_on = baseline["approvedOn"]
    require(isinstance(approved_on, str) and APPROVAL_DATE.fullmatch(approved_on) is not None,
            "Baseline approval date must be YYYY-MM-DD")
    try:
        return dt.date.fromisoformat(approved_on)
    except ValueError as error:
        raise ValidationError("Baseline approval date is not a valid date") from error


def baseline_tuple(baseline: dict[str, Any]) -> dict[str, str]:
    """Return the exact approved tuple owned by the validated baseline."""
    return baseline["tuple"]


def baseline_approval(baseline: dict[str, Any]) -> dict[str, Any]:
    """Return the packet approval record implied by the validated baseline."""
    dapr = baseline["dispositions"]["dapr"]
    return {
        "approvedBy": baseline["approvedBy"],
        "approvedOn": baseline["approvedOn"],
        "ownerRoles": baseline["ownerRoles"],
        "supportTableListed": dapr["supportTableListed"],
        "decision": dapr["decision"],
    }


def baseline_dapr_disposition(baseline: dict[str, Any]) -> str:
    dapr = baseline["dispositions"]["dapr"]
    listing = "support-table-listed" if dapr["supportTableListed"] else "not-support-table-listed"
    return f"{dapr['decision']}-{listing}"


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


def _validate_baseline(workspace: Path, baseline_path: Path, document: dict[str, Any] | None = None) -> dict[str, Any]:
    baseline = read_json(baseline_path) if document is None else document
    exact_fields(
        baseline,
        {"schema", "approvedBy", "approvedOn", "ownerRoles", "tuple", "dispositions", "rollback", "containment", "pinAudit"},
        "Baseline",
    )
    require(baseline["schema"] == "hexalith.runtime-toolchain-baseline.v1", "Baseline schema drift")
    require_text(baseline["approvedBy"], "Baseline approver")
    require(baseline["approvedBy"] in G6_APPROVERS, "Baseline approver is not an authorized G-6 approver")
    baseline_approval_date(baseline)
    owner_roles = baseline["ownerRoles"]
    require(isinstance(owner_roles, list) and owner_roles, "Baseline owner roles are required")
    for role in owner_roles:
        require_text(role, "Baseline owner role")
        require(role in G6_OWNER_ROLES, f"Baseline owner role is not an authorized G-6 owner role: {role}")
    require(len(owner_roles) == len(set(owner_roles)), "Baseline owner roles must be unique")
    require(set(owner_roles) == set(G6_OWNER_ROLES),
            f"Baseline owner roles must include every G-6 owner role: {list(G6_OWNER_ROLES)}")
    expected_tuple = exact_fields(baseline["tuple"], set(TUPLE_FIELDS), "Baseline tuple")
    for field in TUPLE_FIELDS:
        require_version(expected_tuple[field], f"Baseline tuple {field}")
    dispositions = baseline["dispositions"]
    require(isinstance(dispositions, dict), "Baseline dispositions must be an object")
    dapr = exact_fields(dispositions.get("dapr"), DAPR_DISPOSITION_FIELDS, "Dapr support-table disposition")
    require(isinstance(dapr["supportTableListed"], bool), "Dapr support-table listing must be boolean")
    require_version(dapr["listedRuntime"], "Dapr support-table listed runtime")
    require_version(dapr["listedDotnetSdk"], "Dapr support-table listed .NET SDK")
    require_text(dapr["decision"], "Dapr support-table decision")
    require(dapr["supportTableListed"] or dapr["decision"] == DAPR_EXPLICIT_EXCEPTION,
            "An unlisted Dapr tuple requires an approved explicit exception")
    if dapr["supportTableListed"]:
        require(dapr["decision"] in DAPR_APPROVAL_DECISIONS,
                f"A support-table-listed Dapr tuple requires an approval decision: {list(DAPR_APPROVAL_DECISIONS)}")
        # A listed pair is only a listing of this tuple when both listed values are the tuple's own.
        require(dapr["listedRuntime"] == expected_tuple["daprRuntime"],
                "Support-table-listed Dapr runtime differs from the tuple runtime")
        require(dapr["listedDotnetSdk"] == expected_tuple["daprDotnetPackages"],
                "Support-table-listed Dapr .NET SDK differs from the tuple Dapr .NET packages")
    require(dispositions.get("communityToolkitAspireDapr") == "approved-prerelease-exception", "Toolkit prerelease exception missing")
    fluent_version = expected_tuple["fluentUi"]
    if "-" not in fluent_version:
        fluent_disposition = "stable"
    else:
        require(re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+-rc(?:[.-][0-9A-Za-z][0-9A-Za-z.-]*)?", fluent_version, re.IGNORECASE)
                is not None, "Fluent UI prerelease is not an approved release-candidate class")
        fluent_disposition = "approved-release-candidate-exception"
    require(dispositions.get("fluentUi") == fluent_disposition,
            f"Fluent UI disposition drift: {fluent_version} requires {fluent_disposition}")
    require(dispositions.get("nSubstitute") == "stable" and dispositions.get("fluxor") == "stable", "Stable package disposition drift")
    require(dispositions.get("Dapr") == "catalog-only-not-activated", "Catalog-only Dapr classification drift")
    require(dispositions.get("Dapr.Workflow") == "catalog-only-unselected", "Dapr.Workflow classification drift")
    require_rollback(baseline["rollback"], "Baseline rollback")
    require(baseline["containment"] == EXPECTED_CONTAINMENT, "Baseline containment drift")

    audit = exact_fields(baseline["pinAudit"], {"globalJson", "appHostProjects", "literalPins"}, "Pin audit")
    for relative in audit["globalJson"]:
        path = resolve_artifact(workspace, relative, "global.json")
        document = read_json(path)
        require(document.get("sdk", {}).get("version") == expected_tuple["dotnetSdk"], f".NET SDK pin drift: {relative}")
    for item in audit["appHostProjects"]:
        exact_fields(item, {"path", "version"}, "AppHost project pin")
        path = resolve_artifact(workspace, item["path"], "AppHost project")
        version = item["version"]
        require(isinstance(version, str) and version.strip(), "AppHost project version is required")
        project = path.read_text(encoding="utf-8")
        require(f'Aspire.AppHost.Sdk/{version}' in project, f"Aspire AppHost SDK pin drift: {item['path']}")
    require(expected_tuple["aspireSdk"] in {item["version"] for item in audit["appHostProjects"]},
            f"Tuple aspireSdk {expected_tuple['aspireSdk']} is not among the audited AppHost SDK pins")
    literal_pins = audit["literalPins"]
    require(isinstance(literal_pins, list), "Literal pins must be an array")
    active_texts: dict[str, str] = {}
    for item in literal_pins:
        exact_fields(item, {"path", "value"}, "Literal pin")
        path = resolve_artifact(workspace, item["path"], "literal pin")
        require(isinstance(item["value"], str) and item["value"].strip(), "Literal pin value is required")
        active_text = active_texts.setdefault(item["path"], comment_free_text(path))
        target = item["value"].strip()
        matched = target in active_text if "\n" in target else target in {line.strip() for line in active_text.splitlines()}
        require(matched, f"Expected active literal pin missing: {item['path']}::{item['value']}")
    # Every audited pin sets a CLI/runtime role in each place it occurs and must declare exactly the
    # tuple's value for that role, and each role must be declared by at least one audited pin.
    grounded_fields = set()
    for item in literal_pins:
        try:
            roles = literal_pin_roles(item["value"], active_texts[item["path"]])
        except ValidationError as error:
            raise ValidationError(f"{error} ({item['path']})") from error
        for form, field, version in roles:
            require(version == expected_tuple[field],
                    f"Audited {field} literal pin {item['path']}::{item['value'].strip()} ({form}) differs from tuple "
                    f"{field} {expected_tuple[field]}")
            grounded_fields.add(field)
    for field in LITERAL_PIN_TUPLE_FIELDS:
        require(field in grounded_fields,
                f"Tuple {field} {expected_tuple[field]} has no audited literal pin of the same role")

    packages = resolve_artifact(workspace, "references/Hexalith.Builds/Props/Directory.Packages.props", "central package catalog").read_text(encoding="utf-8")
    package_expectations = {package: expected_tuple[field] for package, field in CATALOG_TUPLE_FIELDS.items()}
    for package, version in package_expectations.items():
        require(f'Include="{package}" Version="{version}"' in packages, f"Central package pin drift: {package}")
    require(re.search(r'<PackageVersion\s+Include="Dapr"(?:\s|/|>)', packages) is None, "Catalog-only Dapr must remain absent")
    return baseline


def validate_baseline(workspace: Path, baseline_path: Path) -> dict[str, Any]:
    try:
        baseline = read_json(baseline_path)
        if baseline.get("schema") == "hexalith.runtime-toolchain-baseline.v2":
            return current_contract().validate_baseline(__import__("types").SimpleNamespace(**globals()), workspace, baseline_path, baseline)
        return _validate_baseline(workspace, baseline_path)
    except ValidationError:
        raise
    except STRUCTURAL_ERRORS as error:
        raise ValidationError(f"Malformed baseline structure: {error}") from error


def _validate_packet(workspace: Path, packet_path: Path, baseline_path: Path, baseline: dict[str, Any], candidate: bool) -> None:
    packet = read_json(packet_path)
    exact_fields(packet, PACKET_FIELDS, "Packet")
    require(packet["schema"] == "hexalith.runtime-toolchain-evidence.v1", "Packet schema drift")
    expected_status = "pending" if candidate else "accepted"
    require(packet["status"] == expected_status, f"Packet status must be {expected_status}")
    require_utc_timestamp(packet["capturedUtc"], "Capture timestamp")
    captured_on = dt.datetime.fromisoformat(packet["capturedUtc"][:-1] + "+00:00").date()

    baseline_ref = exact_fields(packet["baseline"], {"path", "sha256"}, "Baseline reference")
    referenced_baseline = resolve_artifact(workspace, baseline_ref["path"], "baseline")
    require(referenced_baseline == baseline_path.resolve(), "Packet references a different baseline")
    require(baseline_ref["sha256"] == sha256(baseline_path), "Baseline hash mismatch")
    expected_tuple = baseline_tuple(baseline)
    require(packet["observedVersions"] == expected_tuple, "Observed tuple mismatch")
    require(packet["approval"] == baseline_approval(baseline), "Approval record mismatch")
    # The approval authorizes a run; a baseline dated after the capture cannot have authorized it.
    require(baseline_approval_date(baseline) <= captured_on,
            "Baseline approval date is later than the packet capture date")

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
    exact_fields(versions, {"schema", "observedUtc", *TUPLE_FIELDS}, "Observed versions")
    require(versions["schema"] == "hexalith.runtime-toolchain-observed-versions.v1", "Observed-versions schema drift")
    require_utc_timestamp(versions["observedUtc"], "Observed-versions timestamp")
    artifact_tuple = {field: versions[field] for field in TUPLE_FIELDS}
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
        expected_tuple["dotnetSdk"], expected_tuple["aspireCli"],
        expected_tuple["daprCli"], expected_tuple["daprRuntime"],
    ):
        require(version in version_command["outcome"], f"Tool-version observation does not ground {version}")
    mutation_outcome = commands_by_purpose["G-6 mutation controls"]["outcome"]
    require(MUTATION_CONTROLS_RESULT.fullmatch(mutation_outcome) is not None,
            "Mutation-controls outcome does not record exactly the self-test result line")
    require(mutation_outcome == MUTATION_CONTROLS_RESULT_LINE,
            f"Mutation command result count drift: expected '{MUTATION_CONTROLS_RESULT_LINE}'")
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
    require(dispositions["daprSupportDisposition"] == baseline_dapr_disposition(baseline),
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
    require(observations.get("runtime", {}).get("dapr") == expected_tuple["daprRuntime"], "Observed Dapr runtime mismatch")
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


def validate_packet(
    workspace: Path, packet_path: Path, baseline_path: Path, baseline: dict[str, Any], candidate: bool = False,
) -> None:
    try:
        if baseline.get("schema") == "hexalith.runtime-toolchain-baseline.v2":
            current_contract().validate_packet(__import__("types").SimpleNamespace(**globals()), workspace, packet_path, baseline_path, baseline, candidate)
            return
        _validate_packet(workspace, packet_path, baseline_path, baseline, candidate)
    except ValidationError:
        raise
    except STRUCTURAL_ERRORS as error:
        raise ValidationError(f"Malformed packet structure: {error}") from error


def current_contract():
    """Load the separately versioned current contract without changing historical rules."""
    spec = importlib.util.spec_from_file_location("runtime_toolchain_v2", Path(__file__).with_name("runtime_toolchain_v2.py"))
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--packet", required=True, type=Path)
    parser.add_argument("--candidate", action="store_true", help="Validate a pending packet without granting acceptance")
    arguments = parser.parse_args()
    try:
        workspace = arguments.workspace.resolve()
        baseline_path = arguments.baseline.resolve()
        baseline = validate_baseline(workspace, baseline_path)
        validate_packet(workspace, arguments.packet.resolve(), baseline_path, baseline, candidate=arguments.candidate)
    except ValidationError as error:
        print(f"G6-EVIDENCE-INVALID: {error}", file=sys.stderr)
        return 1
    print("G6-EVIDENCE-CANDIDATE-VALID" if arguments.candidate else "G6-EVIDENCE-VALID")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
