"""Closed source-bound declaration/lifetime controls; no product qualification claim."""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Github/lib"))
import build_projects
import ci_config
import isolated_builds


def git(root, *arguments):
    return subprocess.check_output(["git", "-c", "core.hooksPath=" + os.devnull, "-C", str(root), *arguments],
        stderr=subprocess.DEVNULL).decode().strip()


def initialize(root):
    root.mkdir()
    git(root, "init", "--quiet", "--initial-branch=main")
    git(root, "config", "user.name", "Fixture")
    git(root, "config", "user.email", "fixture@example.invalid")


def commit(root):
    git(root, "add", ".")
    git(root, "commit", "--quiet", "-m", "test: source fixture")


def configuration():
    document = copy.deepcopy(json.loads((ROOT / "Github/lib/tests/fixtures/contract.json").read_bytes())[0]["document"])
    document.update(buildsTag="v6.1.0", solution="Fixture.slnx", moduleManifest="module.json",
        buildProjects=[{"id": "isolated", "project": "compat/Adapter.csproj", "isolatedContract": "compat/isolated.json"}],
        jobs={"windows": {"runner": "windows-latest"}})
    return document


class IsolatedContractTests(unittest.TestCase):
    def test_additive_variant_and_platforms_have_schema_loader_parity(self):
        validator = Draft202012Validator(json.loads((ROOT / "schemas/hexalith.ci.v1.json").read_bytes()))
        document = configuration()
        for runner in ("windows-latest", "macos-latest", "ubuntu-latest"):
            document["jobs"]["windows"]["runner"] = runner
            validator.validate(document)
            self.assertIs(document, ci_config.validate_config(document, repository="Example/Fixture"))
        mutations = [
            {"versions": {"HexalithEventStoreVersion": "4.2.0"}}, {"command": "build"},
            {"isolatedContract": "../foreign.json"}, {"isolatedContract": None},
            {"isolatedContract": "contract.xml"},
        ]
        for changes in mutations:
            with self.subTest(changes=changes):
                invalid = copy.deepcopy(document)
                invalid["buildProjects"][0].update(changes)
                self.assertFalse(validator.is_valid(invalid))
                with self.assertRaises(ValueError):
                    ci_config.validate_config(invalid, repository="Example/Fixture")
        for values in ({"moduleManifest": None}, {"moduleManifest": None, "publishedBuilds": "tuple.json"}):
            invalid = dict(document, **values)
            self.assertFalse(validator.is_valid(invalid))
            with self.assertRaises(ValueError):
                ci_config.validate_config(invalid, repository="Example/Fixture")

    def test_finite_variant_retains_platform_policy_and_cannot_alias_isolated_owner(self):
        document = configuration()
        finite = {"id": "finite", "project": "compat/Consumer.csproj", "versions": {"HexalithEventStoreVersion": "4.2.0"}}
        document["buildProjects"].append(finite)
        with self.assertRaisesRegex(ValueError, "Linux"):
            ci_config.validate_config(document, repository="Example/Fixture")
        document["jobs"] = {}
        ci_config.validate_config(document, repository="Example/Fixture")
        finite["project"] = "compat/Adapter.csproj"
        with self.assertRaisesRegex(ValueError, "duplicate"):
            ci_config.validate_config(document, repository="Example/Fixture")
        document["buildProjects"].reverse()
        with self.assertRaisesRegex(ValueError, "duplicate"):
            ci_config.validate_config(document, repository="Example/Fixture")


class IsolatedBuildTests(unittest.TestCase):
    def setUp(self):
        # Other owner suites explicitly reload this module during discovery.
        # Bind the current implementation at execution, without eager-loading
        # their fixtures or capturing a stale verifier in this test module.
        import module_preparation
        self.preparation = module_preparation
        self.temporary = tempfile.TemporaryDirectory(prefix="isolated-build-controls-")
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "module"
        initialize(self.root)
        self.adapter = self.root / "compat/Adapter.csproj"
        self.adapter.parent.mkdir()
        self.adapter.write_text('<Project><Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />'
            '<Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" /></Project>')
        self.child = self.root / "fixture/Child.csproj"
        self.child.parent.mkdir()
        self.child.write_text('<Project Sdk="Microsoft.NET.Sdk" />')
        for name in ("Boundary.props", "Boundary.targets"):
            (self.adapter.parent / name).write_text("<Project />")
        self.descriptor = self.adapter.parent / "isolated.json"
        self.value = {"schema": "hexalith.isolated-build.v1", "owner": "compat/Adapter.csproj",
            "projects": ["fixture/Child.csproj"], "roots": ["compat", "fixture"], "framework": "net11.0",
            "propertyPrefix": "FixtureBoundary", "properties": {"FixtureBoundaryMode": "package"},
            "props": "compat/Boundary.props", "targets": "compat/Boundary.targets"}
        self.descriptor.write_text(json.dumps(self.value))
        commit(self.root)
        self.prepared = SimpleNamespace(root=self.root, document=configuration())
        self.case = self.prepared.document["buildProjects"][0]
        self.workspace = self.base / "producer"
        self.workspace.mkdir()
        self.environment = SimpleNamespace(workspace=self.workspace, authority={"kind": "accepted-delivery"},
            values={"HEXALITH_SOURCE_MAPPING_HASH": "a" * 64})

    def evaluation(self):
        return json.dumps({"Properties": {"TargetFramework": "net11.0", "HexalithSourceMappingApplies": "true",
            "HexalithSourceMappingHash": "a" * 64, **dict.fromkeys(isolated_builds.FLAGS, "false")},
            "Items": {"ProjectReference": [{"FullPath": str(self.child), "ReferenceOutputAssembly": "false",
                "HexalithIsolatedBuildContract": "compat/isolated.json"}]}})

    def test_closed_descriptor_owner_paths_shipping_and_guard_refusals(self):
        self.assertEqual(self.value, isolated_builds.declaration(self.prepared, self.case, self.environment))
        for changes in ({"owner": "fixture/Child.csproj"}, {"roots": ["compat"]}, {"roots": ["compat", "COMPAT"]},
                {"projects": ["../foreign.csproj"]}, {"framework": "${{ injected }}"},
                {"properties": {"FixtureBoundaryMode": "$(Injected)"}}, {"extra": True}):
            with self.subTest(changes=changes):
                self.descriptor.write_text(json.dumps(dict(self.value, **changes)))
                commit(self.root)
                with self.assertRaises(ValueError):
                    isolated_builds.declaration(self.prepared, self.case, self.environment)
                self.descriptor.write_text(json.dumps(self.value))
                commit(self.root)
        for xml in ('<Target Name="HexalithReconcileSourceMapping" />', '<Target Name="hexalithreconcilesourcemapping" />',
                    '<Target Name="hEXALITHReconcileSourceMapping" />', '<UsingTask TaskName="Replacement" />', '<Exec Command="true" />'):
            original = self.adapter.read_text()
            self.adapter.write_text(original.replace("</Project>", xml + "</Project>"))
            commit(self.root)
            with self.subTest(xml=xml), self.assertRaises(ValueError):
                isolated_builds.declaration(self.prepared, self.case, self.environment)
            self.adapter.write_text(original)
            commit(self.root)

    def test_evaluated_reference_set_and_flags_fail_before_guard_or_restore(self):
        baseline = json.loads(self.evaluation())
        for label in ("missing", "duplicate", "shipping", "framework", "assembly", "descriptor", "inactive", "mapping-hash"):
            value = copy.deepcopy(baseline)
            if label == "missing": value["Items"]["ProjectReference"] = []
            elif label == "duplicate": value["Items"]["ProjectReference"] *= 2
            elif label == "shipping": value["Properties"]["IsPackable"] = "true"
            elif label == "framework": value["Properties"]["TargetFramework"] = "net10.0"
            elif label == "assembly": value["Items"]["ProjectReference"][0]["ReferenceOutputAssembly"] = "true"
            elif label == "inactive": value["Properties"]["HexalithSourceMappingApplies"] = "false"
            elif label == "mapping-hash": value["Properties"]["HexalithSourceMappingHash"] = "b" * 64
            else: value["Items"]["ProjectReference"][0]["HexalithIsolatedBuildContract"] = "compat/other.json"
            with self.subTest(label=label), patch("ci_pipeline.run_command", return_value=json.dumps(value)) as command:
                with self.assertRaises(ValueError):
                    isolated_builds.preflight(self.prepared, self.case, self.environment)
                self.assertEqual(1, command.call_count)
        with patch("ci_pipeline.run_command", side_effect=[self.evaluation(), ci_config.ConfigError("real guard refused")]) as command:
            with self.assertRaisesRegex(ValueError, "real guard refused"):
                isolated_builds.preflight(self.prepared, self.case, self.environment)
            self.assertIn("-target:HexalithReconcileSourceMapping", command.call_args.args[0])

    def test_independent_strict_commands_have_canonical_outputs_and_no_version_view(self):
        calls = []
        with patch("ci_pipeline.run_command", side_effect=[self.evaluation(), ""]) as preflight:
            isolated_builds.build(self.prepared, self.case, self.environment,
                ["-warnaserror", "-p:Configuration=Release"], lambda args, root: calls.append((args, root)))
        self.assertEqual(["restore", "build"], [args[1] for args, _ in calls])
        self.assertIn("--no-restore", calls[1][0])
        self.assertTrue(all("-warnaserror" in args and "-p:Configuration=Release" in args for args, _ in calls))
        self.assertTrue(all("-nodeReuse:false" in call.args[0] for call in preflight.call_args_list))
        self.assertTrue(all("-nodeReuse:false" in args for args, _ in calls))
        self.assertFalse(any("ArtifactsPath" in argument or "UseArtifactsOutput" in argument for args, _ in calls for argument in args))
        with patch.object(build_projects.sys, "platform", "win32"), patch.object(self.preparation, "verify_environment"):
            with build_projects.output_scope(self.prepared, self.environment):
                self.assertNotIn(build_projects.VARIABLE, os.environ)
                self.assertIs(self.environment, build_projects.registered_environment(self.prepared))
            self.assertNotIn(str(self.root), build_projects._ENVIRONMENTS)
        self.assertFalse((self.base / "build-projects").exists())

    def test_interleaved_declarations_keep_order_and_mapping_refusals_precede_execution(self):
        finite_project = self.root / "finite/Consumer.csproj"
        finite_project.parent.mkdir()
        finite_project.write_text('<Project Sdk="Microsoft.NET.Sdk" />')
        commit(self.root)
        finite = {"id": "first", "project": "finite/Consumer.csproj", "versions": {"HexalithEventStoreVersion": "4.1.0"}}
        last = {"id": "last", "project": "finite/Consumer.csproj", "versions": {"HexalithEventStoreVersion": "4.2.0"}}
        self.prepared.document["buildProjects"] = [finite, self.case, last]
        flags = ["-p:Configuration=Release", "-p:UseHexalithProjectReferences=false"]
        events = []
        def evaluated(root, project, *, properties, flags, **kwargs):
            values = dict.fromkeys(properties, "")
            values.update(TargetFramework="net11.0", IsTestProject="false", XunitTestProject="false")
            values.update(argument[3:].split("=", 1) for argument in flags if argument.startswith("-p:"))
            return values
        def command(arguments, root):
            version = next(argument for argument in arguments if argument.startswith("-p:HexalithEventStoreVersion="))
            events.append((version.rsplit("=", 1)[1], arguments[1]))
        with patch.object(self.preparation, "verify_environment"), \
                patch.object(build_projects, "validate_project"), patch.object(build_projects, "verify_packages"), \
                patch("ci_pipeline.evaluated_properties", side_effect=evaluated), \
                patch.object(isolated_builds, "build", side_effect=lambda *_args: events.append(("isolated", "build"))) as isolated:
            with build_projects.output_scope(self.prepared, self.environment):
                build_projects.build_declared(self.prepared, flags, command)
                self.assertEqual([("4.1.0", "restore"), ("4.1.0", "build"), ("isolated", "build"),
                                  ("4.2.0", "restore"), ("4.2.0", "build")], events)
                isolated.reset_mock()
                events.clear()
                with patch.dict(os.environ, {build_projects.VARIABLE: "{}"}), self.assertRaisesRegex(ValueError, "mapping"):
                    build_projects.build_declared(self.prepared, flags, command)
                isolated.assert_not_called()
                self.assertEqual([], events)

    def test_qualification_scope_registers_isolated_only_with_explicit_verifier_and_cleans(self):
        self.environment.authority["kind"] = "candidate-qualification"
        with patch.object(self.preparation, "_verify_environment") as verify:
            with self.preparation.applied_qualification(self.prepared, self.environment.authority, self.environment):
                self.assertNotIn(build_projects.VARIABLE, os.environ)
                self.assertIs(self.environment, build_projects.registered_environment(self.prepared))
                self.assertTrue(verify.call_args.kwargs["qualification"])
            self.assertNotIn(str(self.root), build_projects._ENVIRONMENTS)
            self.assertNotIn(str(self.root), build_projects._QUALIFICATION_SCOPES)
        with self.assertRaisesRegex(ValueError, "lifetime"):
            build_projects.registered_environment(self.prepared)

    def test_existing_job_receipt_rehydrates_isolated_only_environment_without_version_exports(self):
        state = self.base / "state.json"
        state.write_text(json.dumps({"workspace": str(self.workspace), "values": self.environment.values,
            "authority": self.environment.authority, "inputs": {}, "files": {}}))
        self.addCleanup(build_projects._MAPPINGS.pop, str(self.root), None)
        self.addCleanup(build_projects._ENVIRONMENTS.pop, str(self.root), None)
        with patch.dict(os.environ, {self.preparation.STATE_VARIABLE: str(state)}), \
                patch.object(self.preparation, "verify_environment"):
            restored = self.preparation.ensure_for_job(self.prepared, repository="Example/Fixture")
            self.assertNotIn(build_projects.VARIABLE, os.environ)
            self.assertIs(restored, build_projects.registered_environment(self.prepared))

    def test_underscore_prefix_and_uncommitted_source_keep_the_existing_contract_boundary(self):
        self.value.update(propertyPrefix="Fixture_Boundary", properties={"Fixture_BoundaryMode": "package"})
        self.descriptor.write_text(json.dumps(self.value))
        commit(self.root)
        isolated_builds.declaration(self.prepared, self.case, self.environment)
        self.descriptor.write_text(json.dumps(dict(self.value, extra=True)))
        with patch("ci_pipeline.run_command") as command, self.assertRaises(ValueError):
            isolated_builds.preflight(self.prepared, self.case, self.environment)
        command.assert_not_called()


if __name__ == "__main__":
    unittest.main()
