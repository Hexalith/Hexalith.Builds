#!/usr/bin/env python3
"""Hermetic mutation tests for the G-6 evidence validator."""

from __future__ import annotations

import copy
import importlib.util
import json
import subprocess
import tempfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate-runtime-toolchain-evidence.py")
REAL_BASELINE = SCRIPT.with_name("runtime-toolchain-baseline.json")
SPEC = importlib.util.spec_from_file_location("g6_validator", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


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


def expect_rejected(workspace: Path, baseline_path: Path, baseline: dict, packet_path: Path, packet: dict, label: str) -> None:
    write_json(packet_path, packet)
    try:
        VALIDATOR.validate_packet(workspace, packet_path, baseline_path, baseline)
    except VALIDATOR.ValidationError:
        return
    raise AssertionError(f"mutation was accepted: {label}")


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


def main() -> int:
    baseline_template = VALIDATOR.read_json(REAL_BASELINE)
    with tempfile.TemporaryDirectory(prefix="g6-validator-") as temporary:
        workspace = Path(temporary)
        builds = workspace / "references/Hexalith.Builds"
        baseline_path = builds / "Tools/runtime-toolchain-baseline.json"
        write_json(baseline_path, baseline_template)

        for relative in baseline_template["pinAudit"]["globalJson"]:
            write_json(workspace / relative, {"sdk": {"version": VALIDATOR.EXPECTED_TUPLE["dotnetSdk"]}})
        for relative in baseline_template["pinAudit"]["appHostProjects"]:
            write_text(workspace / relative, f'<Project Sdk="Aspire.AppHost.Sdk/{VALIDATOR.EXPECTED_TUPLE["aspireSdk"]}">\n</Project>\n')
        literal_contents: dict[str, list[str]] = {}
        for item in baseline_template["pinAudit"]["literalPins"]:
            literal_contents.setdefault(item["path"], []).append(item["value"])
        for relative, values in literal_contents.items():
            write_text(workspace / relative, "\n".join(values) + "\n")
        catalog = "<Project><ItemGroup>" + "".join(
            f'<PackageVersion Include="{package}" Version="{version}" />'
            for package, version in {
                "CommunityToolkit.Aspire.Hosting.Dapr": VALIDATOR.EXPECTED_TUPLE["communityToolkitAspireDapr"],
                "Dapr.Client": VALIDATOR.EXPECTED_TUPLE["daprDotnetPackages"],
                "Dapr.Workflow": VALIDATOR.EXPECTED_TUPLE["daprDotnetPackages"],
                "Microsoft.FluentUI.AspNetCore.Components": VALIDATOR.EXPECTED_TUPLE["fluentUi"],
                "NSubstitute": VALIDATOR.EXPECTED_TUPLE["nSubstitute"],
                "Fluxor": VALIDATOR.EXPECTED_TUPLE["fluxor"],
            }.items()
        ) + "</ItemGroup></Project>\n"
        write_text(builds / "Props/Directory.Packages.props", catalog)
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
        write_text(paths["marker"], "G-6 source binding\n")
        revision = initialize_git(workspace)
        baseline = VALIDATOR.validate_baseline(workspace, baseline_path)

        selectors = [f"fixture.support.{number:02d}" for number in range(1, 22)]
        expected_cases = [1] * 20 + [13]
        observations = {
            "topology": {"eventStoreProcessCount": 2, "eventStoreSidecarCount": 2, "independentProcessIdentities": True},
            "profile": {"stateStoreType": "state.postgresql"},
            "runtime": {"dapr": "1.18.2"},
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
                outcome = ".NET SDK 10.0.400; Aspire CLI 13.5.3; Dapr CLI 1.18.0; runtime 1.18.2"
            elif purpose == "G-6 mutation controls":
                outcome = "22 scenarios passed"
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
                "observedUtc": "2026-09-06T12:00:00Z", **VALIDATOR.EXPECTED_TUPLE,
            })
            write_json(paths["commands"], {
                "schema": "hexalith.runtime-toolchain-command-record.v1", "commands": command_results,
            })
            write_json(paths["dispositions"], {
                "schema": "hexalith.runtime-toolchain-dispositions.v1",
                "daprSupportDisposition": "approved-explicit-exception-not-support-table-listed",
                "communityToolkitAspireDapr": "approved-prerelease-exception",
                "fluentUi": "approved-release-candidate-exception", "nSubstitute": "stable", "fluxor": "stable",
                "Dapr": "catalog-only-not-activated", "Dapr.Workflow": "catalog-only-unselected",
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
            "capturedUtc": "2026-09-06T12:00:00Z",
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
            "observedVersions": copy.deepcopy(VALIDATOR.EXPECTED_TUPLE),
            "testCounts": {
                "qualification": {"selectors": 1, "total": 1, "passed": 1, "failed": 0, "skipped": 0},
                "support": {"selectors": 21, "total": 33, "passed": 33, "failed": 0, "skipped": 0},
            },
            "topology": {"eventStoreProcesses": 2, "eventStoreSidecars": 2, "independentProcessIdentities": True, "sharedStateStore": "state.postgresql"},
            "lifecycle": {"singleExecution": True, "duplicateWork": 0, "ownerStopped": True, "survivorReplayExact": True, "ownerRestarted": True, "restartedReplayExact": True, "authorityUnchanged": True, "persistedStateExact": True},
            "approval": {"approvedBy": "Jérôme Piquot", "approvedOn": "2026-09-06", "ownerRoles": ["Builds", "Platform", "FrontComposer/Web"], "supportTableListed": False, "decision": "approved-explicit-exception"},
            "sentinelScan": {"passed": True, "matches": 0, "rawDiagnosticsRetained": False},
            "rollback": {"requiresDomainDataMutation": False, "action": "restore prior pins"},
            "containment": copy.deepcopy(VALIDATOR.EXPECTED_CONTAINMENT), "status": "accepted",
        }
        write_json(paths["packet"], packet)
        VALIDATOR.validate_packet(workspace, paths["packet"], baseline_path, baseline)

        packet_mutations = []
        stale_hash = copy.deepcopy(packet); stale_hash["artifacts"][0]["sha256"] = "b" * 64
        packet_mutations.append(("stale artifact hash", stale_hash))
        tuple_mismatch = copy.deepcopy(packet); tuple_mismatch["observedVersions"]["daprRuntime"] = "1.18.1"
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

    print("G6-EVIDENCE-MUTATIONS-PASSED: 22 scenarios")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
