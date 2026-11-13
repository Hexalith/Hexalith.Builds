"""Committed action policy fixtures using the actual pinned js-yaml CLI."""

from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import expected_state as state
import guard_actions
from test_expected_state import Repository


ROOT = Path(__file__).resolve().parents[3]
PIN = "a" * 40
VALID = "name: Fixture\ndescription: Exercise a custom action\nruns:\n  using: composite\n  steps:\n    - shell: bash\n      run: echo passed\n"


class PinnedParser:
    """A test adapter to root's already signature-verified, integrity lock pin."""

    def __init__(self):
        self.cli = ROOT / "node_modules/js-yaml/bin/js-yaml.js"
        package = json.loads((self.cli.parents[1] / "package.json").read_text())
        if package["version"] != "4.3.2":
            raise AssertionError("action tests require the committed js-yaml 4.3.2 pin")
        self.calls = 0

    def parse_yaml(self, text):
        self.calls += 1
        result = subprocess.run(["node", str(self.cli)], input="# YAML data\n" + text,
                                text=True, capture_output=True, timeout=30, check=False)
        if result.returncode:
            raise state.ExpectedStateError("pinned YAML parser rejected input")
        try:
            return json.loads(result.stdout)
        except ValueError as error:
            raise state.ExpectedStateError("pinned YAML parser did not return a single JSON value") from error


class ActionLintTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repository = Repository(Path(temporary.name) / "module")
        self.parser = PinnedParser()

    def write(self, data=VALID, name="fixture", extension="yml"):
        path = ".github/actions/" + name + "/action." + extension
        self.repository.write(path, data)
        return path

    def lint(self):
        self.repository.commit()
        guard_actions.lint(state.tree_snapshot(state.Git(self.repository.root)), self.parser)

    def reject(self, data, rule, *, name="fixture"):
        path = self.write(data, name)
        with self.assertRaises(state.ExpectedStateError) as raised:
            self.lint()
        self.assertIn(state.escaped(path), str(raised.exception))
        self.assertIn(rule, str(raised.exception))
        self.assertIn(state.OWNERSHIP_MESSAGE, str(raised.exception))

    def test_dormant_nested_metadata_both_extensions_and_json_style_are_parsed(self):
        self.write(name="dormant/nested")
        self.write(json.dumps({"runs": {"using": "composite", "steps": [{"shell": "bash", "run": "echo okay"}]}}),
                   name="other", extension="yaml")
        self.repository.write("other/action.yml", "unrelated invalid YAML")
        self.lint()
        self.assertEqual(2, self.parser.calls)

    def test_flow_aliases_quoting_and_literal_uses_inside_a_run_block(self):
        self.write("name: Quoted\ndescription: Flow and aliases\n"
                   "reference: &target third/party/path@" + PIN + "\n"
                   "runs: {using: composite, steps: [{uses: *target}, {shell: bash, run: 'dotnet test \"some project.csproj\" --no-build'}]}\n")
        self.write("runs:\n  using: composite\n  steps:\n    - shell: bash\n      run: |\n        echo 'uses: unpinned/action@main'\n", name="literal")
        self.lint()

    def test_existing_sample_actions_pass_with_their_explicit_project_commands(self):
        for name in ("after-unit-tests", "sample-windows"):
            self.write((ROOT / ".github/actions" / name / "action.yml").read_text(), name=name)
        self.lint()

    def test_unsafe_yaml_and_invalid_structures_fail_with_logical_paths(self):
        cases = [
            ("[]", "single mapping"),
            ("runs: {using: node24, main: index.js}", "composite"),
            ("runs: {using: docker, image: Dockerfile}", "composite"),
            ("runs: {using: composite, steps: []}", "nonempty sequence"),
            ("runs: {using: composite, steps: [text]}", "must be a mapping"),
            ("runs: {using: composite, steps: [{run: echo okay}]}", "requires a shell"),
            ("runs: {using: composite, steps: [{shell: bash, run: 123}]}", "nonempty text"),
            ("runs: {using: composite, steps: [{shell: bash, run: echo, uses: other/action@main}]}", "exactly one"),
            ("runs: {using: composite, steps: [{name: missing-command}]}", "exactly one"),
            ("runs: {using: composite, using: node24}", "parser"),
            ("!!js/function 'function () {}'", "parser"),
            (VALID + "---\n" + VALID, "parser"),
            ("runs: [", "parser"),
        ]
        for number, (data, rule) in enumerate(cases):
            with self.subTest(rule=rule, number=number):
                self.reject(data, rule, name="invalid-" + str(number))

    def test_invalid_encoding_fails_before_the_parser(self):
        self.write(b"\xff")
        with self.assertRaisesRegex(state.ExpectedStateError, "valid UTF-8"):
            self.lint()
        self.assertEqual(0, self.parser.calls)

    def test_remote_reference_policy_validates_actual_resolved_uses(self):
        invalid = ["third/party@main", "third/party@v1", "third/party@" + "a" * 39,
                   "third/party/path/../target@" + PIN, "docker://alpine:3", "${{ inputs.action }}",
                   "Hexalith/Hexalith.Builds/Github/prepare@" + PIN, "hexalith/hexalith.builds@v1.2.3"]
        for number, value in enumerate(invalid):
            with self.subTest(value=value):
                self.reject(json.dumps({"runs": {"using": "composite", "steps": [{"uses": value}]}}),
                            "action-structure", name="remote-" + str(number))

    def test_local_targets_require_normalized_committed_regular_metadata(self):
        self.write(name="shared/nested", extension="yaml")
        self.write("runs: {using: composite, steps: [{uses: './.github/actions/shared/nested'}]}\n")
        self.lint()
        for number, value in enumerate(["./Github/prepare", "../.github/actions/shared/nested", ".github/actions/shared/nested",
                                        "./.github/actions/shared/../nested", "./.github/actions/shared//nested",
                                        "./.github/actions/shared/nested/", "./.github/actions/missing"]):
            with self.subTest(value=value):
                self.reject(json.dumps({"runs": {"using": "composite", "steps": [{"uses": value}]}}),
                            "action-structure", name="local-" + str(number))

    def test_symlink_or_gitlink_metadata_are_not_followed(self):
        target = self.repository.root.parent / "private.yml"
        target.write_text(VALID)
        path = self.write()
        metadata = self.repository.root / path
        metadata.unlink()
        metadata.symlink_to(target)
        with self.assertRaisesRegex(state.ExpectedStateError, "regular committed"):
            self.lint()
        metadata.unlink()
        self.repository.command("add", "--all")
        sha = self.repository.command("rev-parse", "HEAD")
        self.repository.command("update-index", "--add", "--cacheinfo", "160000", sha, path)
        self.repository.command("commit", "-qm", "test: metadata gitlink")
        with self.assertRaisesRegex(state.ExpectedStateError, "regular committed"):
            guard_actions.lint(state.tree_snapshot(state.Git(self.repository.root)), self.parser)
        self.assertEqual(0, self.parser.calls)

    def test_deny_list_recognizes_literal_primitives_case_insensitively(self):
        commands = ["DAPR init", "dotnet nuget push package.nupkg", "nuget push package.nupkg",
                    "nuget.exe push package.nupkg", "dapr.exe init",
                    "Docker login registry.example", "podman login registry.example", "helm login registry.example",
                    "oras login registry.example", "crane login registry.example", "skopeo login registry.example",
                    "az acr login --name example", "npx semantic-release", "semantic-release"]
        for number, command in enumerate(commands):
            with self.subTest(command=command):
                self.reject(json.dumps({"runs": {"using": "composite", "steps": [{"shell": "bash", "run": command}]}}),
                            "deny-list", name="deny-" + str(number))
        for number, action in enumerate(["actions/checkout", "Actions/Setup-Dotnet", "actions/cache",
                                         "docker/login-action", "azure/docker-login", "aws-actions/amazon-ecr-login"]):
            with self.subTest(action=action):
                self.reject(json.dumps({"runs": {"using": "composite", "steps": [{"uses": action + "@" + PIN}]}}),
                            "deny-list", name="deny-action-" + str(number))

    def test_solution_wide_commands_reject_absent_directory_and_solution_targets(self):
        commands = ["dotnet test", "dotnet build --configuration Release", "dotnet test ./tests", "dotnet test Foo.slnx",
                    "dotnet build Foo.sln", "dotnet test --logger impostor.csproj", "dotnet test --project .",
                    "dotnet test directory project.csproj", "dotnet test --project project.csproj directory",
                    "& dotnet test", 'dotnet "test"', '& "dotnet" "test"', "dotnet.exe test", "dotnet test -- --project Fake.csproj"]
        for number, command in enumerate(commands):
            with self.subTest(command=command):
                self.reject(json.dumps({"runs": {"using": "composite", "steps": [{"shell": "bash", "run": command}]}}),
                            "solution-wide", name="solution-" + str(number))

    def test_explicit_project_positional_switch_quotes_and_continuations_pass(self):
        commands = ['dotnet test "test/My Project.csproj" --no-build --logger trx',
                    "dotnet build src/My.csproj --configuration Release", "dotnet test --project test/Tests.csproj --no-restore",
                    "dotnet test --project=test/Tests.csproj --no-build", "dotnet test --configuration Release test/Tests.csproj",
                    "dotnet test \\\n  --project test/Tests.csproj --no-build", "dotnet test `\n  --project test/Tests.csproj --no-build",
                    '& dotnet "test" --project test/Tests.csproj', '& "dotnet" "test" test/Tests.csproj',
                    "dotnet.exe test test/Tests.csproj"]
        for number, command in enumerate(commands):
            self.write(json.dumps({"runs": {"using": "composite", "steps": [{"shell": "bash", "run": command}]}}),
                       name="explicit-" + str(number))
        self.lint()

    def test_metadata_comments_are_covered_by_the_solution_command_deny_scan(self):
        self.reject(VALID + "# dotnet build --configuration Release\n", "solution-wide", name="comment")

    def test_committed_snapshot_survives_staging_missing_worktree_and_sparse_checkout(self):
        path = self.write()
        self.repository.commit()
        snapshot = state.tree_snapshot(state.Git(self.repository.root))
        self.repository.write(path, "invalid dirty YAML")
        self.repository.command("add", "--all")
        (self.repository.root / path).unlink()
        index = (self.repository.root / ".git/index").read_bytes()
        guard_actions.lint(snapshot, self.parser)
        self.assertEqual(index, (self.repository.root / ".git/index").read_bytes())
        self.assertFalse((self.repository.root / path).exists())
        self.repository.command("read-tree", "HEAD")
        self.repository.command("sparse-checkout", "init", "--cone")
        self.repository.command("sparse-checkout", "set", "outside")
        guard_actions.lint(state.tree_snapshot(state.Git(self.repository.root)), self.parser)

    def test_malicious_path_controls_are_escaped_deterministically(self):
        self.write("runs: {using: node24}", name="bad\n::error::message\x1b[31m")
        self.repository.commit()
        snapshot = state.tree_snapshot(state.Git(self.repository.root))
        errors = []
        for _ in range(2):
            with self.assertRaises(state.ExpectedStateError) as raised:
                guard_actions.lint(snapshot, self.parser)
            errors.append(str(raised.exception))
        self.assertEqual(errors[0], errors[1])
        self.assertIn("bad\\u000a::error::message\\u001b[31m", errors[0])
        self.assertNotIn("\x1b", errors[0])


if __name__ == "__main__":
    unittest.main()
