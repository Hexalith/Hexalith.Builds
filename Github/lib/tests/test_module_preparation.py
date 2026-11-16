"""Control producer failures and verify private preparation lifetime invariants."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Github/lib"))
SPEC = importlib.util.spec_from_file_location("module_preparation", ROOT / "Github/lib/module_preparation.py")
PREP = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = PREP
SPEC.loader.exec_module(PREP)


class PreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="module-preparation-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.prepared = SimpleNamespace(root=self.root, document={"moduleManifest": "module.json"})
        self.authority = {"schema": "hexalith.managed-build.v1", "kind": "accepted-delivery", "producerPin": "6.1.0"}
        self.calls = []
        self.fault = ""
        self.inputs = mock.patch.object(PREP, "_inputs", return_value={"head": "frozen"})
        self.inputs.start()
        self.addCleanup(self.inputs.stop)
        environment = {key: value for key, value in os.environ.items() if key not in PREP.VALIDATOR.KEYS}
        self.environment = mock.patch.dict(os.environ, environment, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def producer(self, command, root):
        """A labeled process control writes producer-shaped test-only output."""
        self.calls.append(command)
        if command[1:3] == ["tool", "restore"]:
            if self.fault == "restore":
                raise PREP.ConfigError("controlled restore refusal")
            return ""
        if command[-1] == "--version":
            return "0.0.0" if self.fault == "version" else "6.1.0"
        workspace = Path(command[command.index("--workspace") + 1])
        (workspace / "source-mapping").mkdir(parents=True)
        values = {key: str(workspace / "source-mapping" / name) for key, name in PREP.VALIDATOR.IMPORTS.items()}
        for path in values.values():
            Path(path).write_text("<Project />")
        values.update(HEXALITH_SOURCE_MAPPING_HASH="1" * 64, Configuration="Release")
        (workspace / "source-mapping/mapping.json").write_text(json.dumps({"Mode": "package", "ContentHash": "1" * 64, "HasGit": True}))
        result = {"status": "prepared", "outcome": {"exitCode": "Success"}, "diagnostics": [], "buildPreparation": {
            "mode": "package", "workspace": str(workspace), "mappingHash": "1" * 64, "environment": values}}
        if self.fault == "source":
            result["buildPreparation"]["mode"] = "source"
        elif self.fault == "runtime":
            result["runId"] = "forbidden-runtime-id"
        elif self.fault in ("remainingResources", "environments", "attachment", "testResultPath"):
            result[self.fault] = []
        elif self.fault == "key":
            values["UNTRUSTED_KEY"] = "unexpected"
        elif self.fault == "configuration":
            values["Configuration"] = "Debug"
        elif self.fault == "escape":
            values["CustomBeforeDirectoryBuildProps"] = str(self.root / "outside.targets")
        elif self.fault == "hash":
            values["HEXALITH_SOURCE_MAPPING_HASH"] = "2" * 64
        elif self.fault == "missing":
            Path(values["AfterMicrosoftNETSdkTargets"]).unlink()
        return json.dumps(result)

    def prepare(self):
        with mock.patch.object(PREP, "_run", side_effect=self.producer):
            return PREP.prepare_environment(self.prepared, self.authority, self.root)

    def test_git_root_spelling_preserves_physical_identity_and_reparse_refusal(self):
        self.assertTrue(PREP._same_git_root(self.root, str(self.root / ".")))
        sibling = self.root / "other-root"
        sibling.mkdir()
        self.assertFalse(PREP._same_git_root(self.root, str(sibling)))
        if os.name != "nt":
            link = self.root / "root-link"
            link.symlink_to(sibling, target_is_directory=True)
            with self.assertRaisesRegex(PREP.ConfigError, "symbolic|reparse"):
                PREP._same_git_root(sibling, str(link))

    def test_disabled_returns_empty_without_sdk_tool_or_network_work(self):
        self.prepared.document = {"moduleManifest": None}
        with mock.patch.object(PREP, "_run") as command, mock.patch.object(PREP, "_git") as git:
            environment = PREP.prepare_environment(self.prepared, {}, self.root)
        self.assertEqual({}, environment.values)
        self.assertIsNone(environment.workspace)
        command.assert_not_called()
        git.assert_not_called()

    def test_unsafe_scratch_spelling_refuses_before_output_creation_or_tool_execution(self):
        nested = self.root / "nested"
        nested.mkdir()
        controlled = self.root / "control\nroot"
        if os.name != "nt":
            controlled.mkdir()
        original_names = {item.name for item in self.root.iterdir()}
        for temporary in (nested / "..", controlled):
            for fetch_tags in (False, True):
                with self.subTest(temporary=temporary, fetch_tags=fetch_tags), \
                        mock.patch.object(PREP, "_run") as command, \
                        mock.patch.object(PREP.tempfile, "mkdtemp") as create:
                    with self.assertRaisesRegex(PREP.ConfigError, "traversal|control"):
                        PREP.prepare_environment(self.prepared, self.authority, temporary, fetch_tags=fetch_tags)
                    command.assert_not_called()
                    create.assert_not_called()
        self.assertEqual(original_names, {item.name for item in self.root.iterdir()})

    def test_one_exact_tool_restore_supplies_all_seven_values(self):
        environment = self.prepare()
        self.assertEqual(3, len(self.calls))
        self.assertIn("--configfile", self.calls[0])
        self.assertIn(str(PREP.NUGET_CONFIG), self.calls[0])
        self.assertIn("prepare-managed-build", self.calls[2])
        self.assertNotIn("prepare-build", self.calls[2])
        self.assertEqual(PREP.VALIDATOR.KEYS, environment.values.keys())
        self.assertTrue(environment.files)
        PREP.verify_environment(self.prepared, self.authority, environment)

    def test_every_producer_failure_exports_nothing_and_cleans_scratch(self):
        for fault in ("restore", "version", "source", "runtime", "key", "configuration", "escape", "hash", "missing"):
            self.fault = fault
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                self.prepare()
            self.assertFalse(list(self.root.glob("hexalith-module-preparation-*")))
            self.assertFalse(any(key in os.environ for key in PREP.VALIDATOR.KEYS))

    def test_runtime_state_without_run_identity_is_refused_before_export(self):
        for field in ("remainingResources", "environments", "attachment", "testResultPath"):
            self.fault = field
            with self.subTest(field=field), self.assertRaisesRegex(PREP.ConfigError, "runtime state"):
                self.prepare()
            self.assertFalse(list(self.root.glob("hexalith-module-preparation-*")))
            self.assertFalse(any(key in os.environ for key in PREP.VALIDATOR.KEYS))

    def test_changed_import_and_changed_input_refuse_reuse(self):
        environment = self.prepare()
        Path(environment.values["AfterMicrosoftNETSdkTargets"]).write_text("<Project changed='true' />")
        with self.assertRaisesRegex(PREP.ConfigError, "changed"):
            PREP.verify_environment(self.prepared, self.authority, environment)
        self.fault = ""
        environment = self.prepare()
        with mock.patch.object(PREP, "_inputs", return_value={"head": "changed"}):
            with self.assertRaisesRegex(PREP.ConfigError, "changed"):
                PREP.verify_environment(self.prepared, self.authority, environment)

    def test_scope_restores_environment_after_product_failure(self):
        environment = self.prepare()
        with self.assertRaisesRegex(RuntimeError, "product failure"):
            with PREP.applied(self.prepared, self.authority, environment):
                self.assertEqual("Release", os.environ["Configuration"])
                raise RuntimeError("product failure")
        self.assertFalse(any(key in os.environ for key in PREP.VALIDATOR.KEYS))

    def test_ambient_imports_fail_before_tool_execution(self):
        with mock.patch.dict(os.environ, {"CustomAfterMicrosoftCommonProps": "ambient.props"}):
            with self.assertRaisesRegex(PREP.ConfigError, "ambient"), mock.patch.object(PREP, "_run") as command:
                PREP.prepare_environment(self.prepared, self.authority, self.root)
        command.assert_not_called()

    def test_independent_job_stages_restore_only_once_and_reject_conflicting_exports(self):
        channel = self.root / "github-env"
        with mock.patch.dict(os.environ, {"RUNNER_TEMP": str(self.root), "GITHUB_ENV": str(channel)}), \
                mock.patch.object(PREP, "accepted_authority", return_value=self.authority), \
                mock.patch.object(PREP, "_run", side_effect=self.producer):
            first = PREP.ensure_for_job(self.prepared, repository="Example/Fixture")
            state = Path(os.environ[PREP.STATE_VARIABLE])
            self.assertTrue(state.is_file())
            second = PREP.ensure_for_job(self.prepared, repository="Example/Fixture")
            self.assertEqual(first, second)
            self.assertEqual(3, len(self.calls))
            self.assertEqual(8, len(channel.read_text().splitlines()))
            with mock.patch.dict(os.environ, {"AfterMicrosoftNETSdkTargets": "changed.targets"}):
                with self.assertRaisesRegex(PREP.ConfigError, "conflict"):
                    PREP.ensure_for_job(self.prepared, repository="Example/Fixture")

    def test_ordinary_input_validation_rejects_qualification_before_any_sdk(self):
        self.inputs.stop()
        context = dict(self.authority, kind="candidate-qualification", moduleRoot=str(self.root), qualification={})
        with mock.patch.object(PREP, "_git", return_value="wrong-head"), mock.patch.object(PREP, "_run") as command:
            with self.assertRaisesRegex(PREP.ConfigError, "accepted-delivery"):
                PREP.prepare_environment(self.prepared, context, self.root)
        command.assert_not_called()


if __name__ == "__main__":
    unittest.main()
