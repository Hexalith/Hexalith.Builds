"""Guard ordering, committed action metadata, and sample-root behavior."""

from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import expected_state as state
import guard
from test_expected_state import BUILDS, REPOSITORY, LocalTransport, Repository, configuration


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.workspace = Repository(self.root / "workspace")
        self.delivered = Repository(self.root / "delivered")
        self.builds = Repository(self.root / "builds")
        self.builds.write("data", "trusted")
        self.builds.commit()
        self.config = configuration(hooks=["after-unit-tests", "nightly"], jobs={"windows-job": {"runner": "windows-latest"}})
        self.workspace.write(".github/hexalith-ci.json", json.dumps(self.config))
        self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", json.dumps(self.config))
        self.delivered.write("rendered/Fixture.perimeter.json", json.dumps({"schema": "hexalith.perimeter.v1",
            "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
        self.delivered.commit()
        self.delivered.command("branch", "delivered/Fixture")
        self.transport = LocalTransport(self.delivered, self.builds)
        self.outputs = {}
        self.workspace.commit()

    def run_guard(self, **changes):
        return guard.run(self.workspace.root, repository=REPOSITORY, transport=self.transport,
                         emit=self.outputs.update, **changes)

    def actions(self):
        for action in [*self.config["hooks"], *self.config["jobs"]]:
            self.workspace.write(f".github/actions/{action}/action.yml", "metadata read as data, never YAML-parsed")
        self.workspace.commit()

    def test_owned_malformed_or_unknown_schema_fails_before_ci_loader_and_emits_drift(self):
        for data in ("malformed JSON", '{"schema":"unknown"}'):
            self.workspace.write(".github/hexalith-ci.json", data)
            self.workspace.commit()
            with self.subTest(data=data), patch.object(state, "load_ci", side_effect=AssertionError("config parsed before comparison")):
                with self.assertRaisesRegex(state.ExpectedStateError, "Fix the product code"):
                    self.run_guard()
            self.assertEqual({"drift": "true"}, self.outputs)

    def test_missing_expected_state_never_parses_config_or_authorizes_restore(self):
        self.delivered.command("branch", "-D", "delivered/Fixture")
        with patch.object(state, "load_ci", side_effect=AssertionError("config parsed without expected state")):
            with self.assertRaises(state.ExpectedStateError):
                self.run_guard()
        self.assertEqual({"drift": "false"}, self.outputs)

    def test_matching_invalid_config_reports_the_same_logical_path_on_repeated_runs(self):
        for data, message in (("malformed JSON", "Expecting value"),
                              ('{"schema":"unknown"}', "unsupported schema")):
            self.workspace.write(".github/hexalith-ci.json", data)
            self.workspace.commit()
            self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", data)
            self.delivered.commit()
            self.delivered.command("branch", "-f", "delivered/Fixture", "HEAD")
            diagnostics = []
            for _ in range(2):
                with self.assertRaises(state.ExpectedStateError) as raised:
                    self.run_guard()
                diagnostics.append(str(raised.exception))
            with self.subTest(data=data):
                self.assertEqual(diagnostics[0], diagnostics[1])
                self.assertIn("guard: .github/hexalith-ci.json:", diagnostics[0])
                self.assertIn(message, diagnostics[0])
                self.assertNotIn("hexalith-guard-config-", diagnostics[0])
                self.assertEqual({"drift": "false"}, self.outputs)

    def test_every_enabled_hook_and_job_requires_regular_committed_root_metadata(self):
        with self.assertRaises(state.ExpectedStateError) as raised:
            self.run_guard()
        self.assertIn("after-unit-tests", str(raised.exception))
        self.assertIn("nightly", str(raised.exception))
        self.assertIn("windows-job", str(raised.exception))
        self.assertEqual({"drift": "false"}, self.outputs)
        self.actions()
        self.assertEqual((), self.run_guard().drift)
        metadata = self.workspace.root / ".github/actions/after-unit-tests/action.yml"
        metadata.unlink()
        metadata.symlink_to("../../nightly/action.yml")
        self.workspace.commit()
        with self.assertRaisesRegex(state.ExpectedStateError, "after-unit-tests"):
            self.run_guard()
        metadata.unlink()
        self.workspace.command("add", "--all")
        self.workspace.command("update-index", "--add", "--cacheinfo", "160000",
                               self.builds.command("rev-parse", "HEAD"), ".github/actions/after-unit-tests/action.yml")
        self.workspace.command("commit", "-qm", "test: metadata gitlink")
        with self.assertRaisesRegex(state.ExpectedStateError, "after-unit-tests"):
            self.run_guard()

    def test_action_worktree_changes_and_staging_do_not_change_head_verdict(self):
        self.actions()
        metadata = self.workspace.root / ".github/actions/after-unit-tests/action.yml"
        metadata.unlink()
        self.workspace.command("add", "--all")
        self.workspace.write(".github/hexalith-ci.json", "dirty config")
        index = (self.workspace.root / ".git/index").read_bytes()
        self.assertEqual((), self.run_guard().drift)
        self.assertEqual(index, (self.workspace.root / ".git/index").read_bytes())
        self.assertEqual("dirty config", (self.workspace.root / ".github/hexalith-ci.json").read_text())

    def sample(self):
        prefix = state.SAMPLE_ROOT
        self.workspace.write(prefix + "/.github/hexalith-ci.json", json.dumps(self.config))
        expected = prefix + "/expected-source"
        self.workspace.write(expected + "/rendered/Hexalith.Sample/.github/hexalith-ci.json", json.dumps(self.config))
        self.workspace.write(expected + "/rendered/Hexalith.Sample.perimeter.json", json.dumps({
            "schema": "hexalith.perimeter.v1", "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
        for action in [*self.config["hooks"], *self.config["jobs"]]:
            self.workspace.write(prefix + f"/.github/actions/{action}/action.yml", "subtree action is insufficient")
        self.workspace.commit()
        return {"module_root": prefix, "expected_source": expected}

    def test_sample_compares_committed_subtrees_and_resolves_actions_from_workspace_root(self):
        inputs = self.sample()
        with self.assertRaisesRegex(state.ExpectedStateError, "workspace-root"):
            guard.run(self.workspace.root, repository=BUILDS, emit=self.outputs.update, **inputs)
        self.actions()
        comparison = guard.run(self.workspace.root, repository=BUILDS, emit=self.outputs.update, **inputs)
        self.assertEqual((), comparison.drift)
        self.workspace.write(state.SAMPLE_ROOT + "/.github/hexalith-ci.json", "edited sample owned file")
        self.workspace.commit()
        with self.assertRaises(state.ExpectedStateError):
            guard.run(self.workspace.root, repository=BUILDS, emit=self.outputs.update, **inputs)
        self.assertEqual({"drift": "true"}, self.outputs)

    def test_sample_config_diagnostics_name_the_sample_logical_path(self):
        inputs = self.sample()
        prefix = state.SAMPLE_ROOT
        for relative in ("/.github/hexalith-ci.json", "/expected-source/rendered/Hexalith.Sample/.github/hexalith-ci.json"):
            self.workspace.write(prefix + relative, '{"schema":"unknown"}')
        self.workspace.commit()
        with self.assertRaises(state.ExpectedStateError) as raised:
            guard.run(self.workspace.root, repository=BUILDS, emit=self.outputs.update, **inputs)
        self.assertIn("guard: " + prefix + "/.github/hexalith-ci.json:", str(raised.exception))
        self.assertNotIn("hexalith-guard-config-", str(raised.exception))
        self.assertEqual({"drift": "false"}, self.outputs)

    def test_solution_none_has_the_same_guard_enforcement(self):
        config = configuration(solution="none")
        self.workspace.write(".github/hexalith-ci.json", json.dumps(config))
        self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", json.dumps(config))
        self.delivered.commit()
        self.delivered.command("branch", "-f", "delivered/Fixture", "HEAD")
        self.workspace.commit()
        self.assertEqual((), self.run_guard().drift)
        self.workspace.write(".github/hexalith-ci.json", "owned edit")
        self.workspace.commit()
        with self.assertRaises(state.ExpectedStateError):
            self.run_guard()
        self.assertEqual("true", self.outputs["drift"])


if __name__ == "__main__":
    unittest.main()
