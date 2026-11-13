"""Execute guard composites against committed trees and anonymous local transports."""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import shlex
import subprocess
import sys
import tempfile
import unittest

import yaml

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

    def invoke(self, *, repository="Example/Module", module_root="", expected_source="", action=ACTION, ref=None):
        output = self.root / "github-output"
        output.write_text("")
        successor = self.workspace / "successor-ran"
        successor.unlink(missing_ok=True)
        environment = dict(self.environment, GITHUB_OUTPUT=str(output), GITHUB_ACTION_PATH=str(action),
                           CI_REPOSITORY=repository, CI_MODULE_ROOT=module_root, CI_EXPECTED_SOURCE=expected_source)
        metadata = yaml.safe_load((action / "action.yml").read_text())
        # Execute the real composite command and a downstream command in one
        # fail-fast shell; a failed guard must prevent that command from running.
        script = "\n".join(step["run"] for step in metadata["runs"]["steps"])
        if ref is not None:
            script = shlex.join([sys.executable, "-I", str(ROOT / "Github/lib/guard.py"), "--ref", ref])
        script += "\ntouch successor-ran\n"
        result = subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", script],
                                cwd=self.workspace, env=environment, capture_output=True, text=True)
        values = dict(line.split("=", 1) for line in output.read_text().splitlines())
        self.assertNotIn("secret-marker", result.stdout + result.stderr)
        self.assertEqual(result.returncode == 0, successor.exists())
        return result, values

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
            # Invalid YAML and runnable shell are opaque availability data.
            self.write(self.workspace, prefix + ".github/actions/" + name + "/action.yml", "[: opaque metadata\n")
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
        self.sample()
        result, values = self.invoke(repository="Hexalith/Hexalith.Builds", module_root=SAMPLE, expected_source=EXPECTED)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({"drift": "false"}, values)
        self.assertFalse(any("clone" in command or "fetch" in command for command in self.commands()))

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


if __name__ == "__main__":
    unittest.main()
