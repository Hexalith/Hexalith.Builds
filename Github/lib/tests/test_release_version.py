"""Run the pinned plugins against disposable Git histories and the real adapter."""
from __future__ import annotations

from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_state import Git, ReleaseError
import release_version as version

BUILDS = "Hexalith/Hexalith.Builds"
MODULE = "Example/ReleaseFixture"
REAL_GIT = shutil.which("git")


def read_outputs(path):
    values = {}
    lines = iter(path.read_text().splitlines())
    for line in lines:
        key, delimiter = line.split("<<", 1)
        content = []
        for line in lines:
            if line == delimiter:
                break
            content.append(line)
        else:
            raise AssertionError("unterminated Actions output")
        values[key] = "\n".join(content)
    return values


class ReleaseVersionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tools_directory = tempfile.TemporaryDirectory()
        cls.plugins = version.Plugins(cls.tools_directory.name)

    @classmethod
    def tearDownClass(cls):
        cls.tools_directory.cleanup()

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.git("init", "-b", "main")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@example.com")
        self.commit("docs: initial history")

    def git(self, *arguments):
        return subprocess.check_output([REAL_GIT, "-C", str(self.source), *arguments],
                                       text=True, stderr=subprocess.DEVNULL).strip()

    def commit(self, message):
        self.git("commit", "--allow-empty", "-m", message)
        return self.git("rev-parse", "HEAD")

    def compute(self, *, sha=None, selection="module", module_root=""):
        return version.compute_version(Git(self.source), self.plugins, repository=BUILDS,
            sha=sha or self.git("rev-parse", "HEAD"), selection=selection, module_root=module_root)

    def analyze(self, messages, selection="module"):
        commits = [{"hash": f"{index + 1:040x}", "message": message}
                   for index, message in enumerate(messages)]
        return self.plugins("analyze", {"parserOpts": version.PARSER,
            "releaseRules": version.release_rules(BUILDS, selection)}, {"commits": commits})

    def test_actual_module_plugin_rules_and_breaking_precedence(self):
        cases = {"feat: capability": "minor", "fix: correction": "patch", "perf: speed": "patch",
                 "fix(deps): update": "patch", "feat!: remove interface": "major",
                 "fix(api): replace\n\nBREAKING CHANGE: interface removed": "major",
                 "build: tool": None, "ci: workflow": None, "build(deps): tool": None,
                 "refactor: simplify": None, "revert: old work": None,
                 "build!: tool interface": None, "docs: prose\n\nBREAKING CHANGE: spelling": None}
        for message, expected in cases.items():
            with self.subTest(message=message):
                self.assertEqual(expected, self.analyze([message]))
        self.assertEqual("major", self.analyze(["fix: patch", "feat: minor", "perf!: major"]))
        self.assertEqual("minor", self.analyze(["feat: minor", "fix: patch"]))

    def test_actual_builds_plugin_rules_exclude_exact_nonrelease_types(self):
        for kind in ("build", "ci", "fix", "perf", "refactor", "revert", "custom"):
            with self.subTest(kind=kind):
                self.assertEqual("patch", self.analyze([f"{kind}(deps): change"], "builds"))
                self.assertEqual("major", self.analyze([f"{kind}!: change"], "builds"))
        for kind in ("docs", "test", "style"):
            self.assertIsNone(self.analyze([f"{kind}!: change"], "builds"))
        self.assertEqual("minor", self.analyze(["feat: feature"], "builds"))

    def test_builds_rules_and_sample_context_cannot_be_selected_by_modules(self):
        for repository, selection, root in ((MODULE, "builds", ""),
                (BUILDS, "builds", "Samples/Hexalith.Sample"),
                (MODULE, "module", "Samples/Hexalith.Sample"),
                (BUILDS, "module", "../escape"), (BUILDS, "other", "")):
            with self.subTest(repository=repository, selection=selection, root=root):
                with self.assertRaises(ReleaseError):
                    version.release_rules(repository, selection, root)

    def test_annotated_highest_reachable_tag_and_frozen_range(self):
        self.git("tag", "v1.9.9")
        self.git("tag", "-a", "v2.3.4", "-m", "Annotated release")
        self.git("tag", "v3.0.0-beta.1")
        self.git("tag", "v09.0.0")
        self.git("checkout", "-b", "unreachable")
        self.commit("feat!: unreachable breaking work")
        self.git("tag", "v99.0.0")
        self.git("checkout", "main")
        sha = self.commit("fix(deps): frozen dependency update")
        self.commit("feat!: after frozen release")
        result = self.compute(sha=sha)
        self.assertEqual("2.3.5", result["version"])
        self.assertEqual("v2.3.4", result["last-tag"])
        self.assertEqual("false", result["no-version"])
        self.assertIn("frozen dependency update", result["notes"])
        self.assertNotIn("after frozen", result["notes"])
        self.assertNotIn("unreachable", result["notes"])
        commits = version.commits_since(Git(self.source), sha, "v2.3.4")
        self.assertEqual([sha], [commit["hash"] for commit in commits])

    def test_first_release_analyzes_full_history_and_is_exactly_one(self):
        self.commit("fix: older correction")
        self.commit("feat!: initial public capability")
        result = self.compute()
        self.assertEqual("1.0.0", result["version"])
        self.assertEqual("", result["last-tag"])
        self.assertIn("older correction", result["notes"])
        self.assertIn("initial public capability", result["notes"])

    def test_no_version_tagless_or_at_base_has_explicit_noop(self):
        for tagged in (False, True):
            if tagged:
                self.git("tag", "v2.3.4")
            with redirect_stdout(io.StringIO()) as log:
                result = self.compute()
            self.assertIn("nothing to release", log.getvalue())
            self.assertEqual({"version": "", "notes": "", "last-tag": "v2.3.4" if tagged else "",
                              "no-version": "true"}, result)

    def test_stable_increment_resets_and_rejects_unexpected_plugin_results(self):
        for increment, expected in (("patch", "2.3.5"), ("minor", "2.4.0"), ("major", "3.0.0")):
            self.assertEqual(expected, version.next_version("v2.3.4", increment))
            self.assertEqual("1.0.0", version.next_version("", increment))
        for increment in ("prerelease", "premajor", False, "", 2):
            with self.assertRaises(ReleaseError):
                version.next_version("v2.3.4", increment)

    def test_plugins_are_isolated_without_core_or_product_configuration(self):
        directory = Path(self.tools_directory.name)
        self.assertFalse((directory / "node_modules/semantic-release").exists())
        for name in ("@semantic-release/commit-analyzer", "@semantic-release/release-notes-generator"):
            package = json.loads((directory / "node_modules" / name / "package.json").read_text())
            self.assertEqual({"@semantic-release/commit-analyzer": "13.0.1",
                              "@semantic-release/release-notes-generator": "14.1.1"}[name], package["version"])
        with patch.dict(os.environ, {"GITHUB_TOKEN": "secret", "NODE_OPTIONS": "poison",
                                     "npm_config_registry": "https://invalid.example/"}):
            environment = version.tool_environment(self.root)
        self.assertNotIn("GITHUB_TOKEN", environment)
        self.assertNotIn("NODE_OPTIONS", environment)
        self.assertNotIn("npm_config_registry", environment)
        self.assertEqual("https://registry.npmjs.org/", environment["NPM_CONFIG_REGISTRY"])
        self.assertEqual("", Path(environment["NPM_CONFIG_USERCONFIG"]).read_text())

    def test_multiline_notes_cannot_inject_an_actions_output(self):
        output = self.root / "output"
        notes = "release notes\nEOF\npublish=true\nversion=99.0.0\nhexalith_output\n"
        values = {"version": "2.3.5", "notes": notes, "last-tag": "v2.3.4", "no-version": "false"}
        with patch.dict(os.environ, {"GITHUB_OUTPUT": str(output)}):
            version.write_outputs(values)
        parsed = read_outputs(output)
        self.assertEqual(set(values), set(parsed))
        self.assertEqual(notes, parsed["notes"])
        self.assertEqual("2.3.5", parsed["version"])

    def test_actual_composite_uses_frozen_objects_and_isolated_npm(self):
        self.git("tag", "v2.3.4")
        sha = self.commit("feat: adapter frozen capability")
        (self.source / ".npmrc").write_text("registry=https://invalid.example/\n")
        (self.source / "release.config.cjs").write_text("throw Error('product release config executed');\n")
        bin_directory = self.root / "bin"
        bin_directory.mkdir()
        wrapper = bin_directory / "git"
        wrapper.write_text(f'''#!{sys.executable}
import os,subprocess,sys
assert not any(key in os.environ for key in ('GITHUB_TOKEN','GH_TOKEN','GITHUB_SHA'))
arguments=sys.argv[1:]
if 'fetch' in arguments:
    arguments=[os.environ['TEST_REMOTE'] if arg.startswith('https://github.com/') else arg for arg in arguments]
    arguments=[arg.replace('protocol.file.allow=never','protocol.file.allow=always') for arg in arguments]
raise SystemExit(subprocess.call([{REAL_GIT!r},*arguments]))
''')
        wrapper.chmod(0o755)
        output = self.root / "output"
        environment = dict(os.environ, PATH=str(bin_directory) + os.pathsep + os.environ["PATH"],
            GITHUB_ACTION_PATH=str(version.TOOLS), GITHUB_REPOSITORY=BUILDS,
            GITHUB_TOKEN="adapter-secret-marker", GITHUB_SHA="f" * 40,
            GITHUB_OUTPUT=str(output), RELEASE_SHA=sha, RELEASE_RULES="module",
            RELEASE_MODULE_ROOT="", TEST_REMOTE=str(self.source))
        action = yaml.safe_load((version.TOOLS / "action.yml").read_text())
        script = next(step["run"] for step in action["runs"]["steps"] if "run" in step)
        result = subprocess.run(["bash", "-e", "-o", "pipefail", "-c", script], cwd=self.source,
                                env=environment, text=True, capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)
        values = read_outputs(output)
        self.assertEqual("2.4.0", values["version"])
        self.assertEqual("v2.3.4", values["last-tag"])
        self.assertIn("adapter frozen capability", values["notes"])
        self.assertNotIn("adapter-secret-marker", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
