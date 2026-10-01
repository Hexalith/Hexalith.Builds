#!/usr/bin/env python3
"""Hermetic mutation tests for the G-6 evidence validator.

Every approved baseline runs the same packet mutation suite against a fixture
workspace generated from that baseline's own tuple, approval, and pin audit, so
no baseline depends on values hard-coded for another revision. The fixture
approval and Dapr disposition are built from literal baseline fields rather than
validator helpers, the bound historical baselines are pinned by SHA-256, and
separate authority controls prove that approver, owner-role, approval-date,
listed-Dapr, Aspire SDK and CLI/runtime pin contradictions are rejected: a tuple
CLI/runtime value must equal the pins of its own role exactly, so swapped Dapr
CLI/runtime values and versions that are only a prefix or suffix of a pin fail.
The fixture writes the role-less `default:` and `version:` pins inside their real
YAML context (workflow input, Dapr CLI setup step), and one control per pin form,
listed below with this script's own prefixes, changes a single pin of that form.
The fixture records the exact result line the validator requires as the
mutation-controls outcome, and main() proves this script prints that same line.
"""

from __future__ import annotations

import copy
import datetime as dt
import importlib.util
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-runtime-toolchain-evidence.py")
BASELINES = (
    # Accepted 2026-09-06 G-6 tuple; bound by the historical accepted packet.
    SCRIPT.with_name("runtime-toolchain-baseline.json"),
    # Superseded 2026-09-27 EventStore 3.108.1 candidate tuple.
    SCRIPT.with_name("runtime-toolchain-baseline-2026-09-27.json"),
    # Pending 2026-09-29 EventStore 3.109.0 candidate tuple.
    SCRIPT.with_name("runtime-toolchain-baseline-2026-09-29.json"),
)
# Bound baselines must keep the exact bytes their packets hash (normalized-LF SHA-256,
# identical to the raw hash of the LF checkout): the accepted 2026-09-06 packet and the
# superseded 2026-09-27 packet. Any later edit to these files is drift, not an update.
HISTORICAL_BASELINE_SHA256 = {
    "runtime-toolchain-baseline.json": "525615c65ada8cabf5a6911a374bb22b62f6a3c1bfa6c91f7245312da9720265",
    "runtime-toolchain-baseline-2026-09-27.json": "b9aa6791effe78cbb9bf405cd8384b81a6d750f996fabf8f79b7cd1c3bedad01",
}
REJECTED_PACKET_MUTATIONS: list[str] = []
AUTHORITY_CONTROLS: list[str] = []
# The Dapr CLI setup action the fixture writes around every `version:` pin.
FIXTURE_DAPR_SETUP_ACTION = "dapr/setup-dapr@" + "0" * 40
# Every literal-pin form the audited baselines use, restated with this script's own prefixes and
# fixture contexts rather than the validator's patterns: (form, tuple field, pin prefix, context).
# A `default:` pin sits under the workflow input its version sets; a `version:` pin sits in the
# `with:` block of a Dapr CLI setup step. Each form gets one control that changes one pin of it.
PIN_FORMS = (
    ("dapr-version input", "daprCli", "dapr-version:", None),
    ("DAPR_CLI_VERSION variable", "daprCli", "DAPR_CLI_VERSION:", None),
    ("dapr-runtime-version input", "daprRuntime", "dapr-runtime-version:", None),
    ("runtime-version input", "daprRuntime", "runtime-version:", None),
    ("DAPR_RUNTIME_VERSION variable", "daprRuntime", "DAPR_RUNTIME_VERSION:", None),
    ("dapr init --runtime-version command", "daprRuntime", "dapr init --runtime-version ", None),
    ("$DaprRuntimeVersion parameter", "daprRuntime", "[string]$DaprRuntimeVersion = ", None),
    ("Aspire CLI install step", "aspireCli", "run: dotnet tool install --global Aspire.Cli --version ", None),
    ("ASPIRE_CLI_INSTALL variable", "aspireCli", "ASPIRE_CLI_INSTALL: ", None),
    ("default: of the dapr-version workflow input", "daprCli", "default:", ("input", "dapr-version")),
    ("default: of the dapr-runtime-version workflow input", "daprRuntime", "default:", ("input", "dapr-runtime-version")),
    ("version: of a Dapr CLI setup action", "daprCli", "version:", ("action", FIXTURE_DAPR_SETUP_ACTION)),
)
DEFAULT_INPUT_BY_FIELD = {"daprCli": "dapr-version", "daprRuntime": "dapr-runtime-version"}
SPEC = importlib.util.spec_from_file_location("g6_validator", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)
# The validator requires the recorded mutation-controls outcome to be exactly this line; main()
# asserts that every baseline ran this many packet scenarios and that it prints this line.
PACKET_SCENARIOS_PER_BASELINE = VALIDATOR.MUTATION_SCENARIOS_PER_BASELINE
RESULT_LINE = VALIDATOR.MUTATION_CONTROLS_RESULT_LINE


def mutation_summary_line(scenarios: int, baselines: int, drift: int, authority: int, pins: int) -> str:
    """The self-test's final output line; the G-6 packet records it verbatim."""
    return (
        f"G6-EVIDENCE-MUTATIONS-PASSED: {scenarios} scenarios for each of {baselines} baselines; "
        f"{drift} baseline-drift controls; {authority} authority controls; "
        f"{pins} historical baseline SHA-256 pins"
    )


def replace_pin_version(value: str, version: str, replacement: str) -> str:
    """Replace one exact version inside a literal pin's text, never part of a longer version."""
    return re.sub(r"(?<![0-9A-Za-z.-])" + re.escape(version) + r"(?![0-9A-Za-z.-])", replacement, value)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def write_text(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8")


def artifact_binding(workspace: Path, kind: str, path: Path) -> dict[str, str]:
    return {"kind": kind, "path": path.relative_to(workspace).as_posix(), "sha256": VALIDATOR.sha256(path)}


def refresh_artifact(packet: dict, kind: str, path: Path) -> None:
    next(item for item in packet["artifacts"] if item["kind"] == kind)["sha256"] = VALIDATOR.sha256(path)


def write_capture_validation(path: Path, observations: Path, qualification: Path, support: Path) -> None:
    write_json(path, {
        "schemaVersion": 1,
        "validation": "passed",
        "observationsSha256": VALIDATOR.sha256(observations),
        "testResultsSha256": VALIDATOR.sha256(qualification),
        "deterministicSupportSha256": VALIDATOR.sha256(support),
    })


def expect_rejected(
    workspace: Path, baseline_path: Path, baseline: dict, packet_path: Path, packet: dict, label: str,
    message: str | None = None, record: list[str] | None = None,
) -> None:
    write_json(packet_path, packet)
    try:
        VALIDATOR.validate_packet(workspace, packet_path, baseline_path, baseline)
    except VALIDATOR.ValidationError as error:
        assert message is None or message in str(error), f"{label}: expected '{message}', got '{error}'"
        (REJECTED_PACKET_MUTATIONS if record is None else record).append(label)
        return
    raise AssertionError(f"mutation was accepted: {label}")


def expect_baseline_rejected(
    workspace: Path, baseline_path: Path, baseline: dict, label: str, message: str | None = None,
) -> None:
    write_json(baseline_path, baseline)
    try:
        VALIDATOR.validate_baseline(workspace, baseline_path)
    except VALIDATOR.ValidationError as error:
        assert message is None or message in str(error), f"{label}: expected '{message}', got '{error}'"
        return
    finally:
        baseline_path.unlink()
    raise AssertionError(f"baseline mutation was accepted: {label}")


def fixture_approval(baseline: dict) -> dict:
    """Packet approval restated from literal baseline fields, independent of validator helpers."""
    dapr = baseline["dispositions"]["dapr"]
    return {
        "approvedBy": baseline["approvedBy"],
        "approvedOn": baseline["approvedOn"],
        "ownerRoles": list(baseline["ownerRoles"]),
        "supportTableListed": dapr["supportTableListed"],
        "decision": dapr["decision"],
    }


def fixture_dapr_disposition(baseline: dict) -> str:
    """Dapr disposition label restated from literal baseline fields, independent of validator helpers."""
    dapr = baseline["dispositions"]["dapr"]
    listing = {True: "support-table-listed", False: "not-support-table-listed"}[dapr["supportTableListed"]]
    return f"{dapr['decision']}-{listing}"


def assert_historical_baseline_bytes() -> None:
    for name, expected in HISTORICAL_BASELINE_SHA256.items():
        actual = VALIDATOR.sha256(SCRIPT.with_name(name))
        assert actual == expected, f"bound historical baseline drifted: {name} hashes {actual}, packets bind {expected}"


def pin_form(value: str, context: tuple[str, str] | None) -> tuple[str, str]:
    """Return the (form, tuple field) of one audited pin from this script's own PIN_FORMS table."""
    forms = [(form, field) for form, field, prefix, form_context in PIN_FORMS
             if value.strip().startswith(prefix) and form_context == context]
    assert len(forms) == 1, f"literal pin has no single fixture form: {value!r} {context!r}"
    return forms[0]


def fixture_pin_contexts(literal_pins: list[dict[str, str]], tuple_values: dict[str, str]) -> list[tuple[str, str] | None]:
    """The YAML context each original pin is written in: its workflow input, its setup step, or none."""
    contexts: list[tuple[str, str] | None] = []
    for item in literal_pins:
        value = item["value"].strip()
        if value.startswith("default:"):
            fields = [field for field, name in DEFAULT_INPUT_BY_FIELD.items() if f"'{tuple_values[field]}'" in value]
            assert len(fields) == 1, f"default: pin sets no single fixture input: {value}"
            contexts.append(("input", DEFAULT_INPUT_BY_FIELD[fields[0]]))
        elif value.startswith("version:"):
            contexts.append(("action", FIXTURE_DAPR_SETUP_ACTION))
        else:
            contexts.append(None)
    return contexts


def write_literal_pins(
    workspace: Path, literal_pins: list[dict[str, str]], contexts: list[tuple[str, str] | None],
) -> None:
    """Write each audited literal pin as an active line of its fixture file, inside its YAML context."""
    files: dict[str, dict] = {}
    for item, context in zip(literal_pins, contexts, strict=True):
        entry = files.setdefault(item["path"], {"lines": [], "inputs": {}, "steps": []})
        value = item["value"].strip()
        if context is None:
            entry["lines"].append(value)
        elif context[0] == "input":
            entry["inputs"].setdefault(context[1], []).append(value)
        else:
            entry["steps"].append((context[1], value))
    for relative, entry in files.items():
        lines = list(entry["lines"])
        if entry["inputs"]:
            lines += ["on:", "  workflow_call:", "    inputs:"]
            for name, values in entry["inputs"].items():
                lines += [f"      {name}:", "        type: string", *(f"        {value}" for value in values)]
        if entry["steps"]:
            lines.append("steps:")
            for action, value in entry["steps"]:
                lines += ["  - name: Install Dapr CLI", f"    uses: {action}", "    with:", f"      {value}"]
        write_text(workspace / relative, "\n".join(lines) + "\n")


def expect_pinned_baseline_rejected(
    workspace: Path, baseline_path: Path, baseline: dict, original_pins: list[dict[str, str]],
    contexts: list[tuple[str, str] | None], label: str, message: str,
) -> None:
    """Reject a baseline whose changed literal pins are really present, in context, in the fixture files."""
    write_literal_pins(workspace, baseline["pinAudit"]["literalPins"], contexts)
    try:
        expect_baseline_rejected(workspace, baseline_path, baseline, label, message)
    finally:
        write_literal_pins(workspace, original_pins, contexts)


def fixture_catalog(tuple_values: dict[str, str]) -> str:
    return "<Project><ItemGroup>" + "".join(
        f'<PackageVersion Include="{package}" Version="{tuple_values[field]}" />'
        for package, field in VALIDATOR.CATALOG_TUPLE_FIELDS.items()
    ) + "</ItemGroup></Project>\n"


def initialize_git(workspace: Path) -> str:
    commands = (
        ("init", "-b", "main"),
        ("add", "."),
        ("-c", "user.name=G6 Fixture", "-c", "user.email=g6@example.invalid", "commit", "-m", "fixture"),
    )
    for command in commands:
        subprocess.run(["git", "-C", str(workspace), *command], check=True, stdout=subprocess.DEVNULL)
    return subprocess.run(
        ["git", "-C", str(workspace), "rev-parse", "HEAD"], check=True, text=True, capture_output=True
    ).stdout.strip()


def run_baseline(real_baseline: Path, other_tuples: list[dict[str, str]]) -> tuple[int, int, int]:
    """Run the packet mutation suite, baseline drift controls and authority controls for one baseline."""
    baseline_template = VALIDATOR.read_json(real_baseline)
    tuple_values = baseline_template["tuple"]
    literal_dispositions = baseline_template["dispositions"]
    # Capture on the approval day, so the fixture satisfies approval-before-capture exactly at its edge.
    captured_utc = f"{baseline_template['approvedOn']}T12:00:00Z"
    REJECTED_PACKET_MUTATIONS.clear()
    AUTHORITY_CONTROLS.clear()
    with tempfile.TemporaryDirectory(prefix="g6-validator-") as temporary:
        workspace = Path(temporary)
        builds = workspace / "references/Hexalith.Builds"
        baseline_path = builds / "Tools" / real_baseline.name
        write_json(baseline_path, baseline_template)

        for relative in baseline_template["pinAudit"]["globalJson"]:
            write_json(workspace / relative, {"sdk": {"version": tuple_values["dotnetSdk"]}})
        for item in baseline_template["pinAudit"]["appHostProjects"]:
            write_text(workspace / item["path"], f'<Project Sdk="Aspire.AppHost.Sdk/{item["version"]}">\n</Project>\n')
        original_pins = copy.deepcopy(baseline_template["pinAudit"]["literalPins"])
        contexts = fixture_pin_contexts(original_pins, tuple_values)
        write_literal_pins(workspace, original_pins, contexts)
        catalog_path = builds / "Props/Directory.Packages.props"
        write_text(catalog_path, fixture_catalog(tuple_values))
        write_text(builds / "schemas/hexalith.runtime-toolchain-evidence.v1.json", "{}\n")
        write_text(builds / "Tools/validate-runtime-toolchain-evidence.py", "# fixture validator\n")
        write_text(builds / "Tools/test-runtime-toolchain-evidence-validator.py", "# fixture mutations\n")

        evidence = workspace / "evidence"
        paths = {
            "observations": evidence / "observations.json",
            "qualification": evidence / "test-results.json",
            "support": evidence / "deterministic-support.json",
            "capture": evidence / "capture-validation.json",
            "versions": evidence / "versions.json",
            "commands": evidence / "commands.json",
            "dispositions": evidence / "dispositions.json",
            "failed": evidence / "failed-attempt.json",
            "marker": evidence / "source-marker.txt",
            "source_state": evidence / "source-state.json",
            "packet": evidence / "packet.json",
        }
        evidence.mkdir(parents=True, exist_ok=True)
        lf_marker = evidence / "line-ending-lf.txt"
        crlf_marker = evidence / "line-ending-crlf.txt"
        lf_marker.write_bytes(b"portable\nsource\n")
        crlf_marker.write_bytes(b"portable\r\nsource\r\n")
        assert VALIDATOR.sha256(lf_marker) == VALIDATOR.sha256(crlf_marker), (
            "G-6 evidence hashes must be stable across Git line-ending materialization"
        )
        write_text(paths["marker"], "G-6 source binding\n")
        revision = initialize_git(workspace)
        baseline = VALIDATOR.validate_baseline(workspace, baseline_path)

        selectors = [f"fixture.support.{number:02d}" for number in range(1, 22)]
        expected_cases = [1] * 20 + [13]
        observations = {
            "topology": {"eventStoreProcessCount": 2, "eventStoreSidecarCount": 2, "independentProcessIdentities": True},
            "profile": {"stateStoreType": "state.postgresql"},
            "runtime": {"dapr": tuple_values["daprRuntime"]},
            "observations": {
                "writers_failover": {
                    "canonicalExecutionIdentities": 1, "sampleExecutions": 1,
                    "ownerStoppedAtTerminalBoundary": True, "failoverReplayExact": True,
                    "restartedNodeReplayExact": True, "nonExecuteAdditionalWork": 0,
                },
                "authority_change": {
                    "rotationReplayExact": True, "canonicalAuthorityCount": 1,
                    "retiredReaderReplayExact": True, "deterministicSupportOracles": selectors,
                },
                "capture": {
                    "before": {
                        "schemaSha256": "a" * 64, "aggregateEventRows": 0,
                        "aggregateSequenceTotal": 0, "aggregateMetadataRows": 0,
                        "protectedSentinelMatches": 0,
                    },
                    "after": {
                        "schemaSha256": "a" * 64, "aggregateEventRows": 4,
                        "aggregateSequenceTotal": 4, "aggregateMetadataRows": 4,
                        "protectedSentinelMatches": 0,
                    },
                    "protectedSentinelMatches": 0,
                    "committedProjectionContainsIdentifiers": False,
                },
            },
        }
        qualification = {
            "schemaVersion": 1, "runner": "fixture", "command": "fixture qualification",
            "summary": {"tests": 1, "passed": 1, "failed": 0, "skipped": 0},
            "test": {"name": VALIDATOR.QUALIFICATION_IDENTITY, "status": "passed"},
        }
        support = {
            "schemaVersion": 1, "runner": "fixture", "command": "fixture support", "selectors": selectors,
            "summary": {"tests": 33, "passed": 33, "failed": 0, "skipped": 0},
            "methods": [
                {"identity": identity, "expectedCases": cases, "observedCases": cases, "passedCases": cases}
                for identity, cases in zip(selectors, expected_cases, strict=True)
            ],
        }
        command_results = []
        for purpose, exit_code in VALIDATOR.REQUIRED_COMMAND_EXIT_CODES.items():
            command = "fixture command"
            outcome = "passed"
            if purpose == "observe exact tool versions":
                command = "dotnet --version && aspire --version && dapr --version"
                outcome = (
                    f".NET SDK {tuple_values['dotnetSdk']}; "
                    f"Aspire CLI {tuple_values['aspireCli']}; "
                    f"Dapr CLI {tuple_values['daprCli']}; "
                    f"runtime {tuple_values['daprRuntime']}"
                )
            elif purpose == "G-6 mutation controls":
                # The exact final output line main() prints, every count included.
                outcome = RESULT_LINE
            elif purpose == "managed restart smoke credential preflight":
                outcome = "TEST_USER_PASSWORD was absent; blocked before startup"
            elif purpose == "broad package-exception inventory outside G-6 affected set":
                outcome = "pre-existing drift outside G-6"
            command_results.append({"purpose": purpose, "command": command, "exitCode": exit_code, "outcome": outcome})
        source_files = [{"path": "evidence/source-marker.txt", "sha256": VALIDATOR.sha256(paths["marker"])}]
        source_repository = {
            "name": "fixture", "path": ".", "revision": revision,
            "diffSha256": VALIDATOR.manifest_digest(source_files), "files": source_files,
        }

        def restore_evidence() -> None:
            write_json(paths["observations"], observations)
            write_json(paths["qualification"], qualification)
            write_json(paths["support"], support)
            write_capture_validation(paths["capture"], paths["observations"], paths["qualification"], paths["support"])
            write_json(paths["versions"], {
                "schema": "hexalith.runtime-toolchain-observed-versions.v1",
                "observedUtc": captured_utc, **tuple_values,
            })
            write_json(paths["commands"], {
                "schema": "hexalith.runtime-toolchain-command-record.v1", "commands": command_results,
            })
            write_json(paths["dispositions"], {
                "schema": "hexalith.runtime-toolchain-dispositions.v1",
                "daprSupportDisposition": fixture_dapr_disposition(baseline_template),
                **{field: literal_dispositions[field] for field in (
                    "communityToolkitAspireDapr", "fluentUi", "nSubstitute", "fluxor", "Dapr", "Dapr.Workflow")},
                "attempts": [
                    {"attempt": 1, "accepted": False, "result": "failed", "reason": "fixture rejected", "disposition": "diagnostic"},
                    {"attempt": 2, "accepted": True, "result": "passed", "reason": "fixture accepted", "disposition": "selected"},
                ],
                "containment": copy.deepcopy(VALIDATOR.EXPECTED_CONTAINMENT),
                "rollback": {"requiresDomainDataMutation": False, "action": "restore fixture pins"},
            })
            write_json(paths["failed"], {"schema": "fixture.failed-attempt.v1", "accepted": False})
            write_json(paths["source_state"], {
                "schema": "hexalith.runtime-toolchain-source-state.v1", "repositories": [source_repository],
            })

        restore_evidence()
        static_artifacts = {
            "baseline-governance": baseline_path,
            "evidence-schema": builds / "schemas/hexalith.runtime-toolchain-evidence.v1.json",
            "evidence-validator": builds / "Tools/validate-runtime-toolchain-evidence.py",
            "mutation-tests": builds / "Tools/test-runtime-toolchain-evidence-validator.py",
            "central-catalog": builds / "Props/Directory.Packages.props",
        }
        packet = {
            "schema": "hexalith.runtime-toolchain-evidence.v1",
            "baseline": {"path": baseline_path.relative_to(workspace).as_posix(), "sha256": VALIDATOR.sha256(baseline_path)},
            "capturedUtc": captured_utc,
            "repositories": [{key: source_repository[key] for key in ("name", "path", "revision", "diffSha256")}],
            "artifacts": [
                artifact_binding(workspace, "source-state", paths["source_state"]),
                *[artifact_binding(workspace, kind, path) for kind, path in static_artifacts.items()],
                artifact_binding(workspace, "observed-versions", paths["versions"]),
                artifact_binding(workspace, "command-record", paths["commands"]),
                artifact_binding(workspace, "dispositions", paths["dispositions"]),
                artifact_binding(workspace, "eventstore-observations", paths["observations"]),
                artifact_binding(workspace, "eventstore-qualification-results", paths["qualification"]),
                artifact_binding(workspace, "eventstore-support-results", paths["support"]),
                artifact_binding(workspace, "eventstore-capture-validation", paths["capture"]),
                artifact_binding(workspace, "failed-attempt-diagnostic", paths["failed"]),
            ],
            "observedVersions": copy.deepcopy(tuple_values),
            "testCounts": {
                "qualification": {"selectors": 1, "total": 1, "passed": 1, "failed": 0, "skipped": 0},
                "support": {"selectors": 21, "total": 33, "passed": 33, "failed": 0, "skipped": 0},
            },
            "topology": {"eventStoreProcesses": 2, "eventStoreSidecars": 2, "independentProcessIdentities": True, "sharedStateStore": "state.postgresql"},
            "lifecycle": {"singleExecution": True, "duplicateWork": 0, "ownerStopped": True, "survivorReplayExact": True, "ownerRestarted": True, "restartedReplayExact": True, "authorityUnchanged": True, "persistedStateExact": True},
            "approval": fixture_approval(baseline_template),
            "sentinelScan": {"passed": True, "matches": 0, "rawDiagnosticsRetained": False},
            "rollback": {"requiresDomainDataMutation": False, "action": "restore prior pins"},
            "containment": copy.deepcopy(VALIDATOR.EXPECTED_CONTAINMENT), "status": "accepted",
        }
        write_json(paths["packet"], packet)
        VALIDATOR.validate_packet(workspace, paths["packet"], baseline_path, baseline)

        def run_mode(candidate: bool) -> subprocess.CompletedProcess[str]:
            command = [
                sys.executable, str(SCRIPT), "--workspace", str(workspace),
                "--baseline", str(baseline_path), "--packet", str(paths["packet"]),
            ]
            if candidate:
                command.append("--candidate")
            return subprocess.run(command, text=True, capture_output=True, check=False)

        accepted_result = run_mode(False)
        assert accepted_result.returncode == 0 and accepted_result.stdout.strip() == "G6-EVIDENCE-VALID"
        pending = copy.deepcopy(packet)
        pending["status"] = "pending"
        write_json(paths["packet"], pending)
        VALIDATOR.validate_packet(workspace, paths["packet"], baseline_path, baseline, candidate=True)
        pending_result = run_mode(True)
        assert pending_result.returncode == 0 and pending_result.stdout.strip() == "G6-EVIDENCE-CANDIDATE-VALID"
        assert run_mode(False).returncode == 1
        write_json(paths["packet"], packet)
        assert run_mode(True).returncode == 1

        packet_mutations = []
        stale_hash = copy.deepcopy(packet); stale_hash["artifacts"][0]["sha256"] = "b" * 64
        packet_mutations.append(("stale artifact hash", stale_hash))
        tuple_mismatch = copy.deepcopy(packet); tuple_mismatch["observedVersions"]["daprRuntime"] = "0.0.0-mutation"
        packet_mutations.append(("tuple mismatch", tuple_mismatch))
        single_sidecar = copy.deepcopy(packet); single_sidecar["topology"]["eventStoreSidecars"] = 1
        packet_mutations.append(("single sidecar", single_sidecar))
        missing_restart = copy.deepcopy(packet); missing_restart["lifecycle"]["ownerRestarted"] = False
        packet_mutations.append(("missing restart", missing_restart))
        wrong_approval = copy.deepcopy(packet); wrong_approval["approval"]["approvedBy"] = "inferred"
        packet_mutations.append(("unapproved", wrong_approval))
        skipped = copy.deepcopy(packet); skipped["testCounts"]["support"] = {"selectors": 21, "total": 33, "passed": 32, "failed": 0, "skipped": 1}
        packet_mutations.append(("skipped support", skipped))
        missing_artifact = copy.deepcopy(packet); missing_artifact["artifacts"] = [item for item in missing_artifact["artifacts"] if item["kind"] != "command-record"]
        packet_mutations.append(("missing required artifact", missing_artifact))
        empty_rollback = copy.deepcopy(packet); empty_rollback["rollback"]["action"] = "  "
        packet_mutations.append(("empty rollback", empty_rollback))
        for label, mutation in packet_mutations:
            expect_rejected(workspace, baseline_path, baseline, paths["packet"], mutation, label)

        def reject_observation(label: str, mutation: dict) -> None:
            restore_evidence(); write_json(paths["observations"], mutation)
            write_capture_validation(paths["capture"], paths["observations"], paths["qualification"], paths["support"])
            changed = copy.deepcopy(packet)
            refresh_artifact(changed, "eventstore-observations", paths["observations"])
            refresh_artifact(changed, "eventstore-capture-validation", paths["capture"])
            expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, label)

        for label, mutator in (
            ("independent process identity", lambda value: value["topology"].update(independentProcessIdentities=False)),
            ("replay result fact", lambda value: value["observations"]["writers_failover"].update(restartedNodeReplayExact=False)),
            ("authority contradiction", lambda value: value["observations"]["authority_change"].update(rotationReplayExact=False)),
            ("persistence contradiction", lambda value: value["observations"]["capture"]["after"].update(
                aggregateSequenceTotal=0, aggregateEventRows=0, aggregateMetadataRows=0)),
        ):
            changed_observations = copy.deepcopy(observations); mutator(changed_observations)
            reject_observation(label, changed_observations)

        restore_evidence()
        retained_result = copy.deepcopy(qualification)
        retained_result["summary"] = {"tests": 1, "passed": 0, "failed": 1, "skipped": 0}; retained_result["test"]["status"] = "failed"
        write_json(paths["qualification"], retained_result)
        write_capture_validation(paths["capture"], paths["observations"], paths["qualification"], paths["support"])
        changed = copy.deepcopy(packet)
        refresh_artifact(changed, "eventstore-qualification-results", paths["qualification"]); refresh_artifact(changed, "eventstore-capture-validation", paths["capture"])
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, "retained test result")

        restore_evidence()
        changed_support = copy.deepcopy(support); changed_support["selectors"][0] = "invented.selector"; changed_support["methods"][0]["identity"] = "invented.selector"
        write_json(paths["support"], changed_support)
        write_capture_validation(paths["capture"], paths["observations"], paths["qualification"], paths["support"])
        changed = copy.deepcopy(packet)
        refresh_artifact(changed, "eventstore-support-results", paths["support"]); refresh_artifact(changed, "eventstore-capture-validation", paths["capture"])
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, "invented support selector")

        restore_evidence()
        changed_commands = copy.deepcopy(command_results)
        next(item for item in changed_commands if item["purpose"] == "G-6 mutation controls")["exitCode"] = 1
        write_json(paths["commands"], {"schema": "hexalith.runtime-toolchain-command-record.v1", "commands": changed_commands})
        changed = copy.deepcopy(packet); refresh_artifact(changed, "command-record", paths["commands"])
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, "failed required command")

        # The mutation-controls outcome must be exactly the self-test's result line: a paraphrase
        # (which the count-only check once required), the line with other text around it, and a line
        # with any single count changed all fail.
        counts = VALIDATOR.MUTATION_CONTROLS_RESULT.fullmatch(RESULT_LINE)
        assert counts is not None, RESULT_LINE
        exact_counts = {name: int(counts[name]) for name in ("scenarios", "baselines", "drift", "authority", "pins")}
        outcome_controls = [
            ("paraphrased mutation-controls outcome", f"{PACKET_SCENARIOS_PER_BASELINE} scenarios passed",
             "does not record exactly the self-test result line"),
            ("mutation-controls line with trailing text", RESULT_LINE + " (recorded verbatim)",
             "does not record exactly the self-test result line"),
            ("mutation-controls line with leading text", "self-test: " + RESULT_LINE,
             "does not record exactly the self-test result line"),
        ]
        for name, delta in (("scenarios", -1), ("baselines", -1), ("drift", 1), ("authority", -1), ("pins", 1)):
            changed_counts = dict(exact_counts, **{name: exact_counts[name] + delta})
            outcome_controls.append((
                f"mutation-controls {name} count drift",
                mutation_summary_line(changed_counts["scenarios"], changed_counts["baselines"], changed_counts["drift"],
                                      changed_counts["authority"], changed_counts["pins"]),
                "Mutation command result count drift"))
        for label, outcome, message in outcome_controls:
            restore_evidence()
            changed_commands = copy.deepcopy(command_results)
            next(item for item in changed_commands if item["purpose"] == "G-6 mutation controls")["outcome"] = outcome
            write_json(paths["commands"], {"schema": "hexalith.runtime-toolchain-command-record.v1", "commands": changed_commands})
            changed = copy.deepcopy(packet); refresh_artifact(changed, "command-record", paths["commands"])
            expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, label, message)

        restore_evidence()
        invented_state = {"schema": "hexalith.runtime-toolchain-source-state.v1", "repositories": [copy.deepcopy(source_repository)]}
        invented_state["repositories"][0]["revision"] = "f" * 40
        write_json(paths["source_state"], invented_state)
        changed = copy.deepcopy(packet); changed["repositories"][0]["revision"] = "f" * 40; refresh_artifact(changed, "source-state", paths["source_state"])
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, "invented repository revision")

        restore_evidence()
        malformed_state = {"schema": "hexalith.runtime-toolchain-source-state.v1", "repositories": [copy.deepcopy(source_repository)]}
        malformed_state["repositories"][0]["files"] = None
        write_json(paths["source_state"], malformed_state)
        changed = copy.deepcopy(packet); refresh_artifact(changed, "source-state", paths["source_state"])
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, "malformed nested structure")

        for label, diagnostic in (
            ("secret-bearing artifact", "Bearer mutation-control-value"),
            ("quoted JSON credential", {'password': 'mutation-control-value'}),
            ("macOS private path", "/Users/alice/private/output.json"),
            ("Windows private path", r"C:\Users\alice\private\output.json"),
        ):
            restore_evidence(); changed_observations = copy.deepcopy(observations); changed_observations["diagnostic"] = diagnostic
            write_json(paths["observations"], changed_observations)
            write_capture_validation(paths["capture"], paths["observations"], paths["qualification"], paths["support"])
            changed = copy.deepcopy(packet)
            refresh_artifact(changed, "eventstore-observations", paths["observations"]); refresh_artifact(changed, "eventstore-capture-validation", paths["capture"])
            expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed, label)
        # One accepted fixture packet plus every rejected packet mutation.
        packet_scenarios = 1 + len(REJECTED_PACKET_MUTATIONS)
        assert len(REJECTED_PACKET_MUTATIONS) == len(set(REJECTED_PACKET_MUTATIONS)), REJECTED_PACKET_MUTATIONS

        # Authority controls: packet-side contradictions of the baseline approval.
        restore_evidence()
        changed_dispositions = json.loads(paths["dispositions"].read_text(encoding="utf-8"))
        listed = baseline_template["dispositions"]["dapr"]["supportTableListed"]
        changed_dispositions["daprSupportDisposition"] = fixture_dapr_disposition(
            {"dispositions": {"dapr": {**baseline_template["dispositions"]["dapr"], "supportTableListed": not listed}}})
        write_json(paths["dispositions"], changed_dispositions)
        changed = copy.deepcopy(packet); refresh_artifact(changed, "dispositions", paths["dispositions"])
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed,
                        "packet Dapr disposition drift", "Dapr disposition drift", AUTHORITY_CONTROLS)
        restore_evidence()

        approved_on = dt.date.fromisoformat(baseline_template["approvedOn"])
        changed = copy.deepcopy(packet); changed["approval"]["approvedOn"] = (approved_on - dt.timedelta(days=1)).isoformat()
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed,
                        "packet approval date drift", "Approval record mismatch", AUTHORITY_CONTROLS)
        changed = copy.deepcopy(packet); changed["approval"]["ownerRoles"] = changed["approval"]["ownerRoles"][:-1]
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed,
                        "packet approval role drift", "Approval record mismatch", AUTHORITY_CONTROLS)
        changed = copy.deepcopy(packet)
        changed["capturedUtc"] = f"{(approved_on - dt.timedelta(days=1)).isoformat()}T23:59:59Z"
        expect_rejected(workspace, baseline_path, baseline, paths["packet"], changed,
                        "capture before approval date", "later than the packet capture date", AUTHORITY_CONTROLS)

        # A baseline approved after the capture: its approval, hash and binding all agree with the
        # packet, so only the approval-before-capture rule can reject it.
        original_baseline_bytes = baseline_path.read_bytes()
        future_baseline = copy.deepcopy(baseline_template); future_baseline["approvedOn"] = "2099-12-31"
        write_json(baseline_path, future_baseline)
        try:
            future = VALIDATOR.validate_baseline(workspace, baseline_path)
            changed = copy.deepcopy(packet)
            changed["baseline"]["sha256"] = VALIDATOR.sha256(baseline_path)
            changed["approval"] = fixture_approval(future_baseline)
            refresh_artifact(changed, "baseline-governance", baseline_path)
            expect_rejected(workspace, baseline_path, future, paths["packet"], changed,
                            "approval after capture", "later than the packet capture date", AUTHORITY_CONTROLS)
        finally:
            baseline_path.write_bytes(original_baseline_bytes)

        # Baseline-owned expectations: the same fixture must reject another
        # baseline's tuple, malformed approval facts, non-exact tuple versions and catalog drift.
        mutated_baseline_path = baseline_path.with_name("mutated-runtime-toolchain-baseline.json")
        baseline_controls: list[tuple[str, dict, str | None]] = []
        for index, other_tuple in enumerate(other_tuples, start=1):
            foreign = copy.deepcopy(baseline_template); foreign["tuple"] = copy.deepcopy(other_tuple)
            baseline_controls.append((f"foreign baseline tuple {index}", foreign, None))
        malformed_date = copy.deepcopy(baseline_template); malformed_date["approvedOn"] = "2026-02-30"
        baseline_controls.append(("malformed approval date", malformed_date, None))
        unapproved_dapr = copy.deepcopy(baseline_template); unapproved_dapr["dispositions"]["dapr"]["decision"] = "inferred"
        baseline_controls.append(("unlisted Dapr without explicit exception", unapproved_dapr, None))
        missing_field = copy.deepcopy(baseline_template); del missing_field["tuple"]["fluxor"]
        baseline_controls.append(("tuple field drift", missing_field, None))
        # Every tuple field must be one exact version; the expected message proves the shape check
        # itself rejects a floating version, not a later pin or catalog comparison.
        for field in VALIDATOR.TUPLE_FIELDS:
            floating = copy.deepcopy(baseline_template)
            floating["tuple"][field] = ".".join(tuple_values[field].split("-", 1)[0].split(".")[:2]) + ".*"
            baseline_controls.append((f"non-exact tuple {field}", floating, f"Baseline tuple {field} must be an exact version"))
        for label, mutation, message in baseline_controls:
            expect_baseline_rejected(workspace, mutated_baseline_path, mutation, label, message)

        # Authority controls: baseline fields that contradict the allowlists or the audited pins.
        authority_baselines = []
        other_approver = copy.deepcopy(baseline_template); other_approver["approvedBy"] = "Inferred Approver"
        authority_baselines.append(("unlisted approver", other_approver, "not an authorized G-6 approver"))
        extra_role = copy.deepcopy(baseline_template); extra_role["ownerRoles"].append("Security")
        authority_baselines.append(("unlisted owner role", extra_role, "not an authorized G-6 owner role: Security"))
        missing_role = copy.deepcopy(baseline_template); missing_role["ownerRoles"].remove("Platform")
        authority_baselines.append(("missing owner role", missing_role, "must include every G-6 owner role"))
        duplicate_role = copy.deepcopy(baseline_template); duplicate_role["ownerRoles"].append(duplicate_role["ownerRoles"][0])
        authority_baselines.append(("duplicate owner role", duplicate_role, "Baseline owner roles must be unique"))
        compact_date = copy.deepcopy(baseline_template); compact_date["approvedOn"] = baseline_template["approvedOn"].replace("-", "")
        authority_baselines.append(("non-hyphenated approval date", compact_date, "Baseline approval date must be YYYY-MM-DD"))
        # A listed pair must still be exact versions and carry an approval, even when it equals the tuple.
        listed_equal = {"supportTableListed": True, "listedRuntime": tuple_values["daprRuntime"],
                        "listedDotnetSdk": tuple_values["daprDotnetPackages"]}
        for label, changes, message in (
            ("non-version listed Dapr runtime", {"listedRuntime": "1.18"},
             "Dapr support-table listed runtime must be an exact version"),
            ("non-version listed Dapr .NET SDK", {"listedDotnetSdk": "latest"},
             "Dapr support-table listed .NET SDK must be an exact version"),
            ("listed Dapr pair with a rejection decision", {"decision": "rejected"},
             "A support-table-listed Dapr tuple requires an approval decision"),
        ):
            listed_control = copy.deepcopy(baseline_template)
            listed_control["dispositions"]["dapr"].update(listed_equal, **changes)
            authority_baselines.append((label, listed_control, message))
        listed_runtime = copy.deepcopy(baseline_template)
        listed_runtime["dispositions"]["dapr"].update(
            supportTableListed=True, listedRuntime="0.0.1", listedDotnetSdk=tuple_values["daprDotnetPackages"])
        authority_baselines.append(("listed Dapr runtime differs", listed_runtime, "listed Dapr runtime differs"))
        listed_sdk = copy.deepcopy(baseline_template)
        listed_sdk["dispositions"]["dapr"].update(
            supportTableListed=True, listedRuntime=tuple_values["daprRuntime"], listedDotnetSdk="0.0.1")
        authority_baselines.append(("listed Dapr .NET SDK differs", listed_sdk, "listed Dapr .NET SDK differs"))
        for field, unaudited in (("daprCli", "1.99.0"), ("daprRuntime", "1.99.0"), ("aspireCli", "13.99.0")):
            unaudited_tuple = copy.deepcopy(baseline_template); unaudited_tuple["tuple"][field] = unaudited
            authority_baselines.append((f"unaudited {field}", unaudited_tuple,
                                        f"differs from tuple {field} {unaudited}"))
        # Swapped Dapr CLI/runtime values each still appear among the audited pins, but only under
        # the other role, so an any-pin match would accept them.
        swapped = copy.deepcopy(baseline_template)
        swapped["tuple"]["daprCli"], swapped["tuple"]["daprRuntime"] = tuple_values["daprRuntime"], tuple_values["daprCli"]
        assert swapped["tuple"]["daprCli"] != tuple_values["daprCli"], "swapped Dapr control needs distinct CLI/runtime values"
        authority_baselines.append(("swapped Dapr CLI and runtime", swapped,
                                    f"differs from tuple daprCli {tuple_values['daprRuntime']}"))
        # A role whose every audited pin is dropped from the audit is no longer grounded, even though
        # the remaining pins of the other roles still validate.
        for field in VALIDATOR.LITERAL_PIN_TUPLE_FIELDS:
            ungrounded = copy.deepcopy(baseline_template)
            ungrounded["pinAudit"]["literalPins"] = [
                item for item, context in zip(original_pins, contexts, strict=True)
                if pin_form(item["value"], context)[1] != field]
            assert len(ungrounded["pinAudit"]["literalPins"]) < len(original_pins), field
            authority_baselines.append((f"no audited {field} pin", ungrounded,
                                        f"Tuple {field} {tuple_values[field]} has no audited literal pin of the same role"))
        unaudited_sdk = copy.deepcopy(baseline_template); unaudited_sdk["tuple"]["aspireSdk"] = "13.99.0"
        authority_baselines.append(("unaudited aspireSdk", unaudited_sdk,
                                    "Tuple aspireSdk 13.99.0 is not among the audited AppHost SDK pins"))
        for label, mutation, message in authority_baselines:
            expect_baseline_rejected(workspace, mutated_baseline_path, mutation, label, message)
            AUTHORITY_CONTROLS.append(label)
        # One control per pin form: change the version of a single audited pin of that form, in the
        # baseline and in its fixture file (so the pin stays present), and require the rejection to
        # name that exact pin. A form the validator stops classifying, or classifies under another
        # role, fails the expected message.
        for form, field, _, _ in PIN_FORMS:
            indexes = [index for index, (item, context) in enumerate(zip(original_pins, contexts, strict=True))
                       if pin_form(item["value"], context) == (form, field)]
            assert indexes, f"no audited pin of form {form}"
            changed_version = "9.9.9"
            assert changed_version != tuple_values[field], form
            single = copy.deepcopy(baseline_template)
            pin = single["pinAudit"]["literalPins"][indexes[0]]
            pin["value"] = replace_pin_version(pin["value"], tuple_values[field], changed_version)
            assert pin["value"] != original_pins[indexes[0]]["value"], form
            expect_pinned_baseline_rejected(
                workspace, mutated_baseline_path, single, original_pins, contexts, f"single {form} pin changed",
                f"{pin['path']}::{pin['value'].strip()} ({form}) differs from tuple {field} {tuple_values[field]}")
            AUTHORITY_CONTROLS.append(f"single {form} pin changed")
        # The two `default:` inputs of one workflow swap their values: every audited line is still
        # present, so only classification by the enclosing input can reject it.
        default_indexes = {}
        for index, context in enumerate(contexts):
            if context is not None and context[0] == "input":
                default_indexes.setdefault(original_pins[index]["path"], []).append(index)
        swap_path, swap_indexes = next((path, indexes) for path, indexes in default_indexes.items() if len(indexes) == 2)
        swapped_contexts = list(contexts)
        swapped_contexts[swap_indexes[0]], swapped_contexts[swap_indexes[1]] = contexts[swap_indexes[1]], contexts[swap_indexes[0]]
        swapped_form, swapped_field = pin_form(original_pins[swap_indexes[0]]["value"], swapped_contexts[swap_indexes[0]])
        write_literal_pins(workspace, original_pins, swapped_contexts)
        try:
            expect_baseline_rejected(
                workspace, mutated_baseline_path, baseline_template, "swapped default: inputs",
                f"{swap_path}::{original_pins[swap_indexes[0]]['value'].strip()} ({swapped_form}) differs from tuple "
                f"{swapped_field} {tuple_values[swapped_field]}")
        finally:
            write_literal_pins(workspace, original_pins, contexts)
        AUTHORITY_CONTROLS.append("swapped default: inputs")
        # An audited pin that sets no role: a `default:` under an unrelated workflow input, a
        # `version:` of an unrelated setup action, and a pin text of no known form. Each is present
        # in its fixture file.
        unrelated_path = "references/Hexalith.Builds/.github/workflows/unrelated-inputs.yml"
        for label, value, fixture_text in (
            ("default: of an unrelated workflow input", f"default: '{tuple_values['daprCli']}'",
             f"on:\n  workflow_call:\n    inputs:\n      packages-lock-file:\n        default: '{tuple_values['daprCli']}'\n"),
            ("version: of an unrelated setup action", f"version: '{tuple_values['daprCli']}'",
             "steps:\n  - name: Install Node.js\n    uses: actions/setup-node@" + "0" * 40
             + f"\n    with:\n      version: '{tuple_values['daprCli']}'\n"),
            ("pin of no known form", f"DAPR_VERSION: '{tuple_values['daprCli']}'", f"DAPR_VERSION: '{tuple_values['daprCli']}'\n"),
        ):
            unrelated = copy.deepcopy(baseline_template)
            unrelated["pinAudit"]["literalPins"].append({"path": unrelated_path, "value": value})
            write_text(workspace / unrelated_path, fixture_text)
            try:
                expect_baseline_rejected(workspace, mutated_baseline_path, unrelated, label,
                                         "sets no Dapr CLI, Dapr runtime or Aspire CLI role")
            finally:
                (workspace / unrelated_path).unlink()
            AUTHORITY_CONTROLS.append(label)
        # The tuple value is only a prefix or a suffix of the version its role's pins declare; the
        # changed pins are written to the fixture files so only the exact-version rule can reject them.
        for field in VALIDATOR.LITERAL_PIN_TUPLE_FIELDS:
            version = tuple_values[field]
            for label, replacement in ((f"tuple {field} is a prefix of its pins", f"{version}-rc.1"),
                                       (f"tuple {field} is a suffix of its pins", f"1{version}")):
                shifted = copy.deepcopy(baseline_template)
                for item in shifted["pinAudit"]["literalPins"]:
                    item["value"] = replace_pin_version(item["value"], version, replacement)
                assert shifted["pinAudit"]["literalPins"] != original_pins, label
                expect_pinned_baseline_rejected(workspace, mutated_baseline_path, shifted, original_pins, contexts, label,
                                                f"differs from tuple {field} {version}")
                AUTHORITY_CONTROLS.append(label)
        # Positive controls: a listed pair equal to the tuple's own runtime and SDK is accepted with
        # either approval decision.
        for decision in ("approved", "approved-explicit-exception"):
            listed_pair = copy.deepcopy(baseline_template)
            listed_pair["dispositions"]["dapr"].update(listed_equal, decision=decision)
            write_json(mutated_baseline_path, listed_pair)
            try:
                VALIDATOR.validate_baseline(workspace, mutated_baseline_path)
            finally:
                mutated_baseline_path.unlink()
            AUTHORITY_CONTROLS.append(f"listed Dapr pair equal to the tuple, decision {decision}")
        # Release class and owner disposition must agree: stable Fluent UI is not an RC exception.
        fluent_controls = (
            ("stable Fluent UI", "5.0.0", "stable", True),
            ("stable Fluent UI classified as RC", "5.0.0", "approved-release-candidate-exception", False),
            ("stable Fluent UI missing disposition", "5.0.0", None, False),
            ("stable Fluent UI unknown disposition", "5.0.0", "unapproved", False),
            ("RC Fluent UI", "5.0.0-rc.5-26219.1", "approved-release-candidate-exception", True),
            ("RC Fluent UI classified as stable", "5.0.0-rc.5-26219.1", "stable", False),
            ("RC Fluent UI missing disposition", "5.0.0-rc.5-26219.1", None, False),
            ("beta Fluent UI classified as RC", "5.0.0-beta.1", "approved-release-candidate-exception", False),
        )
        for label, version, disposition, accepted in fluent_controls:
            fluent_baseline = copy.deepcopy(baseline_template)
            fluent_baseline["tuple"]["fluentUi"] = version
            fluent_baseline["dispositions"]["fluentUi"] = disposition
            write_text(catalog_path, fixture_catalog(fluent_baseline["tuple"]))
            try:
                if accepted:
                    write_json(mutated_baseline_path, fluent_baseline)
                    VALIDATOR.validate_baseline(workspace, mutated_baseline_path)
                else:
                    expect_baseline_rejected(workspace, mutated_baseline_path, fluent_baseline, label, "Fluent UI")
            finally:
                mutated_baseline_path.unlink(missing_ok=True)
                write_text(catalog_path, fixture_catalog(tuple_values))
            AUTHORITY_CONTROLS.append(label)
        assert len(AUTHORITY_CONTROLS) == len(set(AUTHORITY_CONTROLS)), AUTHORITY_CONTROLS

        drifted_tuple = copy.deepcopy(tuple_values); drifted_tuple["communityToolkitAspireDapr"] = "0.0.0-drift"
        write_text(catalog_path, fixture_catalog(drifted_tuple))
        try:
            VALIDATOR.validate_baseline(workspace, baseline_path)
        except VALIDATOR.ValidationError as error:
            assert "Central package pin drift: CommunityToolkit.Aspire.Hosting.Dapr" in str(error), str(error)
        else:
            raise AssertionError("baseline mutation was accepted: central catalog drift")
        finally:
            write_text(catalog_path, fixture_catalog(tuple_values))
        VALIDATOR.validate_baseline(workspace, baseline_path)
        write_json(paths["packet"], packet)
        VALIDATOR.validate_packet(workspace, paths["packet"], baseline_path, baseline)
        return packet_scenarios, len(baseline_controls) + 1, len(AUTHORITY_CONTROLS)


def main() -> int:
    assert_historical_baseline_bytes()
    tuples = [VALIDATOR.read_json(path)["tuple"] for path in BASELINES]
    results = []
    for path, own_tuple in zip(BASELINES, tuples, strict=True):
        others = [other for other in tuples if other != own_tuple]
        packet_scenarios, baseline_scenarios, authority_controls = run_baseline(path, others)
        results.append((path.name, packet_scenarios, baseline_scenarios, authority_controls))
        print(
            f"{path.name}: {packet_scenarios} packet scenarios, {baseline_scenarios} baseline controls "
            f"and {authority_controls} authority controls passed"
        )
    packet_counts = {packet for _, packet, _, _ in results}
    assert packet_counts == {PACKET_SCENARIOS_PER_BASELINE}, packet_counts
    summary = mutation_summary_line(
        PACKET_SCENARIOS_PER_BASELINE,
        len(results),
        sum(baseline for _, _, baseline, _ in results),
        sum(authority for _, _, _, authority in results),
        len(HISTORICAL_BASELINE_SHA256),
    )
    # The validator requires the recorded mutation-controls outcome to be exactly this line, with
    # every count; a changed suite must update the validator's expected counts in the same change.
    assert VALIDATOR.MUTATION_CONTROLS_RESULT.fullmatch(summary) is not None, summary
    assert summary == RESULT_LINE, f"self-test printed '{summary}' but the validator requires '{RESULT_LINE}'"
    current_spec = importlib.util.spec_from_file_location("g6_current_mutations", SCRIPT.with_name("test_runtime_toolchain_v2.py"))
    assert current_spec is not None and current_spec.loader is not None
    current = importlib.util.module_from_spec(current_spec)
    current_spec.loader.exec_module(current)
    assert current.run_controls() == 103
    print(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
