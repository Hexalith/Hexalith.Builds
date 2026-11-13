"""Execute guard composites against committed trees and anonymous local transports."""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

import yaml

from test_ci_environment import CompositeHarness

ROOT = Path(__file__).resolve().parents[3]
ACTION = ROOT / "Github/guard"
REAL_GIT = shutil.which("git")
OWNERSHIP = "CI/CD is owned by Hexalith.CiCd. Fix the product code, not the pipeline."
SAMPLE = "Samples/Hexalith.Sample"
EXPECTED = SAMPLE + "/expected-source"


def config(*, solution="none", actions=False):
    return {"schema": "hexalith.ci.v1", "buildsTag": "v1.0.0", "solution": solution,
            "dapr": False, "browsers": False, "registry": False, "codeql": [],
            "tests": {"integration": [], "exclude": {}}, "hooks": ["after-unit-tests"] if actions else [],
            "jobs": {"sample-windows": {"runner": "windows-latest"}} if actions else {}, "submodules": []}


class GuardAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="guard-adapter-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.workspace = self.root / "workspace"
        self.delivered = self.root / "delivered"
        self.builds = self.root / "builds"
        self.bin = self.root / "bin"
        self.ledger = self.root / "git-ledger"
        self.bin.mkdir()
        for directory, branch in ((self.workspace, "main"), (self.delivered, "delivered/Module"),
                                  (self.builds, "main")):
            directory.mkdir()
            self.git(directory, "init", "-b", branch)
            self.git(directory, "config", "user.name", "Guard Fixture")
            self.git(directory, "config", "user.email", "guard@example.invalid")
        self.document = config()
        self.write(self.workspace, ".github/hexalith-ci.json", json.dumps(self.document))
        self.commit(self.workspace)
        self.accept()
        self.write(self.builds, "trusted", "Builds tag object\n")
        self.builds_sha = self.commit(self.builds)
        self.git(self.builds, "tag", "-a", "v1.0.0", "-m", "trusted annotated tag")
        shim = self.bin / "git"
        shim.write_text(f"#!{sys.executable}\n" + f'''
import json, os, subprocess, sys
args=sys.argv[1:]
assert not any(k in os.environ for k in ('GITHUB_TOKEN','GH_TOKEN','GITHUB_SHA','GIT_CONFIG_COUNT',
    'GIT_CONFIG_PARAMETERS','GIT_CONFIG_KEY_0','GIT_CONFIG_VALUE_0','GIT_DIR','GIT_WORK_TREE'))
assert not any('secret-marker' in value for value in args)
with open({str(self.ledger)!r}, 'a') as stream: stream.write(json.dumps(args)+'\\n')
for i, value in enumerate(args):
    if value=='https://github.com/Hexalith/Hexalith.CiCd.git': args[i]={self.delivered.as_uri()!r}
    elif value=='https://github.com/Hexalith/Hexalith.Builds.git': args[i]={self.builds.as_uri()!r}
    elif value=='protocol.file.allow=never': args[i]='protocol.file.allow=always'
raise SystemExit(subprocess.call([{REAL_GIT!r},*args]))
''')
        shim.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + str(Path(sys.executable).parent)
                                + os.pathsep + os.environ.get("PATH", ""),
            GITHUB_WORKSPACE=str(self.workspace), GITHUB_ACTION_PATH=str(ACTION),
            GITHUB_TOKEN="secret-marker", GH_TOKEN="secret-marker", GITHUB_SHA="secret-marker",
            CI_REPOSITORY="Example/Module", CI_MODULE_ROOT="", CI_EXPECTED_SOURCE="",
            GIT_CONFIG_COUNT="1", GIT_CONFIG_KEY_0="url.ext::poison.insteadOf",
            GIT_CONFIG_VALUE_0="https://github.com/")

    def git(self, directory, *arguments):
        environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
        result = subprocess.run([REAL_GIT, "-c", "core.hooksPath=" + os.devnull, "-C", str(directory), *arguments],
                                env=environment, capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def write(self, directory, path, data):
        target = directory / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(data)

    def commit(self, directory):
        self.git(directory, "add", ".")
        self.git(directory, "commit", "-m", "test: disposable guard adapter fixture")
        return self.git(directory, "rev-parse", "HEAD")

    def accept(self, *, manifest=None):
        self.write(self.delivered, "rendered/Module/.github/hexalith-ci.json", json.dumps(self.document))
        self.write(self.delivered, "rendered/Module.perimeter.json", json.dumps(manifest or {
            "schema": "hexalith.perimeter.v1", "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
        return self.commit(self.delivered)

    def invoke(self, *, repository="Example/Module", module_root="", expected_source="", action=ACTION, ref=None,
               event_name="workflow_dispatch", event=None):
        successor = self.workspace / "successor-ran"
        successor.unlink(missing_ok=True)
        environment = dict(self.environment, GITHUB_ACTION_PATH=str(action), CI_REPOSITORY=repository,
                           CI_MODULE_ROOT=module_root, CI_EXPECTED_SOURCE=expected_source)
        harness = CompositeHarness(self.workspace, environment, repository=repository, event_name=event_name, event=event)
        if ref is None:
            result = harness.run_action(action)
            values = harness.action_outputs
        else:
            # Execute the explicit CLI seam independently of composite routing.
            output = self.root / "github-output"
            output.write_text("")
            environment.update(GITHUB_OUTPUT=str(output), PATH=harness.path,
                               GITHUB_EVENT_NAME=event_name, GITHUB_EVENT_PATH=harness.environment["GITHUB_EVENT_PATH"])
            result = subprocess.run([sys.executable, "-I", str(ROOT / "Github/lib/guard.py"), "--ref", ref],
                                    cwd=self.workspace, env=environment, capture_output=True, text=True)
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
        if result.returncode == 0:
            subprocess.run(["bash", "-e", "-c", "touch successor-ran"], cwd=self.workspace, check=True)
        self.last_harness = harness
        self.assertNotIn("secret-marker", result.stdout + result.stderr)
        self.assertEqual(result.returncode == 0, successor.exists())
        return result, values

    def tools(self):
        ledger = self.workspace / "guard-tools.jsonl"
        return [json.loads(line) for line in ledger.read_text().splitlines()] if ledger.exists() else []

    def commands(self):
        return [json.loads(line) for line in self.ledger.read_text().splitlines()] if self.ledger.exists() else []

    def sample(self, *, root_actions=True):
        document = config(solution="Fixture.slnx", actions=True)
        data = json.dumps(document)
        self.write(self.workspace, SAMPLE + "/.github/hexalith-ci.json", data)
        self.write(self.workspace, EXPECTED + "/rendered/Hexalith.Sample/.github/hexalith-ci.json", data)
        self.write(self.workspace, EXPECTED + "/rendered/Hexalith.Sample.perimeter.json", json.dumps({
            "schema": "hexalith.perimeter.v1", "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
        for name in ("after-unit-tests", "sample-windows"):
            prefix = "" if root_actions else SAMPLE + "/"
            self.write(self.workspace, prefix + ".github/actions/" + name + "/action.yml",
                       "name: fixture\ndescription: fixture\nruns:\n  using: composite\n"
                       "  steps:\n    - run: echo fixture\n      shell: bash\n")
        self.commit(self.workspace)

    def test_matching_none_module_executes_successor_through_anonymous_branch_clone(self):
        result, values = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"drift": "false"}, values)
        commands = self.commands()
        clone = next(command for command in commands if "clone" in command)
        self.assertIn("https://github.com/Hexalith/Hexalith.CiCd.git", clone)
        self.assertIn("delivered/Module", clone)
        self.assertTrue(any("--depth" in arg for arg in clone))

    def test_malformed_owned_config_is_drift_before_parser_and_stops_successor(self):
        self.write(self.workspace, ".github/hexalith-ci.json", '{"schema":"hexalith.ci.v999"}')
        self.commit(self.workspace)
        result, values = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertEqual({"drift": "true"}, values)
        self.assertEqual([], self.tools())
        self.assertIn(OWNERSHIP, result.stderr + result.stdout)
        self.assertIn(".github/hexalith-ci.json", result.stderr + result.stdout)
        self.assertNotIn("unsupported schema", result.stderr + result.stdout)

    def test_missing_delivered_branch_or_manifest_never_authorizes_repair(self):
        self.git(self.delivered, "branch", "-m", "unaccepted")
        result, values = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertEqual({"drift": "false"}, values)
        self.assertIn(OWNERSHIP, result.stderr + result.stdout)
        self.git(self.delivered, "branch", "-m", "delivered/Module")
        (self.delivered / "rendered/Module.perimeter.json").unlink()
        self.commit(self.delivered)
        result, values = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertEqual({"drift": "false"}, values)
        self.assertIn(OWNERSHIP, result.stderr + result.stdout)

    def test_builds_root_does_no_expected_acquisition_and_sample_reads_root_actions(self):
        result, values = self.invoke(repository="Hexalith/Hexalith.Builds")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any("clone" in command or "fetch" in command for command in self.commands()))
        self.assertEqual([], self.tools())
        self.assertFalse(any(reference.startswith("actions/setup-node@")
                             for reference, _, _ in self.last_harness.external_calls))
        self.sample()
        result, values = self.invoke(repository="Hexalith/Hexalith.Builds", module_root=SAMPLE, expected_source=EXPECTED)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"drift": "false"}, values)
        self.assertFalse(any("clone" in command or "fetch" in command for command in self.commands()))
        self.assertEqual([["ci", "--ignore-scripts"], ["audit", "signatures"]],
                         [call["args"] for call in self.tools() if call["tool"] == "npm"])
        self.assertEqual(1, sum(reference.startswith("actions/setup-node@")
                                for reference, _, _ in self.last_harness.external_calls))

    def test_sample_nested_actions_do_not_satisfy_workspace_root_availability(self):
        self.sample(root_actions=False)
        result, values = self.invoke(repository="Hexalith/Hexalith.Builds", module_root=SAMPLE, expected_source=EXPECTED)
        self.assertEqual(1, result.returncode)
        self.assertEqual({"drift": "false"}, values)
        self.assertIn("after-unit-tests", result.stderr + result.stdout)
        self.assertIn(OWNERSHIP, result.stderr + result.stdout)

    def test_symlink_and_gitlink_action_metadata_are_unavailable_without_execution(self):
        self.sample()
        hook = self.workspace / ".github/actions/after-unit-tests/action.yml"
        hook.unlink()
        hook.symlink_to("../../../.github/hexalith-ci.json")
        job = self.workspace / ".github/actions/sample-windows/action.yml"
        job.unlink()
        self.git(self.workspace, "add", ".github/actions")
        self.git(self.workspace, "update-index", "--add", "--cacheinfo",
                 "160000," + self.builds_sha + ",.github/actions/sample-windows/action.yml")
        self.git(self.workspace, "commit", "-m", "test: nonregular enabled action metadata")
        result, values = self.invoke(repository="Hexalith/Hexalith.Builds", module_root=SAMPLE, expected_source=EXPECTED)
        self.assertEqual(1, result.returncode)
        self.assertEqual({"drift": "false"}, values)
        self.assertIn("after-unit-tests", result.stderr + result.stdout)
        self.assertIn("sample-windows", result.stderr + result.stdout)
        self.assertIn(OWNERSHIP, result.stderr + result.stdout)

    def test_fixture_controls_fail_before_lookup_even_on_builds_root(self):
        for repository, module_root, expected_source in (("Example/Module", SAMPLE, EXPECTED),
            ("Hexalith/Hexalith.Builds", SAMPLE, ""), ("Hexalith/Hexalith.Builds", "", EXPECTED),
            ("Hexalith/Hexalith.Builds", "../escape", EXPECTED),
            ("Hexalith/Hexalith.Builds", SAMPLE, "../escape")):
            with self.subTest(repository=repository, module_root=module_root, expected_source=expected_source):
                self.ledger.unlink(missing_ok=True)
                result, values = self.invoke(repository=repository, module_root=module_root, expected_source=expected_source)
                self.assertEqual(1, result.returncode)
                self.assertEqual({"drift": "false"}, values)
                self.assertFalse(any("clone" in command or "fetch" in command for command in self.commands()))

    def test_head_verdict_ignores_staged_dirty_and_omitted_worktree_files(self):
        original = (self.workspace / ".github/hexalith-ci.json").read_bytes()
        self.write(self.workspace, ".github/hexalith-ci.json", "staged malformed config")
        self.git(self.workspace, "add", ".github/hexalith-ci.json")
        (self.workspace / ".github/hexalith-ci.json").unlink()
        index = (self.workspace / ".git/index").read_bytes()
        result, values = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"drift": "false"}, values)
        self.assertEqual(index, (self.workspace / ".git/index").read_bytes())
        self.assertFalse((self.workspace / ".github/hexalith-ci.json").exists())
        self.assertEqual(original.decode(), self.git(self.workspace, "show", "HEAD:.github/hexalith-ci.json"))

    def test_cli_ref_index_is_rejected_for_modules_and_builds_root(self):
        for repository in ("Example/Module", "Hexalith/Hexalith.Builds"):
            with self.subTest(repository=repository):
                self.ledger.unlink(missing_ok=True)
                result, values = self.invoke(repository=repository, ref="index")
                self.assertEqual(1, result.returncode)
                self.assertEqual({"drift": "false"}, values)
                self.assertIn("committed Git tree", result.stderr)
                self.assertIn(OWNERSHIP, result.stderr)
                self.assertFalse(any("clone" in command or "fetch" in command for command in self.commands()))

    def test_downloaded_action_without_git_acquires_trusted_tag_objects_separately(self):
        self.git(self.workspace, "update-index", "--add", "--cacheinfo", "160000," + self.builds_sha + ",references/Runtime")
        self.git(self.workspace, "commit", "-m", "test: add committed runtime gitlink")
        self.accept(manifest={"schema": "hexalith.perimeter.v1", "entries": [
            {"kind": "whole", "path": ".github/hexalith-ci.json"},
            {"kind": "gitlink", "path": "references/Runtime", "buildsTag": "v1.0.0"}]})
        trusted = self.root / "downloaded-action"
        shutil.copytree(ACTION, trusted / "Github/guard")
        shutil.copytree(ROOT / "Github/lib", trusted / "Github/lib", ignore=shutil.ignore_patterns("tests", "__pycache__"))
        shutil.copytree(ROOT / "schemas", trusted / "schemas")
        result, values = self.invoke(action=trusted / "Github/guard")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"drift": "false"}, values)
        self.assertFalse((trusted / ".git").exists())
        self.assertTrue(any("https://github.com/Hexalith/Hexalith.Builds.git" in command for command in self.commands()))

    def test_push_event_lints_whole_builds_range_through_sample_with_trusted_tools_once(self):
        self.sample()
        before = self.git(self.workspace, "rev-parse", "HEAD")
        for number in (1, 2):
            self.write(self.workspace, "outside-sample", str(number))
            self.git(self.workspace, "add", "outside-sample")
            self.git(self.workspace, "commit", "-m", f"fix: repository-wide event commit {number}")
        after = self.git(self.workspace, "rev-parse", "HEAD")
        result, values = self.invoke(repository="Hexalith/Hexalith.Builds", module_root=SAMPLE,
                                     expected_source=EXPECTED, event_name="push",
                                     event={"before": before, "after": after})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("false", values["drift"])
        selected = [call["stdin"].strip() for call in self.tools()
                    if call["tool"] == "node" and call["args"][0].endswith("cli.js")]
        self.assertEqual(["fix: repository-wide event commit 1", "fix: repository-wide event commit 2"], selected)
        self.assertEqual(2, sum(call["tool"] == "npm" for call in self.tools()))

    def test_pr_event_uses_base_head_and_title_instead_of_synthetic_merge_checkout(self):
        before = self.git(self.workspace, "rev-parse", "HEAD")
        self.write(self.workspace, "product", "head change")
        self.git(self.workspace, "add", "product")
        self.git(self.workspace, "commit", "-m", "fix: pull request head")
        head = self.git(self.workspace, "rev-parse", "HEAD")
        merge = self.git(self.workspace, "commit-tree", "HEAD^{tree}", "-p", before, "-p", head,
                         "-m", "synthetic merge must not be selected")
        self.git(self.workspace, "update-ref", "refs/heads/main", merge)
        result, values = self.invoke(event_name="pull_request", event={"pull_request": {
            "base": {"sha": before}, "head": {"sha": head}, "title": "fix: pull request title"}})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("false", values["drift"])
        selected = [call["stdin"].strip() for call in self.tools()
                    if call["tool"] == "node" and call["args"][0].endswith("cli.js")]
        self.assertEqual(["fix: pull request head", "fix: pull request title"], selected)
        self.assertNotIn("synthetic merge must not be selected", selected)

    def test_module_npm_commitlint_and_node_configuration_cannot_steer_guard(self):
        self.write(self.workspace, "package.json", json.dumps({"scripts": {
            "preinstall": "touch module-code-ran"}, "commitlint": {"plugins": ["poison-module-plugin"]}}))
        self.write(self.workspace, "commitlint.config.mjs", "throw Error('poison-marker');")
        self.write(self.workspace, ".npmrc", "registry=https://poison-marker.invalid/\n")
        self.commit(self.workspace)
        self.environment.update(NODE_OPTIONS="--require=poison-marker", NODE_PATH="poison-marker",
                                NPM_CONFIG_USERCONFIG=str(self.workspace / ".npmrc"),
                                npm_config_registry="https://poison-marker.invalid/")
        index = (self.workspace / ".git/index").read_bytes()
        result, values = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("false", values["drift"])
        self.assertFalse((self.workspace / "module-code-ran").exists())
        self.assertEqual(index, (self.workspace / ".git/index").read_bytes())
        self.assertNotIn("poison-marker", result.stdout + result.stderr)

    def test_malformed_action_audit_and_tool_failure_never_authorize_repair_or_run_successor(self):
        for failure in ("action", "audit", "signatures", "commitlint"):
            with self.subTest(failure=failure):
                for name in ("bad-project", "fail-guard-signatures", "fail-guard-commitlint", "guard-tools.jsonl"):
                    (self.workspace / name).unlink(missing_ok=True)
                shutil.rmtree(self.workspace / ".github/actions", ignore_errors=True)
                if failure == "action":
                    self.write(self.workspace, ".github/actions/dormant/action.yaml", "runs: {using: node24}\n")
                elif failure == "audit":
                    self.write(self.workspace, "bad-project", "<Project><NoWarn>NU1903</NoWarn></Project>")
                else:
                    (self.workspace / ("fail-guard-" + failure)).touch()
                self.commit(self.workspace)
                result, values = self.invoke()
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                self.assertEqual("false", values["drift"])
                self.assertIn(OWNERSHIP, result.stderr)
                expected = {"action": ".github/actions/dormant/action.yaml", "audit": "bad-project",
                            "signatures": "npm signature verification", "commitlint": "fixture-controlled-rule"}[failure]
                self.assertIn(expected, result.stderr)
                self.assertNotIn("private-tool-error", result.stdout + result.stderr)
                self.assertNotIn("\x1b", result.stdout + result.stderr)

    def test_owned_drift_precedes_malformed_event_and_never_installs_npm_tools(self):
        self.write(self.workspace, ".github/hexalith-ci.json", "owned malformed configuration")
        self.commit(self.workspace)
        result, values = self.invoke(event_name="pull_request", event={"pull_request": {"head": {"sha": "bad"}}})
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual("true", values["drift"])
        self.assertIn(".github/hexalith-ci.json", result.stderr)
        self.assertNotIn("endpoint", result.stderr)
        self.assertEqual([], self.tools())


if __name__ == "__main__":
    unittest.main()
