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
The fixture records this script's own final output line as the mutation-controls
outcome, and main() proves the validator's pattern matches the line it prints.
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
# Literal pins whose own text names no role; they cannot ground a tuple CLI/runtime value.
ROLE_LESS_PIN_KEYS = ("version:", "default:")
SPEC = importlib.util.spec_from_file_location("g6_validator", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)
# The validator requires the recorded mutation-controls outcome to report this many packet
# scenarios; main() asserts that every baseline ran exactly this many.
PACKET_SCENARIOS_PER_BASELINE = VALIDATOR.MUTATION_SCENARIOS_PER_BASELINE


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


def write_literal_pins(workspace: Path, literal_pins: list[dict[str, str]]) -> None:
    """Write each audited literal pin as an active line of its fixture file."""
    contents: dict[str, list[str]] = {}
    for item in literal_pins:
        contents.setdefault(item["path"], []).append(item["value"])
    for relative, values in contents.items():
        write_text(workspace / relative, "\n".join(values) + "\n")


def expect_pinned_baseline_rejected(
    workspace: Path, baseline_path: Path, baseline: dict, original_pins: list[dict[str, str]], label: str, message: str,
) -> None:
    """Reject a baseline whose changed literal pins are really present in the fixture files."""
    write_literal_pins(workspace, baseline["pinAudit"]["literalPins"])
    try:
        expect_baseline_rejected(workspace, baseline_path, baseline, label, message)
    finally:
        write_literal_pins(workspace, original_pins)


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
        write_literal_pins(workspace, original_pins)
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
                # The real final output line format, as main() prints it.
                outcome = mutation_summary_line(
                    PACKET_SCENARIOS_PER_BASELINE, len(BASELINES), 1, 1, len(HISTORICAL_BASELINE_SHA256))
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

        # The mutation-controls outcome must carry the self-test's real result line, so a paraphrase
        # (which the count-only check once required) and a line reporting a short suite both fail.
        for label, outcome, message in (
            ("paraphrased mutation-controls outcome", f"{PACKET_SCENARIOS_PER_BASELINE} scenarios passed",
             "does not record the self-test result line"),
            ("mutation-controls scenario count drift",
             mutation_summary_line(PACKET_SCENARIOS_PER_BASELINE - 1, len(BASELINES), 1, 1, len(HISTORICAL_BASELINE_SHA256)),
             "Mutation command result count drift"),
        ):
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
        # The tuple CLI version appears only in pins whose text names no role.
        role_less = copy.deepcopy(baseline_template)
        role_less["pinAudit"]["literalPins"] = [
            item for item in original_pins
            if tuple_values["daprCli"] not in item["value"] or item["value"].strip().startswith(ROLE_LESS_PIN_KEYS)]
        assert any(tuple_values["daprCli"] in item["value"] for item in role_less["pinAudit"]["literalPins"]), (
            "role-less pin control must keep the Dapr CLI version in a role-less pin")
        authority_baselines.append(("Dapr CLI only in role-less pins", role_less,
                                    f"Tuple daprCli {tuple_values['daprCli']} has no audited literal pin of the same role"))
        unaudited_sdk = copy.deepcopy(baseline_template); unaudited_sdk["tuple"]["aspireSdk"] = "13.99.0"
        authority_baselines.append(("unaudited aspireSdk", unaudited_sdk,
                                    "Tuple aspireSdk 13.99.0 is not among the audited AppHost SDK pins"))
        for label, mutation, message in authority_baselines:
            expect_baseline_rejected(workspace, mutated_baseline_path, mutation, label, message)
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
                expect_pinned_baseline_rejected(workspace, mutated_baseline_path, shifted, original_pins, label,
                                                f"differs from tuple {field} {version}")
                AUTHORITY_CONTROLS.append(label)
        # Positive control: a listed pair equal to the tuple's own runtime and SDK is accepted.
        listed_pair = copy.deepcopy(baseline_template)
        listed_pair["dispositions"]["dapr"].update(
            supportTableListed=True, listedRuntime=tuple_values["daprRuntime"],
            listedDotnetSdk=tuple_values["daprDotnetPackages"])
        write_json(mutated_baseline_path, listed_pair)
        try:
            VALIDATOR.validate_baseline(workspace, mutated_baseline_path)
        finally:
            mutated_baseline_path.unlink()
        AUTHORITY_CONTROLS.append("listed Dapr pair equal to the tuple")
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
    # The validator reads this exact line from the recorded mutation-controls outcome.
    grounded = VALIDATOR.MUTATION_CONTROLS_RESULT.fullmatch(summary)
    assert grounded is not None and int(grounded["scenarios"]) == VALIDATOR.MUTATION_SCENARIOS_PER_BASELINE, summary
    print(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
