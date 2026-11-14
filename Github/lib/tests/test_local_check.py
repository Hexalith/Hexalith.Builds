"""Real committed Git inputs and shared guard/build/unit behavior for local CI."""

from __future__ import annotations

import contextlib
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

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ci_pipeline
import expected_state as state
import local_check as local
from test_ci_pipeline import FAKE_DOTNET, EXPECTED_ENGINE_FLAGS, EXPECTED_PROPERTIES
from test_expected_state import LocalTransport, Repository, configuration


ROOT = Path(__file__).resolve().parents[3]
REAL_GIT = shutil.which("git")
COMPOSITE = json.dumps({"name": "fixture", "description": "fixture composite", "runs": {
    "using": "composite", "steps": [{"run": "echo fixture", "shell": "bash"}]}})


class FixtureTools:
    """Parse the fixture's JSON-form YAML and observe complete commit messages."""

    def __init__(self, calls):
        self.calls = calls

    def parse_yaml(self, text):
        self.calls.append(("yaml", text))
        return json.loads(text)

    def lint_message(self, message, identity):
        self.calls.append(("commitlint", identity, message))


class LocalCheckTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="local-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.module = Repository(self.root / "module")
        self.source = Repository(self.root / "builds-source")
        self.delivered = Repository(self.root / "delivered")
        # A committed miniature Builds checkout runs the actual CLI as well as
        # the in-process API. No production dependency or checkout is changed.
        for source in (ROOT / "Github/lib").glob("*.py"):
            self.source.write("Github/lib/" + source.name, source.read_bytes())
        for source in (ROOT / "Github/guard").iterdir():
            if source.is_file():
                self.source.write("Github/guard/" + source.name, source.read_bytes())
        for name in ("check.py", "NuGet.ci.Config", "schemas/hexalith.ci.v1.json",
                     "schemas/hexalith.perimeter.v1.json"):
            self.source.write(name, (ROOT / name).read_bytes())
        self.source.write(".gitignore", "__pycache__/\n*.pyc\n")
        self.builds_sha = self.source.commit()
        self.source.command("tag", "-a", "v1.2.3", "-m", "test: annotated Builds tag")
        self.builds_path = self.module.root / local.BUILDS_PATH
        self.builds_path.parent.mkdir()
        self.module.command("clone", "-q", str(self.source.root), str(self.builds_path))
        self.builds = state.Git(self.builds_path)
        self.module.command("remote", "add", "origin", "https://github.com/Example/Fixture.git")
        self.module.write(".gitmodules", '[submodule "Builds"]\n\tpath = references/Hexalith.Builds\n'
                          '\turl = https://github.com/Hexalith/Hexalith.Builds.git\n')
        self.module.write(".gitignore", "commands.jsonl\ntests-completed\nbuild-completed\n**/bin/\n**/obj/\n")
        self.document = configuration(solution="none")
        self.calls = []
        self.installations = []
        self.transport = LocalTransport(self.delivered, self.source)
        self.bin = self.root / "sdk"
        self.bin.mkdir()
        executable = self.bin / "dotnet"
        executable.write_text(f"#!{sys.executable}\n" + FAKE_DOTNET)
        executable.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ.get("PATH", ""))
        self.record()

    def record(self, *, publish=True, base=True):
        self.module.write(".github/hexalith-ci.json", json.dumps(self.document))
        head = self.module.commit()
        if base:
            self.module.command("update-ref", "refs/remotes/origin/main", head)
        if publish:
            self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", json.dumps(self.document))
            self.delivered.write("rendered/Fixture.perimeter.json", json.dumps({"schema": "hexalith.perimeter.v1",
                "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
            self.delivered.commit()
            self.delivered.command("branch", "-f", "delivered/Fixture", "HEAD")
        return head

    def tools(self, directory):
        self.installations.append(Path(directory))
        return FixtureTools(self.calls)

    def run_check(self):
        with patch.dict(os.environ, self.environment, clear=True):
            local.run(self.module.root, transport=self.transport, tool_factory=self.tools)

    def invoke(self, **environment):
        return subprocess.run([sys.executable, "-I", "-S", str(self.builds_path / "check.py")],
                              cwd=self.module.root, env=dict(self.environment, **environment),
                              text=True, capture_output=True, check=False)

    def commands(self):
        path = self.module.root / "commands.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def cli_controls(self):
        """Control only external transports/tools; execute the actual check.py CLI."""
        self.ledger = self.root / "external-tools.jsonl"
        script = f"#!{sys.executable}\n" + f'''
import json, os, pathlib, subprocess, sys
args=sys.argv[1:]
name=pathlib.Path(sys.argv[0]).name
cwd=pathlib.Path.cwd()
payload=sys.stdin.read() if name=="node" else ""
with pathlib.Path({str(self.ledger)!r}).open("a") as stream:
    stream.write(json.dumps({{"tool":name,"args":args,"stdin":payload}})+"\\n")
if name=="git":
    for position, value in enumerate(args):
        if value=="https://github.com/Hexalith/Hexalith.CiCd.git":
            args[position]={self.delivered.root.as_uri()!r}
        elif value=="https://github.com/Hexalith/Hexalith.Builds.git":
            args[position]={self.source.root.as_uri()!r}
        elif value=="protocol.file.allow=never":
            args[position]="protocol.file.allow=always"
    raise SystemExit(subprocess.call([{REAL_GIT!r}, *args]))
assert not any(key.upper().startswith(("GITHUB_", "GH_", "NODE_")) for key in os.environ)
if name=="npm":
    assert args in (["ci", "--ignore-scripts"], ["audit", "signatures"]), args
    assert not cwd.is_relative_to(pathlib.Path({str(self.module.root)!r}))
    for filename in ("package.json", "package-lock.json"):
        assert (cwd / filename).read_bytes()==(pathlib.Path({str(self.builds_path)!r}) / "Github/guard" / filename).read_bytes()
elif name=="node":
    if args[0].endswith("js-yaml.js"):
        assert payload.startswith("# guard YAML input\\n")
        print(json.dumps(json.loads(payload.removeprefix("# guard YAML input\\n"))))
    else:
        assert args[0].endswith("cli.js") and "--color=false" in args, args
        config=pathlib.Path(args[args.index("--config")+1])
        assert config==pathlib.Path({str(self.builds_path)!r}) / "Github/guard/commitlint.config.mjs"
        assert config.is_file()
else:
    raise AssertionError(name)
'''
        for name in ("git", "npm", "node"):
            path = self.bin / name
            path.write_text(script)
            path.chmod(0o755)
        # Include the real Builds tag acquisition in the guard's transport path.
        self.delivered.write("rendered/Fixture.perimeter.json", json.dumps({"schema": "hexalith.perimeter.v1",
            "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"},
                        {"kind": "gitlink", "path": local.BUILDS_PATH, "buildsTag": "v1.2.3"}]}))
        self.delivered.commit()
        self.delivered.command("branch", "-f", "delivered/Fixture", "HEAD")

    def external_calls(self):
        """Return shimmed npm/Node calls and Git calls that name a fixed remote."""
        calls = [json.loads(line) for line in self.ledger.read_text().splitlines()] if self.ledger.exists() else []
        return [call for call in calls
                if call["tool"] != "git" or any(value.startswith("https://") for value in call["args"])]

    def build_fixture(self, **changes):
        self.document.update(solution="Fixture.slnx", hooks=["after-unit-tests"],
                             tests={"integration": ["test/Custom.csproj"],
                                    "exclude": {"test/Owned.csproj": "after-unit-tests"}})
        self.module.write(".github/actions/after-unit-tests/action.yml", COMPOSITE)
        self.data = {"projects": {"test/Z.Tests.csproj": True, "src/Ordinary.csproj": False,
                                 "test/A.Tests.csproj": True, "test/Fixture.IntegrationTests.csproj": True,
                                 "test/Custom.csproj": True, "test/Owned.csproj": True}, "xunit": False, **changes}
        for project in self.data["projects"]:
            self.module.write(project, "<Project />")
        self.module.write("Fixture.slnx", "<Solution />")
        self.module.write("global.json", json.dumps({"sdk": {"version": "11.0.100"}}))
        self.module.write("fake-sdk.json", json.dumps(self.data))
        self.record()

    def unchanged(self):
        """Observe caller source, index, refs and checkout identities exactly."""
        observations = []
        for git in (local.git_at(self.module.root), local.git_at(self.builds_path)):
            index = Path(git.run("rev-parse", "--git-path", "index").decode().removesuffix("\n"))
            if not index.is_absolute():
                index = git.root / index
            observations.append((index.read_bytes(), git.run("show-ref"), git.run("rev-parse", "HEAD"),
                                 git.run("config", "--local", "--null", "--list")))
            tracked = git.run("ls-files", "-z").decode().split("\0")
            observations.append({name: (git.root / name).read_bytes() for name in tracked
                                 if name and (git.root / name).is_file()})
        return observations

    def test_none_runs_real_guard_and_empty_range_without_sdk_or_actions_outputs(self):
        before = self.unchanged()
        output = self.root / "output"
        output.write_text("unchanged\n")
        self.environment.update(GITHUB_OUTPUT=str(output), GITHUB_WORKSPACE="/missing", CI_REPOSITORY="bad",
                                CI_MODULE_ROOT="bad", CI_EXPECTED_SOURCE="bad", GITHUB_EVENT_NAME="pull_request",
                                CI_EVENT_NAME="push", GITHUB_EVENT_PATH="/missing", CI_EVENT_PATH="/missing")
        self.run_check()
        self.assertEqual(before, self.unchanged())
        self.assertEqual("unchanged\n", output.read_text())
        self.assertEqual([], self.commands())
        self.assertFalse(any(call[0] == "commitlint" for call in self.calls))
        self.assertEqual(1, len(self.installations))

    def test_cli_success_and_product_failure_run_real_guard_and_preserve_status(self):
        for failure in (None, "test/A.Tests.csproj"):
            self.build_fixture(fail_project=failure)
            self.cli_controls()
            for name in ("commands.jsonl", "tests-completed", "build-completed"):
                (self.module.root / name).unlink(missing_ok=True)
            self.ledger.unlink(missing_ok=True)
            # Keep a nonempty commit range so the launched CLI calls commitlint.
            self.module.write("committed-source", str(failure))
            self.module.commit()
            before = self.unchanged()
            result = self.invoke(GITHUB_EVENT_PATH="/missing", CI_REPOSITORY="untrusted/context")
            with self.subTest(failure=failure):
                self.assertEqual(23 if failure else 0, result.returncode, result.stdout + result.stderr)
                self.assertIn("guard policy passed", result.stdout)
                self.assertEqual(["restore", "build"], [args[0] for args in self.commands()[:2]])
                expected = ["test/A.Tests.csproj"] + ([] if failure else ["test/Z.Tests.csproj"])
                self.assertEqual(expected, [args[1] for args in self.commands() if args[0] == "test"])
                completed = (self.module.root / "tests-completed").read_text().splitlines()
                self.assertEqual([project + suffix for project in expected
                                  for suffix in (":failing-test-completed", ":passing-test-completed")], completed)
                self.assertEqual(before, self.unchanged())
                calls = [json.loads(line) for line in self.ledger.read_text().splitlines()]
                self.assertEqual([["ci", "--ignore-scripts"], ["audit", "signatures"]],
                                 [call["args"] for call in calls if call["tool"] == "npm"])
                self.assertTrue(any(call["tool"] == "node" and call["args"][0].endswith("js-yaml.js") for call in calls))
                self.assertTrue(any(call["tool"] == "node" and call["args"][0].endswith("cli.js") for call in calls))
                self.assertTrue(any("https://github.com/Hexalith/Hexalith.CiCd.git" in call["args"] for call in calls))
                self.assertTrue(any("https://github.com/Hexalith/Hexalith.Builds.git" in call["args"] for call in calls))
                self.assertFalse(list(self.builds_path.rglob("*.pyc")))

    def test_cli_none_success_uses_real_guard_without_sdk(self):
        self.cli_controls()
        before = self.unchanged()
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("guard policy passed", result.stdout)
        self.assertEqual([], self.commands())
        self.assertEqual(before, self.unchanged())

    def test_cli_disables_fsmonitor_helpers_for_module_and_builds(self):
        self.cli_controls()
        marker = self.root / "fsmonitor-executed"
        helper = self.root / "fsmonitor-helper"
        helper.write_text(f"#!{sys.executable}\nfrom pathlib import Path\nPath({str(marker)!r}).write_text('executed')\n")
        helper.chmod(0o755)
        self.module.command("config", "core.fsmonitor", str(helper))
        self.module.command("-C", str(self.builds_path), "config", "core.fsmonitor", str(helper))
        before = self.unchanged()
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse(marker.exists())
        self.assertEqual(before, self.unchanged())

    def test_cli_refuses_effective_clean_process_and_included_filters_without_executing_them(self):
        self.cli_controls()
        marker = self.root / "filter-executed"
        helper = self.root / "filter-helper"
        helper.write_text(f"#!{sys.executable}\nfrom pathlib import Path\nPath({str(marker)!r}).write_text('executed')\n")
        helper.chmod(0o755)
        for directory in (self.module.root, self.builds_path):
            attributes = directory / ".git/info/attributes"
            attributes.write_text(".gitignore filter=fixture\n")
            path = directory / ".gitignore"
            original = path.read_bytes()
            path.write_bytes(original + b"caller change\n")
            for option in ("clean", "process", "included"):
                if option == "included":
                    included = self.root / "included-filter-config"
                    included.write_text(f'[filter "fixture"]\n\tclean = {helper}\n')
                    key, value = "include.path", str(included)
                else:
                    key, value = "filter.fixture." + option, str(helper)
                self.module.command("-C", str(directory), "config", key, value)
                before = self.unchanged()
                result = self.invoke()
                with self.subTest(directory=directory, option=option):
                    self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                    self.assertIn("executable Git clean/process filters", result.stderr)
                    self.assertFalse(marker.exists())
                    self.assertEqual(before, self.unchanged())
                    self.assertEqual([], self.commands())
                self.module.command("-C", str(directory), "config", "--unset-all", key)
            attributes.unlink()
            path.write_bytes(original)
        self.assertEqual([], self.external_calls())

    def test_empty_effective_filter_override_is_allowed_without_executing_helper(self):
        marker = self.root / "filter-executed"
        self.module.command("config", "filter.fixture.clean", f"touch {marker}")
        self.module.command("config", "--add", "filter.fixture.clean", "")
        # Status must compare this file's content through the fixture filter.
        (self.module.root / ".git/info/attributes").write_text(".gitignore filter=fixture\n")
        tracked = self.module.root / ".gitignore"
        tracked.write_bytes(tracked.read_bytes())
        os.utime(tracked, (tracked.stat().st_atime, tracked.stat().st_mtime + 10))
        before = self.unchanged()
        self.run_check()
        self.assertFalse(marker.exists())
        self.assertEqual(before, self.unchanged())

    def test_shallow_head_rejects_truncated_range_even_when_both_endpoints_resolve(self):
        base = self.module.command("rev-parse", "HEAD")
        for number in range(2):
            self.module.write("source", str(number))
            self.module.commit()
        head = self.module.command("rev-parse", "HEAD")
        shallow = self.module.root / ".git/shallow"
        shallow.write_text(head + "\n")
        self.cli_controls()
        self.assertEqual(base, self.module.command("rev-parse", base + "^{commit}"))
        self.assertEqual([head], self.module.command("rev-list", base + "..HEAD").splitlines())
        before = self.unchanged()
        result = self.invoke()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("history is shallow", result.stderr)
        self.assertIn("fetch --unshallow", result.stderr)
        self.assertEqual(before, self.unchanged())
        self.assertEqual(head + "\n", shallow.read_text())
        self.assertEqual([], self.external_calls())
        self.assertEqual([], self.commands())

    def test_whitespace_module_and_dependency_roots_are_preserved(self):
        root = self.root / " module with trailing whitespace \t"
        self.module.root.rename(root)
        self.module.root = root
        self.builds_path = root / local.BUILDS_PATH
        self.builds = state.Git(self.builds_path)
        dependency = Repository(self.root / "dependency")
        dependency.write("source", "committed")
        dependency.commit()
        path = "references/Extra dependency "
        self.module.command("clone", "-q", str(dependency.root), str(root / path))
        self.module.write(".gitmodules", (root / ".gitmodules").read_text()
                          + f'[submodule "Extra"]\n\tpath = "{path}"\n\turl = https://github.com/Example/Extra.git\n')
        self.document["submodules"] = [path]
        self.record()
        before = self.unchanged()
        self.run_check()
        self.assertEqual(before, self.unchanged())
        self.assertEqual(1, len(self.installations))

    def test_guard_time_source_edit_refuses_product_stages_and_preserves_caller_edit(self):
        path = self.module.root / ".gitignore"
        changed = path.read_bytes() + b"caller edit\n"
        original = self.tools

        def edit(directory):
            tools = original(directory)
            path.write_bytes(changed)
            return tools

        with patch.object(self, "tools", side_effect=edit), self.assertRaisesRegex(ci_pipeline.PipelineError, "commit all"):
            self.run_check()
        self.assertEqual(changed, path.read_bytes())
        self.assertEqual([], self.commands())

    def test_guard_time_head_change_refuses_product_stages_without_reverting_caller(self):
        original = self.tools
        changed = []

        def edit(directory):
            tools = original(directory)
            self.module.write("caller-source", "committed during guard")
            changed.append(self.module.commit())
            return tools

        with patch.object(self, "tools", side_effect=edit), self.assertRaisesRegex(ci_pipeline.PipelineError, "HEAD changed"):
            self.run_check()
        self.assertEqual(changed[0], self.module.command("rev-parse", "HEAD"))
        self.assertEqual("committed during guard", (self.module.root / "caller-source").read_text())
        self.assertEqual([], self.commands())

    def test_guard_time_builds_pin_change_refuses_product_stages_without_retargeting(self):
        self.source.write("new", "next Builds revision")
        tip = self.source.commit()
        self.module.command("-C", str(self.builds_path), "fetch", "-q", "origin")
        original = self.tools

        def edit(directory):
            tools = original(directory)
            self.module.command("-C", str(self.builds_path), "checkout", "-q", tip)
            return tools

        with patch.object(self, "tools", side_effect=edit), self.assertRaisesRegex(state.ExpectedStateError, "differs from committed gitlink"):
            self.run_check()
        self.assertEqual(tip, self.module.command("-C", str(self.builds_path), "rev-parse", "HEAD"))
        self.assertEqual([], self.commands())

    def test_build_time_edit_stops_unit_successor_and_preserves_caller_edit(self):
        self.build_fixture()
        path = self.module.root / "src/Ordinary.csproj"
        changed = path.read_bytes() + b"caller edit\n"
        original = ci_pipeline.run_command

        def edit(arguments, root, **options):
            result = original(arguments, root, **options)
            if arguments[0:2] == ["dotnet", "build"]:
                path.write_bytes(changed)
            return result

        with patch.object(ci_pipeline, "run_command", side_effect=edit), self.assertRaisesRegex(ci_pipeline.PipelineError, "commit all"):
            self.run_check()
        self.assertEqual(changed, path.read_bytes())
        self.assertFalse(any(args[0] == "test" for args in self.commands()))

    def test_unit_time_edit_prevents_success_and_preserves_caller_edit(self):
        self.build_fixture()
        path = self.module.root / "src/Ordinary.csproj"
        changed = path.read_bytes() + b"caller edit\n"
        original = ci_pipeline.run_command

        def edit(arguments, root, **options):
            result = original(arguments, root, **options)
            if arguments[0:3] == ["dotnet", "test", "test/Z.Tests.csproj"]:
                path.write_bytes(changed)
            return result

        with patch.object(ci_pipeline, "run_command", side_effect=edit), self.assertRaisesRegex(ci_pipeline.PipelineError, "commit all"):
            self.run_check()
        self.assertEqual(changed, path.read_bytes())
        self.assertEqual(2, sum(args[0] == "test" for args in self.commands()))

    def test_unit_time_head_change_prevents_success_without_reverting_caller(self):
        self.build_fixture()
        original = ci_pipeline.run_command
        changed = []

        def edit(arguments, root, **options):
            result = original(arguments, root, **options)
            if arguments[0:3] == ["dotnet", "test", "test/Z.Tests.csproj"]:
                self.module.write("caller-source", "committed during unit tests")
                changed.append(self.module.commit())
            return result

        with patch.object(ci_pipeline, "run_command", side_effect=edit), self.assertRaisesRegex(ci_pipeline.PipelineError, "HEAD changed"):
            self.run_check()
        self.assertEqual(changed[0], self.module.command("rev-parse", "HEAD"))
        self.assertEqual("committed during unit tests", (self.module.root / "caller-source").read_text())

    def test_unit_time_tag_change_prevents_success_without_restoring_tag(self):
        self.build_fixture()
        self.source.write("new", "next Builds revision")
        tip = self.source.commit()
        self.module.command("-C", str(self.builds_path), "fetch", "-q", "origin")
        original = ci_pipeline.run_command

        def edit(arguments, root, **options):
            result = original(arguments, root, **options)
            if arguments[0:3] == ["dotnet", "test", "test/Z.Tests.csproj"]:
                self.module.command("-C", str(self.builds_path), "tag", "-f", "v1.2.3", tip)
            return result

        with patch.object(ci_pipeline, "run_command", side_effect=edit), self.assertRaisesRegex(state.ExpectedStateError, "tag.*changed"):
            self.run_check()
        self.assertEqual(tip, self.module.command("-C", str(self.builds_path), "rev-parse", "refs/tags/v1.2.3"))

    def test_explicit_range_lints_all_messages_and_ignores_event_payload(self):
        base = self.module.command("rev-parse", "HEAD")
        for number in range(2):
            self.module.write("source", str(number))
            self.module.commit()
        self.environment["CI_EVENT_PATH"] = "/missing"
        self.run_check()
        commits = self.module.command("rev-list", "--reverse", base + "..HEAD").splitlines()
        self.assertEqual(["commit " + sha for sha in commits], [call[1] for call in self.calls if call[0] == "commitlint"])

    def test_guard_precedes_sdk_and_shared_flags_and_classification_apply(self):
        self.build_fixture()
        original = ci_pipeline.run_command

        def ordered(arguments, root, **options):
            if arguments[0] == "dotnet":
                self.assertEqual(1, len(self.installations))
            return original(arguments, root, **options)

        before = self.unchanged()
        with patch.object(ci_pipeline, "run_command", side_effect=ordered), patch.object(
                ci_pipeline, "initialize_submodules", side_effect=AssertionError("local mutation")):
            self.run_check()
        commands = self.commands()
        self.assertEqual(["restore", "build"], [command[0] for command in commands[:2]])
        self.assertEqual(1, sum(command[0] == "restore" for command in commands))
        self.assertEqual(["test/A.Tests.csproj", "test/Z.Tests.csproj"],
                         [command[1] for command in commands if command[0] == "test"])
        for command in commands:
            if command[0] != "sln":
                for flag in (*EXPECTED_PROPERTIES, *EXPECTED_ENGINE_FLAGS):
                    self.assertIn(flag, command)
        self.assertEqual(before, self.unchanged())

    def test_failing_project_completes_and_preserves_status_without_later_projects(self):
        self.build_fixture(fail_project="test/A.Tests.csproj")
        before = self.unchanged()
        with self.assertRaises(ci_pipeline.PipelineError) as raised:
            self.run_check()
        self.assertEqual(23, raised.exception.returncode)
        self.assertEqual(["test/A.Tests.csproj:failing-test-completed", "test/A.Tests.csproj:passing-test-completed"],
                         (self.module.root / "tests-completed").read_text().splitlines())
        self.assertEqual(before, self.unchanged())

    def test_guard_and_build_failures_stop_successors(self):
        self.build_fixture(failure="build")
        self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", "owned drift")
        self.delivered.commit()
        self.delivered.command("branch", "-f", "delivered/Fixture", "HEAD")
        with self.assertRaisesRegex(state.ExpectedStateError, "Fix the product code"):
            self.run_check()
        self.assertEqual([], self.commands())
        self.record()
        with self.assertRaises(ci_pipeline.PipelineError) as raised:
            self.run_check()
        self.assertEqual(17, raised.exception.returncode)
        self.assertEqual(["restore", "build"], [command[0] for command in self.commands()])

    def test_cli_mismatched_checkout_wins_over_malformed_json_and_inherited_git(self):
        self.module.write(".github/hexalith-ci.json", "malformed JSON")
        self.module.commit()
        self.source.write("new", "other revision")
        changed = self.source.commit()
        self.module.command("-C", str(self.builds_path), "fetch", "-q", "origin")
        self.module.command("-C", str(self.builds_path), "checkout", "-q", changed)
        before = self.unchanged()
        result = self.invoke(GIT_DIR=str(self.source.root / ".git"), GIT_WORK_TREE=str(self.source.root))
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("differs from committed gitlink", result.stderr)
        self.assertIn(state.OWNERSHIP_MESSAGE, result.stderr)
        self.assertNotIn("Expecting value", result.stderr)
        self.assertEqual(before, self.unchanged())
        self.assertFalse(list(self.builds_path.rglob("*.pyc")))

    def test_missing_wrong_mode_or_uninitialized_builds_precedes_json(self):
        self.module.write(".github/hexalith-ci.json", "malformed JSON")
        self.module.commit()
        shutil.rmtree(self.builds_path)
        before = (self.module.root / ".git/index").read_bytes()
        # The module's own Builds copy is gone; launch the repository command.
        result = subprocess.run([sys.executable, "-I", "-S", str(ROOT / "check.py")], cwd=self.module.root,
                                env=self.environment, text=True, capture_output=True, check=False)
        self.assertEqual(1, result.returncode)
        self.assertIn("initialize the required", result.stderr)
        self.assertEqual(before, (self.module.root / ".git/index").read_bytes())
        for mode in ("missing", "regular"):
            self.module.command("rm", "--cached", local.BUILDS_PATH)
            if mode == "regular":
                self.module.write(local.BUILDS_PATH, "ordinary file")
                self.module.command("add", local.BUILDS_PATH)
            self.module.command("commit", "-qm", "test: remove Builds gitlink")
            with self.subTest(mode=mode), self.assertRaisesRegex(state.ExpectedStateError, "direct 160000"):
                self.run_check()
            if mode == "missing":
                self.module.command("update-index", "--add", "--cacheinfo", "160000", self.builds_sha, local.BUILDS_PATH)
                self.module.command("commit", "-qm", "test: restore fixture gitlink")

    def test_invalid_config_and_missing_mismatched_or_unavailable_tag_fail_before_tools(self):
        for document, message in (({"schema": "unknown"}, "unsupported schema"),
                                  (configuration(solution="none", buildsTag="v9.9.9"), "required local commit"),
                                  (configuration(solution="none", buildsTag="main"), "exact stable")):
            self.document = document
            self.record()
            with self.subTest(document=document), self.assertRaisesRegex(ValueError, message):
                self.run_check()
        self.document = configuration(solution="none")
        del self.document["buildsTag"]
        self.record()
        with self.assertRaisesRegex(local.ConfigError, "buildsTag"):
            self.run_check()
        self.source.write("new", "different tag object")
        self.source.commit()
        self.source.command("tag", "v2.0.0")
        self.module.command("-C", str(self.builds_path), "fetch", "-q", "--tags")
        self.document = configuration(solution="none", buildsTag="v2.0.0")
        self.record()
        with self.assertRaisesRegex(state.ExpectedStateError, "different from the committed gitlink"):
            self.run_check()
        self.assertEqual([], self.installations)
        self.assertEqual([], self.commands())

    def test_dirty_staged_untracked_and_hidden_module_inputs_fail_without_mutation(self):
        for kind in ("dirty", "staged", "untracked", "assumed", "sparse"):
            path = self.module.root / ".gitignore"
            original = path.read_bytes()
            if kind in ("dirty", "staged"):
                path.write_bytes(original + b"modified\n")
                if kind == "staged":
                    self.module.command("add", ".gitignore")
            elif kind == "untracked":
                self.module.write("untracked-input", "new input")
            else:
                self.module.command("update-index", "--assume-unchanged" if kind == "assumed" else "--skip-worktree", ".gitignore")
            before = self.unchanged()
            named = "untracked-input" if kind == "untracked" else r"\.gitignore"
            with self.subTest(kind=kind), self.assertRaisesRegex(ci_pipeline.PipelineError, "commit all.*flags: " + named + r"\Z"):
                self.run_check()
            self.assertEqual(before, self.unchanged())
            path.write_bytes(original)
            self.module.command("update-index", "--no-assume-unchanged", ".gitignore")
            self.module.command("update-index", "--no-skip-worktree", ".gitignore")
            self.module.command("add", ".gitignore")
            if kind == "untracked":
                (self.module.root / "untracked-input").unlink()

    def test_direct_dependencies_must_be_declared_aligned_and_clean_without_initialization(self):
        dependency = Repository(self.root / "dependency")
        dependency.write("data", "committed")
        dependency.write(".gitmodules", '[submodule "Nested"]\n\tpath = references/Nested\n'
                         '\turl = https://github.com/Example/Nested.git\n')
        (dependency.root / "references/Nested").mkdir(parents=True)
        dependency.command("update-index", "--add", "--cacheinfo", "160000", self.builds_sha, "references/Nested")
        pin = dependency.commit()
        path = "references/Extra dependency"
        self.document["submodules"] = [path]
        (self.module.root / path).mkdir()
        self.module.command("update-index", "--add", "--cacheinfo", "160000", pin, path)
        self.record()
        with self.assertRaisesRegex(ci_pipeline.PipelineError, "not declared"):
            self.run_check()
        self.assertEqual(([], []), (self.installations, self.transport.calls))
        self.module.write(".gitmodules", (self.module.root / ".gitmodules").read_text()
                          + f'[submodule "Extra"]\n\tpath = {path}\n\turl = https://github.com/Example/Extra.git\n')
        self.record()
        with self.assertRaisesRegex(state.ExpectedStateError, "initialize the required"):
            self.run_check()
        self.assertFalse((self.module.root / path / ".git").exists())
        self.assertEqual(([], []), (self.installations, self.transport.calls))
        self.module.command("clone", "-q", str(dependency.root), str(self.module.root / path))
        self.run_check()
        self.assertFalse((self.module.root / path / "references/Nested/.git").exists())
        self.installations.clear()
        self.transport.calls.clear()
        (self.module.root / path / "data").write_text("dirty")
        with self.assertRaisesRegex(ci_pipeline.PipelineError, "Extra dependency.*commit all"):
            self.run_check()
        (self.module.root / path / "data").write_text("committed")
        dependency.write("new", "different revision")
        tip = dependency.commit()
        self.module.command("-C", str(self.module.root / path), "fetch", "-q")
        self.module.command("-C", str(self.module.root / path), "checkout", "-q", tip)
        with self.assertRaisesRegex(state.ExpectedStateError, "differs from committed gitlink"):
            self.run_check()
        self.assertEqual(tip, state.Git(self.module.root / path).run("rev-parse", "HEAD").decode().strip())
        self.assertEqual(([], []), (self.installations, self.transport.calls))

    def test_undeclared_builds_gitlink_refuses_before_guard(self):
        self.module.write(".gitmodules", "")
        self.record()
        with self.assertRaisesRegex(ci_pipeline.PipelineError,
                                    "references/Hexalith.Builds: required direct submodule is not declared"):
            self.run_check()
        self.assertEqual(([], []), (self.installations, self.transport.calls))

    def test_subdirectory_workspace_checks_the_module_root(self):
        before = self.unchanged()
        with patch.dict(os.environ, self.environment, clear=True):
            local.run(self.module.root / ".github", transport=self.transport, tool_factory=self.tools)
        self.assertEqual(before, self.unchanged())
        self.assertEqual(1, len(self.installations))

    def test_outside_work_tree_refuses_without_guard_ownership_message(self):
        outside = self.root / "outside"
        outside.mkdir()
        with patch.dict(os.environ, self.environment, clear=True), self.assertRaisesRegex(
                ci_pipeline.PipelineError, "inside the module's Git work tree") as raised:
            local.run(outside, transport=self.transport, tool_factory=self.tools)
        self.assertNotIn(state.OWNERSHIP_MESSAGE, str(raised.exception))
        self.assertEqual([], self.installations)

    def test_dirty_builds_and_staged_gitlink_refuse_without_changing_inputs(self):
        ignored = self.builds_path / ".gitignore"
        original = ignored.read_bytes()
        ignored.write_bytes(original + b"modified\n")
        before = self.unchanged()
        with self.assertRaisesRegex(ci_pipeline.PipelineError, "Hexalith.Builds.*commit all"):
            self.run_check()
        self.assertEqual(before, self.unchanged())
        ignored.write_bytes(original)
        self.source.write("new", "next revision")
        next_sha = self.source.commit()
        self.module.command("update-index", "--cacheinfo", "160000", next_sha, local.BUILDS_PATH)
        before = self.unchanged()
        with self.assertRaisesRegex(ci_pipeline.PipelineError, "module.*commit all"):
            self.run_check()
        self.assertEqual(before, self.unchanged())
        # Committed `ignore = all` hides the gitlink from plain diff and status.
        self.module.command("update-index", "--cacheinfo", "160000", self.builds_sha, local.BUILDS_PATH)
        self.module.write(".gitmodules", (self.module.root / ".gitmodules").read_text() + "\tignore = all\n")
        self.record()
        self.module.command("update-index", "--cacheinfo", "160000", next_sha, local.BUILDS_PATH)
        with self.subTest(ignore="all"), self.assertRaisesRegex(ci_pipeline.PipelineError, "module.*commit all"):
            self.run_check()

    def test_missing_base_and_unsupported_origin_do_not_start_guard_or_sdk(self):
        head = self.module.command("rev-parse", "HEAD")
        self.module.command("update-ref", "-d", "refs/remotes/origin/main")
        with self.assertRaisesRegex(ci_pipeline.PipelineError, "origin/main.*fetch"):
            self.run_check()
        # Restore the base so each origin refusal is the failure under test.
        self.module.command("update-ref", "refs/remotes/origin/main", head)
        unsupported, invalid = "one standard GitHub HTTPS or SSH", "invalid GitHub repository identity"
        for origin, message in (("https://example.com/Example/Fixture.git", unsupported),
                                ("file:///tmp/Fixture", unsupported),
                                ("https://token@github.com/Example/Fixture.git", unsupported),
                                ("https://github.com/Example/Fixture.git?query", unsupported),
                                ("https://github.com/../Fixture.git", invalid),
                                ("https://github.com/Example/..", invalid),
                                ("https://github.com/Hexalith/Hexalith.Builds.git", "Builds itself is checked by its own CI"),
                                ("git@github.com:hexalith/hexalith.builds", "Builds itself is checked by its own CI")):
            self.module.command("remote", "set-url", "origin", origin)
            with self.subTest(origin=origin), self.assertRaisesRegex(ci_pipeline.PipelineError, message):
                self.run_check()
        self.module.command("config", "--add", "remote.origin.url", "https://github.com/Example/Fixture.git")
        with self.subTest(origin="multi-valued"), self.assertRaisesRegex(ci_pipeline.PipelineError, unsupported):
            self.run_check()
        self.module.command("config", "--unset-all", "remote.origin.url")
        with self.subTest(origin="missing"), self.assertRaisesRegex(ci_pipeline.PipelineError, unsupported):
            self.run_check()
        self.assertEqual([], self.transport.calls)
        self.assertEqual([], self.commands())

    def test_builds_origin_is_refused_before_module_gitlink_checks(self):
        builds = Repository(self.root / "builds-own")
        builds.write(".github/hexalith-ci.json", "malformed JSON")
        builds.commit()
        builds.command("remote", "add", "origin", "https://github.com/Hexalith/Hexalith.Builds.git")
        with patch.dict(os.environ, self.environment, clear=True), \
                self.assertRaisesRegex(ci_pipeline.PipelineError, "Builds itself is checked by its own CI"):
            local.run(builds.root, transport=self.transport, tool_factory=self.tools)
        self.assertEqual([], self.installations)

    def test_standard_https_and_ssh_origins_supply_only_repository_identity(self):
        for origin in ("https://github.com/Example/Fixture", "https://github.com/Example/Fixture.git",
                       "git@github.com:Example/Fixture.git", "ssh://git@github.com/Example/Fixture.git"):
            self.module.command("remote", "set-url", "origin", origin)
            with self.subTest(origin=origin):
                self.assertEqual("Example/Fixture", local.repository_identity(local.git_at(self.module.root)))

    def add_dependency(self):
        dependency = Repository(self.root / "dependency")
        dependency.write("data", "committed")
        dependency.commit()
        path = "references/Extra"
        self.module.command("clone", "-q", str(dependency.root), str(self.module.root / path))
        self.module.write(".gitmodules", (self.module.root / ".gitmodules").read_text()
                          + f'[submodule "Extra"]\n\tpath = {path}\n\turl = https://github.com/Example/Extra.git\n')
        self.document["submodules"] = [path]
        return dependency, self.module.root / path

    def test_guard_time_builds_edit_refuses_product_stages_and_preserves_caller_edit(self):
        self.build_fixture()
        path = self.builds_path / ".gitignore"
        changed = path.read_bytes() + b"caller edit\n"
        original = self.tools

        def edit(directory):
            tools = original(directory)
            path.write_bytes(changed)
            return tools

        with patch.object(self, "tools", side_effect=edit), \
                self.assertRaisesRegex(ci_pipeline.PipelineError, "Hexalith.Builds: commit all.*: .gitignore"):
            self.run_check()
        self.assertEqual(changed, path.read_bytes())
        self.assertEqual([], self.commands())

    def test_build_time_dependency_edit_or_retarget_stops_unit_successor(self):
        dependency, directory = self.add_dependency()
        self.build_fixture()
        dependency.write("new", "different revision")
        tip = dependency.commit()
        self.module.command("-C", str(directory), "fetch", "-q")
        original = ci_pipeline.run_command
        for change, error, message in (("edit", ci_pipeline.PipelineError, "references/Extra: commit all.*: data"),
                                       ("retarget", state.ExpectedStateError, "references/Extra: checkout HEAD .* differs")):
            def edit(arguments, root, **options):
                result = original(arguments, root, **options)
                if arguments[0:2] == ["dotnet", "build"]:
                    if change == "edit":
                        (directory / "data").write_text("caller edit")
                    else:
                        self.module.command("-C", str(directory), "checkout", "-q", tip)
                return result

            (self.module.root / "commands.jsonl").unlink(missing_ok=True)
            with self.subTest(change=change):
                with patch.object(ci_pipeline, "run_command", side_effect=edit), self.assertRaisesRegex(error, message):
                    self.run_check()
                self.assertEqual(["restore", "build"], [args[0] for args in self.commands()])
            if change == "edit":
                self.assertEqual("caller edit", (directory / "data").read_text())
                (directory / "data").write_text("committed")
        self.assertEqual(tip, self.module.command("-C", str(directory), "rev-parse", "HEAD"))

    def test_stat_dirty_tracked_input_does_not_refresh_caller_index(self):
        path = self.module.root / ".gitignore"
        status = path.stat()
        os.utime(path, ns=(status.st_atime_ns, status.st_mtime_ns + 5_000_000_000))
        before = self.unchanged()
        self.run_check()
        self.assertEqual(before, self.unchanged())

    def test_duplicate_committed_submodule_paths_refuse_before_guard(self):
        self.module.write(".gitmodules", (self.module.root / ".gitmodules").read_text()
                          + f'[submodule "Duplicate"]\n\tpath = {local.BUILDS_PATH}\n'
                          '\turl = https://github.com/Example/Duplicate.git\n')
        self.record()
        with self.assertRaisesRegex(ci_pipeline.PipelineError, "duplicate direct submodule paths"):
            self.run_check()
        self.assertEqual([], self.installations)

    def test_main_preserves_product_exit_status(self):
        with patch.object(local, "run", side_effect=ci_pipeline.PipelineError("product failed", 23)), \
                patch.object(sys, "argv", ["check.py"]), contextlib.redirect_stderr(io.StringIO()) as output:
            self.assertEqual(23, local.main())
        self.assertIn("product failed", output.getvalue())

    def test_actual_module_sdk_restore_build_and_evaluation(self):
        # Package-free smoke check uses the module's actual SDK selection. The
        # shared pipeline's real xUnit suite proves complete failing projects.
        self.document.update(solution="Fixture.slnx")
        self.module.write("global.json", (ROOT / "global.json").read_bytes())
        self.module.write("Fixture.slnx", '<Solution><Project Path="src/Library.csproj" /></Solution>')
        self.module.write("src/Library.csproj", '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                          '<TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>')
        self.module.write("src/Value.cs", "public sealed class Value { }")
        self.record()
        self.environment["PATH"] = os.environ["PATH"]
        before = self.unchanged()
        self.run_check()
        self.assertEqual(before, self.unchanged())
        self.assertTrue((self.module.root / "src/bin/Release/net11.0/Library.dll").is_file())


if __name__ == "__main__":
    unittest.main()
