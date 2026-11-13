"""Real pinned parser/commitlint policy plus isolated failure/injection controls."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import expected_state as state
import guard_tools
import guard
from test_expected_state import Repository, configuration, BUILDS


class ToolControlsTests(unittest.TestCase):
    def test_manifest_preparation_failure_does_not_expose_private_paths(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(guard_tools.shutil, "copyfile",
                side_effect=OSError("/private/path\x1b secret")):
            with self.assertRaisesRegex(state.ExpectedStateError, "tool preparation failed") as raised:
                guard_tools.Tools(directory)
            self.assertNotIn("secret", str(raised.exception))
            self.assertNotIn(directory, str(raised.exception))

    def test_environment_excludes_module_node_npm_git_and_arbitrary_tokens(self):
        poison = {"NODE_OPTIONS": "--require=/module/evil.js", "NODE_PATH": "/module", "NPM_CONFIG_REGISTRY": "evil",
                  "GIT_CONFIG_COUNT": "2", "GITHUB_TOKEN": "secret", "UNUSUAL_TOKEN": "secret", "COREPACK_HOME": "/module"}
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, poison):
            environment = guard_tools.tool_environment(Path(directory))
            for key in poison:
                if key == "NPM_CONFIG_REGISTRY":
                    self.assertEqual("https://registry.npmjs.org/", environment[key])
                else:
                    self.assertNotIn(key, environment)
            self.assertEqual("", Path(environment["NPM_CONFIG_USERCONFIG"]).read_text())

    def test_install_uses_only_trusted_manifest_and_signatures_fail_closed(self):
        calls = []
        def run(arguments, **kwargs):
            calls.append((arguments, kwargs))
            return subprocess.CompletedProcess(arguments, int(arguments[1] == "audit"), "", "\x1b[31mprivate secret")
        with tempfile.TemporaryDirectory() as directory, patch.object(guard_tools.subprocess, "run", side_effect=run):
            with self.assertRaisesRegex(state.ExpectedStateError, "signature verification") as raised:
                guard_tools.Tools(directory)
            self.assertNotIn("private secret", str(raised.exception))
            self.assertEqual([["npm", "ci", "--ignore-scripts"], ["npm", "audit", "signatures"]], [c[0] for c in calls])
            self.assertEqual((guard_tools.SOURCE / "package-lock.json").read_bytes(), (Path(directory) / "package-lock.json").read_bytes())
            self.assertTrue(all(c[1]["cwd"] == Path(directory) and c[1]["timeout"] for c in calls))

    def test_tool_timeouts_and_malicious_output_are_safely_bounded(self):
        tool = object.__new__(guard_tools.Tools)
        with tempfile.TemporaryDirectory() as directory:
            tool.directory, tool.environment = Path(directory), {}
            with patch.object(guard_tools.subprocess, "run", side_effect=subprocess.TimeoutExpired("node", 1)):
                with self.assertRaisesRegex(state.ExpectedStateError, "trusted guard tool failed"):
                    tool.parse_yaml("runs: {}")
            with patch.object(guard_tools.subprocess, "run", return_value=subprocess.CompletedProcess([], 1,
                    "\x1b[31m::error::evil [no-skip-ci]", "/private/path secret")):
                with self.assertRaises(state.ExpectedStateError) as raised:
                    tool.lint_message("fix: unsafe\n", "commit \x1b\n::error::identity")
                self.assertIn("no-skip-ci", str(raised.exception))
                self.assertNotIn("secret", str(raised.exception))
                self.assertNotIn("\x1b", str(raised.exception))
                self.assertNotIn("\n::error", str(raised.exception))


class RealPinnedToolsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="hexalith-real-guard-test-")
        cls.addClassCleanup(cls.temporary.cleanup)
        with patch.dict(os.environ, {"NODE_OPTIONS": "--require=/module/evil.js",
                "NODE_PATH": "/module", "NPM_CONFIG_USERCONFIG": "/module/.npmrc",
                "NPM_CONFIG_REGISTRY": "https://invalid.example", "ARBITRARY_TOKEN": "private"}):
            cls.tools = guard_tools.Tools(cls.temporary.name)

    def test_real_locked_versions_and_yaml_syntax(self):
        lock = json.loads((Path(self.temporary.name) / "package-lock.json").read_text())
        for package, version in (("@commitlint/cli", "21.2.4"), ("@commitlint/config-conventional", "21.2.4"), ("js-yaml", "4.3.2")):
            self.assertEqual(version, lock["packages"]["node_modules/" + package]["version"])
        for text in ('runs: {using: composite, steps: [{run: "echo yes", shell: bash}]}',
                     '{"runs":{"using":"composite","steps":[]}}',
                     'step: &step {run: echo yes, shell: bash}\nruns: {using: composite, steps: [*step]}'):
            self.assertIsInstance(self.tools.parse_yaml(text)["runs"], dict)
        for text in ('runs: x\nruns: y', 'x: !!js/function >\n  evil', '---\nx: 1\n---\ny: 2'):
            with self.assertRaises(state.ExpectedStateError):
                self.tools.parse_yaml(text)

    def test_yaml_alias_expansion_fails_closed_before_unbounded_python_capture(self):
        text = "a0: &a0 [x,x,x,x,x]\n"
        for number in range(1, 8):
            text += f"a{number}: &a{number} [" + ",".join([f"*a{number - 1}"] * 5) + "]\n"
        with self.assertRaisesRegex(state.ExpectedStateError, "parser output exceeds size limit"):
            self.tools.parse_yaml(text)

    def test_real_effective_conventional_rules_and_length_boundaries(self):
        good = [f"{kind}: subject\n" for kind in ("build", "ci", "docs", "feat", "fix", "perf", "refactor", "revert", "style", "test")]
        good += ["fix: " + "x" * 195, "fix: subject\n\n" + "x" * 200,
                 "fix: subject\n\nRefs: " + "x" * 94,
                 "fix: subject\n\nhttps://example.invalid/" + "x" * 220]
        for message in good:
            with self.subTest(message=message):
                self.tools.lint_message(message, "valid fixture")
        bad = [("chore: subject", "type-enum"), ("FIX: subject", "type-case"),
               ("fix: Subject", "subject-case"), ("fix: subject.", "subject-full-stop"),
               ("fix:", "subject-empty"), ("subject", "type-empty"),
               ("fix: " + "x" * 196, "header-max-length"),
               ("fix: subject\n\n" + "x" * 201, "body-max-line-length"),
               ("fix: subject\n\nRefs: " + "x" * 95, "footer-max-line-length"),
               ("Merge branch topic", "type-empty")]
        for message, rule in bad:
            with self.subTest(rule=rule), self.assertRaisesRegex(state.ExpectedStateError, rule):
                self.tools.lint_message(message, "invalid fixture")

    def test_module_npm_and_commitlint_code_cannot_execute(self):
        with tempfile.TemporaryDirectory(prefix="hexalith-poison-module-") as module:
            module = Path(module)
            marker = module / "executed"
            script = f"require('node:fs').writeFileSync({json.dumps(str(marker))}, 'unsafe')"
            (module / "package.json").write_text(json.dumps({"scripts": {"preinstall": "node evil.cjs"},
                "commitlint": {"plugins": ["./evil.cjs"]}}))
            (module / "evil.cjs").write_text(script)
            (module / "commitlint.config.cjs").write_text(script)
            (module / ".npmrc").write_text("registry=https://invalid.example\nignore-scripts=false\n")
            previous = Path.cwd()
            try:
                os.chdir(module)
                self.tools.lint_message("fix: trusted policy\n", "poison module control")
                self.tools.parse_yaml("runs: {using: composite, steps: []}")
            finally:
                os.chdir(previous)
            self.assertFalse(marker.exists())

    def test_real_guard_push_pr_and_selected_merge_enforce_the_actual_cli_policy(self):
        def fixture(directory):
            repository = Repository(Path(directory) / "workspace")
            prefix = state.SAMPLE_ROOT
            data = json.dumps(configuration(solution="none"))
            repository.write(prefix + "/.github/hexalith-ci.json", data)
            repository.write(prefix + "/expected-source/rendered/Hexalith.Sample/.github/hexalith-ci.json", data)
            repository.write(prefix + "/expected-source/rendered/Hexalith.Sample.perimeter.json", json.dumps({
                "schema": "hexalith.perimeter.v1", "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
            repository.write(".github/actions/dormant/action.yml",
                "runs: {using: composite, steps: [{run: echo safe, shell: bash}]}\n")
            return repository, repository.commit()

        def commit(repository, message):
            repository.command("commit", "--allow-empty", "-qm", message)
            return repository.command("rev-parse", "HEAD")

        def run(repository, name, event):
            outputs = {}
            index = (repository.root / ".git/index").read_bytes()
            try:
                return guard.run(repository.root, repository=BUILDS, module_root=state.SAMPLE_ROOT,
                    expected_source=state.SAMPLE_ROOT + "/expected-source", event_name=name, event=event,
                    emit=outputs.update, tool_factory=lambda _directory: self.tools)
            finally:
                self.assertEqual({"drift": "false"}, outputs)
                self.assertEqual(index, (repository.root / ".git/index").read_bytes())

        with tempfile.TemporaryDirectory() as directory:
            repository, base = fixture(directory)
            first = commit(repository, "fix: earlier event change\n\ncomplete body\n")
            head = commit(repository, "feat: later event change\n")
            run(repository, "push", {"before": base, "after": head})
            repository.command("switch", "-qc", "unrelated", base)
            commit(repository, "chore: unrelated creation-push history")
            repository.command("switch", "main")
            run(repository, "push", {"before": "0" * 40, "after": head})
            repository.command("switch", "-qc", "target", base)
            commit(repository, "fix: target advance")
            repository.command("merge", "--no-ff", head, "-m", "Merge pull request #123 [skip ci]")
            synthetic = repository.command("rev-parse", "HEAD")
            event = {"pull_request": {"base": {"sha": base}, "head": {"sha": head}, "title": "fix: pull request title"}}
            run(repository, "pull_request", event)  # The forbidden checkout message is outside event range.
            event["pull_request"]["title"] = "fix: pull request title [skip actions]"
            with self.assertRaisesRegex(state.ExpectedStateError, "pull request title: commitlint no-skip-ci"):
                run(repository, "pull_request", event)
            with self.assertRaisesRegex(state.ExpectedStateError, "commit " + synthetic + ":.*no-skip-ci"):
                run(repository, "push", {"before": head, "after": synthetic})
        with tempfile.TemporaryDirectory() as directory:
            repository, base = fixture(directory)
            earlier = commit(repository, "chore: forbidden earlier type")
            head = commit(repository, "fix: valid latest commit")
            with self.assertRaisesRegex(state.ExpectedStateError, "commit " + earlier + ": commitlint type-enum"):
                run(repository, "push", {"before": base, "after": head})
            with self.assertRaisesRegex(state.ExpectedStateError, "commit " + earlier + ": commitlint type-enum"):
                run(repository, "push", {"before": "0" * 40, "after": head})

    def test_real_skip_rule_uses_raw_message_and_root_uses_same_policy(self):
        for marker in ("[skip ci]", "[ci skip]", "[no ci]", "[skip actions]", "[actions skip]", "skip-checks: false"):
            for message in ("fix: subject\n\n" + marker, "Merge branch topic\n\n" + marker):
                with self.subTest(marker=marker), self.assertRaisesRegex(state.ExpectedStateError, "no-skip-ci"):
                    self.tools.lint_message(message, "commit fixture")
        root = guard_tools.SOURCE.parents[1]
        result = subprocess.run(["node", str(root / "node_modules/@commitlint/cli/cli.js"),
            "--config", str(root / "commitlint.config.mjs"), "--color=false"], cwd=root,
            input="fix: subject\n\nskip-checks: false\n", text=True, capture_output=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("no-skip-ci", result.stdout)
