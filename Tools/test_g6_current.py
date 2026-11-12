#!/usr/bin/env python3
"""Focused controls for current G-6 applicability and retained critical proof."""

from __future__ import annotations

import copy
import importlib.util
import json
import re
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


TOOLS = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("g6_current", TOOLS / "g6_current.py")
G6 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(G6)
# The policy CI and release select; the legacy g6-current-policy.json still names the retired OQ8 validator.
POLICY = TOOLS / "g6-current-policy-2026-10-20.json"


class CurrentG6Tests(unittest.TestCase):
    def setUp(self) -> None:
        """Generate hermetic source and retained evidence; neither claims live qualification."""
        temporary = tempfile.TemporaryDirectory(prefix="g6-current-controls-")
        self.addCleanup(temporary.cleanup)
        self.workspace = Path(temporary.name)
        self.capture_run = self.workspace / "synthetic-run"
        self.capture = self.capture_run / "capture"
        self.capture.mkdir(parents=True)
        workflows = self.workspace / ".github/workflows"
        workflows.mkdir(parents=True)
        sha = "1" * 40
        (workflows / "ci.yml").write_text(
            "jobs:\n  synthetic:\n    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@" + sha + "\n")
        (workflows / "release.yml").write_text(
            "jobs:\n  synthetic:\n    steps:\n      - uses: actions/checkout@" + "2" * 40 + "\n"
            "        with:\n          repository: Hexalith/Hexalith.Builds\n          ref: " + sha + "\n")
        focused = {"summary": {"tests": 1, "passed": 1, "failed": 0, "skipped": 0},
                   "test": {"name": "Hexalith.EventStore.Server.LiveSidecar.Tests.Actors."
                                    "IdempotencyAdmissionOq8PostgresqlTests."
                                    "ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants",
                            "status": "passed"}}
        selectors = [f"Hexalith.EventStore.Server.Tests.Synthetic.Case{index}" for index in range(21)]
        support = {"summary": {"tests": 33, "passed": 33, "failed": 0, "skipped": 0},
                   "selectors": selectors,
                   "methods": [{"identity": identity, "observedCases": 2 if index < 12 else 1,
                                "passedCases": 2 if index < 12 else 1}
                               for index, identity in enumerate(selectors)]}
        for name, document in (("test-results.json", focused), ("deterministic-support.json", support),
                               ("observations.json", {"syntheticFixture": True}),
                               ("capture-validation.json", {"syntheticFixture": True})):
            (self.capture / name).write_text(json.dumps(document), encoding="utf-8")
        logs = self.capture_run / "logs"
        logs.mkdir()
        proofs = {
            "fresh EventStore qualifier build": "Build succeeded.\n0 Error(s)",
            "fresh EventStore support build": "Build succeeded.\n0 Error(s)",
            "real PostgreSQL two-sidecar stop/restart qualifier":
                "Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0",
            "21 exact deterministic support selectors":
                "Total: 33, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0",
        }
        for purpose, proof in proofs.items():
            name = re.sub(r"[^a-z0-9]+", "-", purpose.lower()).strip("-") + ".log"
            (logs / name).write_text("Synthetic validator test input\n" + proof + "\n")
        containers = [{"role": role, "id": f"{index:064x}"}
                      for index, role in enumerate(("placement", "scheduler", "redis", "postgresql"), 1)]
        cleanup = {"before": [], "after": [], "ownedContainers": containers,
                   "removedContainerIds": [item["id"] for item in containers],
                   "ownedProcessesStopped": True, "scratchRemoved": True, "fixtureScratchRemoved": True}
        (self.capture_run / "cleanup.json").write_text(json.dumps(cleanup), encoding="utf-8")

    def material_workspace(self, policy: dict) -> None:
        """Populate real Git inventories covering the selected material paths and prefixes."""
        paths = set(policy["materialFiles"])
        paths.update(prefix + "synthetic-source.cs" for prefix in policy["materialPrefixes"])
        modules = sorted({Path(path).parts[1] for path in paths if path.startswith("references/")})
        (self.workspace / ".gitmodules").write_text("".join(
            f'[submodule "{name}"]\n\tpath = references/{name}\n\turl = https://example.invalid/{name}.git\n'
            for name in modules))
        for name in sorted(paths):
            path = self.workspace / name
            path.parent.mkdir(parents=True, exist_ok=True)
            if not path.exists():
                path.write_text("Synthetic material fingerprint fixture\n", encoding="utf-8")
        sample = self.workspace / "references/Hexalith.EventStore/samples/Hexalith.EventStore.Sample/Program.cs"
        sample.write_text("// synthetic live-sample material\n", encoding="utf-8")
        for root in [self.workspace / "references" / name for name in modules] + [self.workspace]:
            subprocess.run(["git", "-C", str(root), "init", "-q"], check=True, capture_output=True)
            command = ["git", "-C", str(root), "add", "--", "."]
            if root == self.workspace:
                command.append(":(exclude)references")
            subprocess.run(command, check=True, capture_output=True)

    def test_restored_hosting_previews_are_exact_and_specific_to_the_selected_sdk(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / "consumer.csproj"
            project.write_text("<Project />")
            (root / "obj").mkdir()
            assets = root / "obj/project.assets.json"
            for sdk, preview, allowed in (
                ("13.6.5", "13.6.5-preview.1.26559.7", True),
                ("13.6.5", "13.6.4-preview.1.26523.11", False),
                ("13.6.5", "13.6.5-preview.1.26559.8", False),
                ("13.6.4", "13.6.4-preview.1.26523.11", True),
                ("13.6.1", "13.6.1-preview.1.26506.6", True),
                ("13.6.0", "13.6.0-preview.1.26479.8", True),
                ("13.6.1", "13.6.0-preview.1.26479.8", False),
                ("13.6.0", "13.6.1-preview.1.26506.6", False),
                ("13.6.1", "13.6.1-preview.1.26506.7", False),
            ):
                with self.subTest(sdk=sdk, preview=preview):
                    assets.write_text(json.dumps({"libraries": {
                        "Aspire.Hosting.Keycloak/" + preview: {"type": "package"},
                        "Aspire.Hosting.Kubernetes/" + preview: {"type": "package"},
                    }}))
                    graph, issues = G6.resolved_graph(root, {"resolvedProjects": ["consumer.csproj"]}, {"aspireSdk": sdk})
                    self.assertEqual(2, len(graph[0]["packages"]))
                    self.assertEqual(0 if allowed else 2, len(issues))

    def test_sdk11_tuple_authorization_still_requires_actual_behavior_and_source_evidence(self) -> None:
        current_policy = TOOLS / "g6-current-policy-2026-11-12.json"
        current = G6.policy_document(current_policy)
        self.assertEqual("Current user authorization", current["approval"]["approvedBy"])
        self.assertIn("not unseen qualification result acceptance", current["approval"]["reference"])
        self.assertEqual("11.0.100", current["tuple"]["dotnetSdk"])
        self.assertEqual("1.19.1", current["tuple"]["daprRuntime"])
        with patch.dict(globals(), POLICY=current_policy):
            # Exercise the same real validator refusals for failed/skipped live cases,
            # stale source, retargeted/missing AppHost builds and failed cleanup.
            self.test_recomputed_digest_cannot_turn_21_cases_or_stale_source_into_qualification()

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
        self.material_workspace(policy)
        original = G6.tracked_material(self.workspace, policy)
        sample = "references/Hexalith.EventStore/samples/Hexalith.EventStore.Sample/Program.cs"
        sample_path = self.workspace / sample
        self.assertTrue(sample_path.is_file())
        docs = self.workspace / "_bmad-output/unrelated.md"
        docs.parent.mkdir(parents=True)
        docs.write_text("Unrelated documentation must not change qualification material.\n")
        self.assertEqual(original, G6.tracked_material(self.workspace, policy))
        sample_path.write_text("// changed synthetic live-sample material\n")
        changed = G6.tracked_material(self.workspace, policy)
        self.assertNotEqual(original["fingerprint"], changed["fingerprint"])
        self.assertEqual(original["fileCount"], changed["fileCount"])

    def test_synthetic_capture_revalidates_and_tamper_is_rejected(self) -> None:
        names = ("observations.json", "test-results.json", "deterministic-support.json")
        directory = self.capture_run / "capture"
        relative = directory.relative_to(self.workspace).as_posix()
        logs = []
        for purpose in G6.REQUIRED_PURPOSES:
            name = re.sub(r"[^a-z0-9]+", "-", purpose.lower()).strip("-") + ".log"
            path = self.capture_run / "logs" / name
            logs.append({"purpose": purpose, "path": path.relative_to(self.workspace).as_posix(),
                         "sha256": G6.file_hash(path)})
        qualification = {"receipts": {
            "captureDirectory": relative,
            "files": [{"name": name, "sha256": G6.file_hash(directory / name)} for name in names],
            "logs": logs,
            "cleanup": {"path": (self.capture_run / "cleanup.json").relative_to(self.workspace).as_posix(),
                        "sha256": G6.file_hash(self.capture_run / "cleanup.json")},
        }, "cleanup": {field: True for field in ("ownedProcessesStopped", "scratchRemoved", "fixtureScratchRemoved",
                                                   "sharedResourcesUnchanged", "ownedContainersRemoved")},
            "tests": {"qualifier": {"total": 1, "passed": 1, "failed": 0, "skipped": 0},
                      "support": {"total": 33, "passed": 33, "failed": 0, "skipped": 0}}}
        self.assertFalse((self.workspace / "references/Hexalith.EventStore/tools/validate-oq8-platform-evidence.py").exists())
        G6.validate_receipts(self.workspace, self.capture_run / "packet.json", qualification)
        broken = copy.deepcopy(qualification)
        broken["receipts"]["files"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(G6.G6Error, "capture file changed"):
            G6.validate_receipts(self.workspace, self.capture_run / "packet.json", broken)
        missing = copy.deepcopy(qualification)
        missing["receipts"]["files"].pop()
        with self.assertRaisesRegex(G6.G6Error, "incomplete"):
            G6.validate_receipts(self.workspace, self.capture_run / "packet.json", missing)
        retired = copy.deepcopy(qualification)
        retired["receipts"]["files"].append({"name": "capture-validation.json",
                                             "sha256": G6.file_hash(directory / "capture-validation.json")})
        with self.assertRaisesRegex(G6.G6Error, "incomplete"):
            G6.validate_receipts(self.workspace, self.capture_run / "packet.json", retired)
        with self.assertRaisesRegex(G6.G6Error, "not in this result's run directory"):
            G6.validate_receipts(self.workspace, self.workspace / "forged-current-result.json", qualification)
        for label, counts in (("qualifier", {"total": 1, "passed": 0, "failed": 1, "skipped": 0}),
                              ("support", {"total": 21, "passed": 21, "failed": 0, "skipped": 0})):
            with self.subTest(contradiction=label):
                contradicted = copy.deepcopy(qualification)
                contradicted["tests"][label] = counts
                with self.assertRaisesRegex(G6.G6Error, "contradict the retained test documents"):
                    G6.validate_receipts(self.workspace, self.capture_run / "packet.json", contradicted)

    def test_retained_test_documents_must_prove_exact_qualifier_and_support_passes(self) -> None:
        source = self.capture_run / "capture"
        tests = {"qualifier": {"total": 1, "passed": 1, "failed": 0, "skipped": 0},
                 "support": {"total": 33, "passed": 33, "failed": 0, "skipped": 0}}
        focused = json.loads((source / "test-results.json").read_text(encoding="utf-8"))
        support = json.loads((source / "deterministic-support.json").read_text(encoding="utf-8"))
        mutations = [
            ("qualifier failed", "test-results.json",
             lambda f, s: f["summary"].update(passed=0, failed=1), "exactly 1 of 1"),
            ("qualifier identity", "test-results.json",
             lambda f, s: f["test"].update(name="Hexalith.EventStore.Other"), "identity or status"),
            ("qualifier status", "test-results.json",
             lambda f, s: f["test"].update(status="skipped"), "identity or status"),
            ("support skipped", "deterministic-support.json",
             lambda f, s: s["summary"].update(passed=32, skipped=1), "all 33 cases"),
            ("support unrecognized", "deterministic-support.json",
             lambda f, s: s.update(unrecognizedCases=[{"identitySha256": "0" * 64, "status": "passed"}]), "unrecognized"),
            ("support selector dropped", "deterministic-support.json",
             lambda f, s: s.update(selectors=s["selectors"][:-1]), "21 exact selectors"),
            ("support selector unobserved", "deterministic-support.json",
             lambda f, s: s["methods"][0].update(observedCases=0, passedCases=0), "observed passing"),
            ("support case total", "deterministic-support.json",
             lambda f, s: s["methods"][0].update(observedCases=s["methods"][0]["observedCases"] + 1,
                                                 passedCases=s["methods"][0]["passedCases"] + 1), "do not total"),
        ]
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "test-results.json").write_text(json.dumps(focused), encoding="utf-8")
            (directory / "deterministic-support.json").write_text(json.dumps(support), encoding="utf-8")
            G6.validate_retained_tests(directory, tests)
            for label, _, mutation, message in mutations:
                with self.subTest(label=label):
                    altered_focused, altered_support = copy.deepcopy(focused), copy.deepcopy(support)
                    mutation(altered_focused, altered_support)
                    (directory / "test-results.json").write_text(json.dumps(altered_focused), encoding="utf-8")
                    (directory / "deterministic-support.json").write_text(json.dumps(altered_support), encoding="utf-8")
                    with self.assertRaisesRegex(G6.G6Error, message):
                        G6.validate_retained_tests(directory, tests)

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
        }
        self.assertNotIn("strict EventStore capture validation", G6.REQUIRED_PURPOSES)
        commands = [{"purpose": purpose, "command": examples[purpose], "exitCode": 0}
                    for purpose in sorted(G6.REQUIRED_PURPOSES)]
        apphosts = G6.required_apphost_builds()
        self.assertEqual(10, len(apphosts))
        self.assertEqual("apphost.cs", apphosts["AppHost build Hexalith.Platform"])
        commands += [{"purpose": purpose, "command": f"dotnet build [workspace]/selected/{project} --configuration Debug",
                      "exitCode": 0} for purpose, project in sorted(apphosts.items())]
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
                                "buildsExecutionSha": G6.builds_execution_sha(self.workspace, "ci")},
                "limitations": [], "receipts": {},
            },
            "approval": approved_policy["approval"], "artifactSha256": "",
        }

        def check(value: dict, release: bool = False) -> None:
            value["artifactSha256"] = G6.digest({key: item for key, item in value.items() if key != "artifactSha256"})
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "result.json"
                path.write_text(json.dumps(value))
                G6.validate(self.workspace, POLICY, path, release)

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
                ("AppHost build missing", lambda x: x["qualification"].update(commands=[
                    item for item in x["qualification"]["commands"] if item["purpose"] != "AppHost build Hexalith.Projects.AppHost"])),
                ("AppHost build failed", lambda x: next(item for item in x["qualification"]["commands"]
                                                        if item["purpose"] == "AppHost build Hexalith.Platform").update(exitCode=1)),
                ("AppHost build retargeted", lambda x: next(item for item in x["qualification"]["commands"]
                                                            if item["purpose"] == "AppHost build Hexalith.Conversations.AppHost").update(
                    command="dotnet build [workspace]/selected/Other.AppHost.csproj")),
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
                executionScope="release", buildsExecutionSha=G6.builds_execution_sha(self.workspace, "release"))
            with self.assertRaisesRegex(G6.G6Error, "source SHA or gitlinks differ"):
                check(evidence, release=True)
            evidence["audit"]["source"] = source
            evidence["qualification"]["environment"].update(
                executionScope="ci", buildsExecutionSha=G6.builds_execution_sha(self.workspace, "ci"))
            with self.assertRaisesRegex(G6.G6Error, "execution scope"):
                check(evidence, release=True)


if __name__ == "__main__":
    unittest.main()
