from __future__ import annotations
import copy
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

MODULE_PATH = Path(__file__).resolve().parents[1] / "prepare_module_build.py"
spec = importlib.util.spec_from_file_location("prepare_module_build", MODULE_PATH)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class PreparationTests(unittest.TestCase):
    def result(self, workspace):
        mapping_dir = workspace / "source-mapping"
        mapping_dir.mkdir(parents=True)
        digest = "a" * 64
        environment = {key: str(mapping_dir / filename) for key, filename in helper.IMPORTS.items()}
        environment.update(Configuration="Release", HEXALITH_SOURCE_MAPPING_HASH=digest)
        for filename in helper.IMPORTS.values():
            (mapping_dir / filename).write_text("<Project />")
        (mapping_dir / "mapping.json").write_text(json.dumps(dict(ContentHash=digest, Mode=1, HasGit=True)))
        return dict(status="prepared", outcome=dict(exitCode="Success"), buildPreparation=dict(workspace=str(workspace), mode="package", mappingHash=digest, environment=environment))

    def test_verified_environment_and_unsafe_results(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            self.assertEqual(set(helper.verified_environment(result, workspace)), helper.KEYS)
            mutations = [
                lambda r: r.update(outcome=[]),
                lambda r: r.update(buildPreparation=[]),
                lambda r: r["buildPreparation"]["environment"].update(BASH_ENV="/tmp/attack"),
                lambda r: r["buildPreparation"]["environment"].update(Configuration="Debug"),
                lambda r: r["buildPreparation"].update(mode="source"),
                lambda r: r.update(runId="forbidden"),
                lambda r: r["buildPreparation"]["environment"].update(HEXALITH_SOURCE_MAPPING_HASH="b" * 64),
                lambda r: r["buildPreparation"]["environment"].update(CustomAfterMicrosoftCommonProps="/tmp/outside"),
                lambda r: r["buildPreparation"]["environment"].update(Configuration="Release\nEVIL=1"),
            ]
            for mutate in mutations:
                changed = copy.deepcopy(result)
                mutate(changed)
                with self.assertRaises(ValueError):
                    helper.verified_environment(changed, workspace)

    def test_exact_pin_package_command_and_export_without_shell_evaluation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".config").mkdir()
            (root / "module.json").write_text("{}")
            (root / ".config/dotnet-tools.json").write_text(json.dumps(dict(isRoot=True, tools={"hexalith.builds.module.cli": dict(version="9.8.7", rollForward=False, commands=["hexalith-module"])})))
            export = root / "github-env"
            calls = []
            def run(command, cwd):
                calls.append(command)
                self.assertEqual(cwd, root)
                if command[1:3] == ["tool", "restore"]:
                    return "restored"
                if command[-1] == "--version":
                    return "9.8.7+source"
                self.assertIn("prepare-build", command)
                self.assertEqual(command[command.index("--mode") + 1], "package")
                workspace = Path(command[command.index("--workspace") + 1])
                return json.dumps(self.result(workspace))
            with mock.patch.object(helper, "run", side_effect=run):
                helper.prepare("module.json", root, root, export)
            self.assertEqual(3, len(calls))
            self.assertEqual(helper.KEYS, {line.split("=", 1)[0] for line in export.read_text().splitlines()})

    def test_failed_tool_or_unqualified_pin_never_exports(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".config").mkdir()
            (root / "module.json").write_text("{}")
            manifest = root / ".config/dotnet-tools.json"
            export = root / "github-env"
            for version, roll_forward in [("[4.35.2,)", False), ("4.35.2", True), ("4.35.2", False)]:
                manifest.write_text(json.dumps(dict(isRoot=True, tools={"hexalith.builds.module.cli": dict(version=version, rollForward=roll_forward, commands=["hexalith-module"])})))
                with mock.patch.object(helper, "run", side_effect=ValueError("unpublished prepare-build")), self.assertRaises(ValueError):
                    helper.prepare("module.json", root, root, export)
                self.assertFalse(export.exists())

    def test_duplicate_keys_and_import_symlinks_refuse(self):
        with self.assertRaises(ValueError):
            helper.read_json('{"status":"prepared","status":"failed"}')
        if os.name == "nt":
            return
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            target = workspace / "source-mapping/SourceMapping.props"
            target.unlink()
            outside = Path(temporary) / "outside.props"
            outside.write_text("<Project />")
            target.symlink_to(outside)
            with self.assertRaises(ValueError):
                helper.verified_environment(result, workspace)

    def test_external_mapping_metadata_symlink_refuses(self):
        if os.name == "nt":
            return
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            metadata = workspace / "source-mapping/mapping.json"
            external = Path(temporary) / "external-mapping.json"
            metadata.replace(external)
            metadata.symlink_to(external)
            with self.assertRaisesRegex(ValueError, "symbolic-link traversal"):
                helper.verified_environment(result, workspace)

    def test_boolean_mapping_mode_refuses(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            metadata = workspace / "source-mapping/mapping.json"
            mapping = json.loads(metadata.read_text())
            mapping["Mode"] = True
            metadata.write_text(json.dumps(mapping))
            with self.assertRaisesRegex(ValueError, "persisted mapping differs"):
                helper.verified_environment(result, workspace)

    def test_normal_producer_validation_runs_behavioral_helper_tests(self):
        for name in ("ci.yml", "build-release.yml"):
            workflow = (MODULE_PATH.parents[2] / ".github/workflows" / name).read_text()
            self.assertIn("python3 -m unittest discover -s Github/prepare-module-build/tests -v", workflow)

    def test_all_workflow_jobs_opt_in_before_restore(self):
        workflow = (MODULE_PATH.parents[2] / ".github/workflows/domain-ci.yml").read_text()
        self.assertIn("      module-manifest:", workflow)
        for name in ("build-and-test", "aspire-tests", "performance-tests"):
            job = workflow.split("\n  " + name + ":", 1)[1]
            if name != "performance-tests":
                next_name = "aspire-tests" if name == "build-and-test" else "performance-tests"
                job = job.split("\n  " + next_name + ":", 1)[0]
            self.assertIn("inputs.module-manifest != ''", job)
            self.assertIn("inputs.module-manifest != '' && !inputs.governed-ci", job)
            self.assertLess(job.index("name: Prepare module build"), job.index("name: Restore"))
            self.assertIn("./.hexalith/builds-execution/Github/prepare-module-build", job)
            self.assertIn("ref: ${{ job.workflow_sha }}", job)


if __name__ == "__main__":
    unittest.main()
