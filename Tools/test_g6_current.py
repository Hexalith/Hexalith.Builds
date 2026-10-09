#!/usr/bin/env python3
"""Focused controls for current G-6 applicability and retained critical proof."""

from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


TOOLS = Path(__file__).resolve().parent
WORKSPACE = TOOLS.parents[2]
SPEC = importlib.util.spec_from_file_location("g6_current", TOOLS / "g6_current.py")
G6 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(G6)
POLICY = TOOLS / "g6-current-policy.json"
HISTORICAL = WORKSPACE / "_bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain-20261001/attempt-16"


class CurrentG6Tests(unittest.TestCase):
    def test_sdk_scanner_recognizes_explicit_imports_and_property_selection(self) -> None:
        import xml.etree.ElementTree as ET
        document = ET.fromstring('<Project><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /><Import Project="Sdk.targets" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /></Project>')
        self.assertEqual(G6.apphost_sdk_versions(document, {"HexalithAspireAppHostSdkVersion": "13.6.0"}), {"13.6.0"})
        with self.assertRaisesRegex(G6.G6Error, "Unresolved Aspire.AppHost.Sdk catalog field"):
            G6.apphost_sdk_versions(document, {})
        changed = ET.fromstring('<Project><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="13.7.0" /></Project>')
        self.assertEqual(G6.apphost_sdk_versions(changed, {}), {"13.7.0"})

    def test_sdk_import_groups_preserve_activation(self) -> None:
        spec = importlib.util.spec_from_file_location("g6_group_controls", TOOLS / "test_runtime_toolchain_v2.py")
        controls = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(controls)
        self.assertEqual(controls.run_sdk_import_group_controls(), 6)

    def test_evaluated_catalog_imports_conditions_and_nested_properties(self) -> None:
        spec = importlib.util.spec_from_file_location("g6_evaluated_fixture_tests", TOOLS / "test_runtime_toolchain_v2.py")
        controls = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(controls)
        self.assertEqual(controls.run_evaluated_catalog_controls(), 47)

    def test_material_fingerprint_covers_live_sample_and_ignores_unrelated_docs(self) -> None:
        policy = G6.policy_document(POLICY)
        original = G6.tracked_material(WORKSPACE, policy)
        sample = "references/Hexalith.EventStore/samples/Hexalith.EventStore.Sample/Program.cs"
        self.assertTrue((WORKSPACE / sample).is_file())
        observed = []
        hash_file = G6.file_hash

        def altered(path: Path) -> str:
            relative = path.relative_to(WORKSPACE).as_posix()
            observed.append(relative)
            return "0" * 64 if relative == sample else hash_file(path)

        with patch.object(G6, "file_hash", side_effect=altered):
            changed = G6.tracked_material(WORKSPACE, policy)
        self.assertIn(sample, observed)
        self.assertNotEqual(original["fingerprint"], changed["fingerprint"])
        self.assertFalse(any(path.startswith("_bmad-output/") for path in observed))

    def test_historical_capture_revalidates_and_tamper_is_rejected(self) -> None:
        names = ("observations.json", "test-results.json", "deterministic-support.json", "capture-validation.json")
        directory = HISTORICAL / "capture"
        relative = directory.relative_to(WORKSPACE).as_posix()
        logs = []
        for purpose in G6.REQUIRED_PURPOSES:
            name = __import__("re").sub(r"[^a-z0-9]+", "-", purpose.lower()).strip("-") + ".log"
            path = HISTORICAL / "logs" / name
            logs.append({"purpose": purpose, "path": path.relative_to(WORKSPACE).as_posix(),
                         "sha256": G6.file_hash(path)})
        qualification = {"receipts": {
            "captureDirectory": relative,
            "files": [{"name": name, "sha256": G6.file_hash(directory / name)} for name in names],
            "logs": logs,
            "cleanup": {"path": (HISTORICAL / "cleanup.json").relative_to(WORKSPACE).as_posix(),
                        "sha256": G6.file_hash(HISTORICAL / "cleanup.json")},
        }, "cleanup": {field: True for field in ("ownedProcessesStopped", "scratchRemoved", "fixtureScratchRemoved",
                                                   "sharedResourcesUnchanged", "ownedContainersRemoved")}}
        G6.validate_receipts(WORKSPACE, HISTORICAL / "packet.json", qualification, "1.18.2")
        broken = copy.deepcopy(qualification)
        broken["receipts"]["files"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(G6.G6Error, "capture file changed"):
            G6.validate_receipts(WORKSPACE, HISTORICAL / "packet.json", broken, "1.18.2")
        missing = copy.deepcopy(qualification)
        missing["receipts"]["files"].pop()
        with self.assertRaisesRegex(G6.G6Error, "incomplete"):
            G6.validate_receipts(WORKSPACE, HISTORICAL / "packet.json", missing, "1.18.2")
        with self.assertRaisesRegex(G6.G6Error, "not in this result's run directory"):
            G6.validate_receipts(WORKSPACE, WORKSPACE / "forged-current-result.json", qualification, "1.18.2")

    def test_recomputed_digest_cannot_turn_21_cases_or_stale_source_into_qualification(self) -> None:
        source = {"rootSha": "a" * 40, "gitlinks": []}
        approved_policy = copy.deepcopy(G6.policy_document(POLICY))
        approved_policy["approval"].update(decision="approved", approvedBy="Example Owner",
                                           approvedAtUtc="2026-10-02T10:00:00Z", reference="reviewed decision")
        approved_policy["exceptions"]["communityToolkitAspireDapr"] = "approved-prerelease-exception"
        current = {
            "schema": "hexalith.g6-current-audit.v1", "policySha256": "b" * 64,
            "source": source, "materialInputs": {"fingerprint": "c" * 64, "fileCount": 1},
            "effectiveTuple": approved_policy["tuple"], "resolvedPackages": [],
            "tupleApproved": True, "issues": [],
        }
        examples = {
            "fresh EventStore qualifier build": "dotnet build tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Hexalith.EventStore.Server.LiveSidecar.Tests.csproj",
            "fresh EventStore support build": "dotnet build tests/Hexalith.EventStore.Server.Tests/Hexalith.EventStore.Server.Tests.csproj",
            "real PostgreSQL two-sidecar stop/restart qualifier": "dotnet LiveSidecar.Tests.dll -method ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants",
            "21 exact deterministic support selectors": "dotnet Server.Tests.dll -method Server.Tests.Selector",
            "strict EventStore capture validation": "python3 tools/validate-oq8-platform-evidence.py",
        }
        commands = [{"purpose": purpose, "command": examples[purpose], "exitCode": 0}
                    for purpose in sorted(G6.REQUIRED_PURPOSES)]
        evidence = {
            "schema": "hexalith.g6-current-evidence.v1", "audit": copy.deepcopy(current),
            "qualification": {
                "status": "qualified", "runner": "xUnit.net v3",
                "fixture": "IdempotencyAdmissionOq8PostgresqlTests.ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants",
                "commands": commands,
                "tests": {"qualifier": {"total": 1, "passed": 1, "failed": 0, "skipped": 0},
                          "support": {"total": 21, "passed": 21, "failed": 0, "skipped": 0}},
                "cleanup": {field: True for field in ("ownedProcessesStopped", "scratchRemoved", "fixtureScratchRemoved",
                                                      "sharedResourcesUnchanged", "ownedContainersRemoved")},
                "environment": {"runner": "test", "executionScope": "ci",
                                "buildsExecutionSha": G6.builds_execution_sha(WORKSPACE, "ci")},
                "limitations": [], "receipts": {},
            },
            "approval": approved_policy["approval"], "artifactSha256": "",
        }

        def check(value: dict, release: bool = False) -> None:
            value["artifactSha256"] = G6.digest({key: item for key, item in value.items() if key != "artifactSha256"})
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "result.json"
                path.write_text(json.dumps(value))
                G6.validate(WORKSPACE, POLICY, path, release)

        with patch.object(G6, "policy_document", return_value=approved_policy), \
             patch.object(G6, "audit", return_value=current), \
             patch.object(G6, "validate_receipts"), \
             patch.object(G6, "repository_paths", return_value=[]), \
             patch.object(G6, "git", return_value=""):
            with self.assertRaisesRegex(G6.G6Error, "support failed"):
                check(evidence)
            evidence["qualification"]["tests"]["support"] = {"total": 33, "passed": 33, "failed": 0, "skipped": 0}
            check(evidence)
            mutations = [
                ("controlled tuple", lambda x: x["audit"]["effectiveTuple"].update(communityToolkitAspireDapr="13.6.0-beta.910")),
                ("qualifier failed", lambda x: x["qualification"]["tests"].update(qualifier={"total": 1, "passed": 0, "failed": 1, "skipped": 0})),
                ("qualifier skipped", lambda x: x["qualification"]["tests"].update(qualifier={"total": 1, "passed": 0, "failed": 0, "skipped": 1})),
                ("support skipped", lambda x: x["qualification"]["tests"].update(support={"total": 33, "passed": 32, "failed": 0, "skipped": 1})),
                ("cleanup failed", lambda x: x["qualification"]["cleanup"].update(scratchRemoved=False)),
                ("owner decision missing", lambda x: x["approval"].update(decision="pending")),
                ("execution SHA changed", lambda x: x["qualification"]["environment"].update(buildsExecutionSha="0" * 40)),
            ]
            for label, mutation in mutations:
                with self.subTest(label=label):
                    altered = copy.deepcopy(evidence)
                    mutation(altered)
                    with self.assertRaises(G6.G6Error):
                        check(altered)
            missing = copy.deepcopy(evidence)
            del missing["qualification"]["tests"]["qualifier"]
            with self.assertRaisesRegex(G6.G6Error, "critical test result set"):
                check(missing)
            evidence["audit"]["source"] = {"rootSha": "d" * 40, "gitlinks": []}
            with self.assertRaisesRegex(G6.G6Error, "source SHA or gitlinks differ"):
                check(evidence)
            evidence["qualification"]["environment"].update(
                executionScope="release", buildsExecutionSha=G6.builds_execution_sha(WORKSPACE, "release"))
            with self.assertRaisesRegex(G6.G6Error, "source SHA or gitlinks differ"):
                check(evidence, release=True)
            evidence["audit"]["source"] = source
            evidence["qualification"]["environment"].update(
                executionScope="ci", buildsExecutionSha=G6.builds_execution_sha(WORKSPACE, "ci"))
            with self.assertRaisesRegex(G6.G6Error, "execution scope"):
                check(evidence, release=True)


if __name__ == "__main__":
    unittest.main()
