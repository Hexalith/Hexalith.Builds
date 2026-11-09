"""Execute prepare composites, including real PowerShell, with command controls.

External checkout/setup actions and SDK executable doubles are labeled controls;
Windows runner evidence is provided separately by hosted CI.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

from test_ci_environment import CompositeHarness
import test_ci_pipeline as FIXTURE


ROOT = FIXTURE.ROOT
ACTION = ROOT / "Github/prepare"


class PrepareTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURE.SubprocessPipelineTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        self.external = []
        self.fail_external = ""
        self.inputs = {"solution": "Fixture.slnx", "global-json": "global.json", "submodules": "[]"}
        self.environment = self.fixture.environment.copy()
        self.environment.pop("CI_STAGE_STATE", None)
        self.save()

    def save(self):
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.fixture.document))
        (self.root / "fake-sdk.json").write_text(json.dumps(self.fixture.data))

    def external_control(self, reference, inputs, environment):
        """Observe parameters at checkout/setup boundaries, never simulate hosting."""
        self.external.append((reference, inputs))
        if reference.startswith("actions/checkout@"):
            self.assertIs(inputs["submodules"].lower() == "false", True)
            self.assertEqual("false", inputs["persist-credentials"].lower())
        elif reference.startswith("actions/setup-dotnet@"):
            self.assertEqual(self.inputs["global-json"], inputs["global-json-file"])
            self.assertTrue((self.root / inputs["global-json-file"]).is_file())
        else:
            self.fail(f"unexpected external action: {reference}")
        return 47 if reference.startswith(self.fail_external) and self.fail_external else 0

    def invoke(self, os_name="Windows", *, inputs=None):
        self.save()
        return CompositeHarness(self.root, self.environment, runner_os=os_name,
                                external_action=self.external_control).run_action(
                                    ACTION, self.inputs if inputs is None else inputs)

    def commands(self):
        return self.fixture.commands()

    def test_windows_release_flags_one_restore_no_tests_or_environment_or_config_read(self):
        # Invalid module JSON is deliberately unreadable to a config loader.
        self.save = lambda: None
        (self.root / ".github/hexalith-ci.json").write_text("not JSON")
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertEqual(["restore", "build"], [command[0] for command in commands])
        for command in commands:
            offset = 3 if command[0] == "build" else 2
            self.assertEqual(FIXTURE.PIPE.build_flags(), command[offset:])
            for flag in (*FIXTURE.EXPECTED_PROPERTIES, *FIXTURE.EXPECTED_ENGINE_FLAGS):
                self.assertIn(flag, command)
        self.assertIn("--no-restore", commands[1])
        self.assertEqual(2, len(self.external))

    def test_windows_prefixed_nested_solution_with_spaces_keeps_sdk_directory(self):
        module = self.root / "Samples/A sample"
        (module / "solutions/nested").mkdir(parents=True)
        (module / "solutions/nested/A sample.slnx").write_text("<Solution />")
        for name in ("global.json", "fake-sdk.json"):
            shutil.copy(self.root / name, module / name)
        self.inputs.update(solution="Samples/A sample/solutions/nested/A sample.slnx",
                           **{"global-json": "Samples/A sample/global.json"})
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = [json.loads(line) for line in (module / "commands.jsonl").read_text().splitlines()]
        self.assertEqual("solutions/nested/A sample.slnx", commands[0][1])
        self.assertTrue((module / "build-completed").is_file())
        self.assertEqual([], self.commands())

    def test_windows_unsafe_missing_or_outside_paths_fail_before_sdk_and_product(self):
        for key, value in (("solution", "../outside.slnx"), ("solution", "/tmp/a.slnx"),
                           ("solution", "C:/a.slnx"), ("solution", "a\\b.slnx"),
                           ("solution", "bad\nname.slnx"), ("solution", "missing.slnx"),
                           ("solution", "global.json"), ("global-json", "../global.json"),
                           ("global-json", "Fixture.slnx"), ("global-json", "missing/global.json")):
            with self.subTest(key=key, value=value):
                self.external.clear()
                result = self.invoke(inputs=dict(self.inputs, **{key: value}))
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(1, len(self.external))
                self.assertEqual([], self.commands())
        (self.root / "another").mkdir()
        shutil.copy(self.root / "global.json", self.root / "another/global.json")
        result = self.invoke(inputs=dict(self.inputs, **{"global-json": "another/global.json"}))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.commands())

    def test_windows_rejects_symlink_paths_before_sdk(self):
        (self.root / "link").symlink_to(self.root, target_is_directory=True)
        result = self.invoke(inputs=dict(self.inputs, solution="link/Fixture.slnx"))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.commands())

    def test_windows_none_skips_sdk_submodules_and_product(self):
        (self.root / "global.json").unlink()
        result = self.invoke(inputs={"solution": "none", "submodules": "[]"})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.commands())
        self.assertEqual(1, len(self.external))

    def test_windows_omitted_solution_fails_before_sdk_or_product(self):
        result = self.invoke(inputs={})
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Unsafe solution path", result.stderr)
        self.assertEqual([], self.commands())
        self.assertEqual(1, len(self.external))

    def test_external_checkout_and_sdk_failure_prevent_all_product_commands(self):
        for boundary, count in (("actions/checkout@", 1), ("actions/setup-dotnet@", 2)):
            with self.subTest(boundary=boundary):
                self.external.clear()
                self.fail_external = boundary
                result = self.invoke()
                self.assertEqual(47, result.returncode, result.stdout + result.stderr)
                self.assertEqual(count, len(self.external))
                self.assertEqual([], self.commands())

    def test_windows_restore_and_build_failures_preserve_status_without_successor(self):
        for command in ("restore", "build"):
            with self.subTest(command=command):
                (self.root / "commands.jsonl").unlink(missing_ok=True)
                self.fixture.data["failure"] = command
                result = self.invoke()
                self.assertEqual(17, result.returncode, result.stdout + result.stderr)
                self.assertEqual(["restore"] if command == "restore" else ["restore", "build"],
                                 [args[0] for args in self.commands()])

    def git(self, *arguments):
        result = subprocess.run(["git", *arguments], cwd=self.root, capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result.stdout.strip()

    def submodule_controls(self):
        """Real root declarations/index, with update replaced by an effect double."""
        self.git("init", "-q")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Prepare fixture")
        (self.root / "seed").write_text("seed")
        self.git("add", "seed")
        self.git("commit", "-qm", "fixture")
        revision = self.git("rev-parse", "HEAD")
        for index, path in enumerate(("references/A space", "references/B", "references/Unrequested")):
            self.git("config", "--file", ".gitmodules", f"submodule.module{index}.path", path)
            self.git("config", "--file", ".gitmodules", f"submodule.module{index}.url", "https://example.invalid/repo.git")
            self.git("update-index", "--add", "--cacheinfo", f"160000,{revision},{path}")
        real_git = shutil.which("git")
        executable = self.fixture.bin / "git"
        executable.write_text(f"#!{sys.executable}\n" + f"REAL_GIT={real_git!r}\n" + r'''
import json, os, pathlib, subprocess, sys
args=sys.argv[1:]
if "update" in args and "submodule" in args:
    pathlib.Path("submodules-initialized.json").write_text(json.dumps(args))
    sys.exit(int(os.environ.get("FAIL_SUBMODULE_UPDATE", "0")))
os.execv(REAL_GIT, [REAL_GIT, *args])
''')
        executable.chmod(0o755)

    def test_windows_lf_decoding_selects_only_exact_root_gitlinks(self):
        self.submodule_controls()
        self.inputs["submodules"] = json.dumps(["references/A space", "references/B"])
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        arguments = json.loads((self.root / "submodules-initialized.json").read_text())
        self.assertEqual(["references/A space", "references/B"], arguments[arguments.index("--") + 1:])
        self.assertIn("submodule.recurse=false", arguments)
        self.assertIn("protocol.file.allow=never", arguments)
        self.assertNotIn("--recursive", arguments)
        self.assertNotIn("--remote", arguments)

    def test_windows_invalid_submodules_fail_before_sdk_or_update(self):
        self.submodule_controls()
        (self.root / "ordinary").write_text("ordinary")
        self.git("add", "ordinary")
        self.git("config", "--file", ".gitmodules", "submodule.ordinary.path", "ordinary")
        for paths in (["references/Missing"], ["ordinary"], ["references"],
                      ["../outside"], ["references/A space", "references/A space"], ["references/B\nordinary"]):
            with self.subTest(paths=paths):
                self.external.clear()
                result = self.invoke(inputs=dict(self.inputs, submodules=json.dumps(paths)))
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual([], self.commands())
                self.assertEqual(1, len(self.external))
                self.assertFalse((self.root / "submodules-initialized.json").exists())

    def test_windows_submodule_failure_preserves_status_before_restore(self):
        self.submodule_controls()
        self.environment["FAIL_SUBMODULE_UPDATE"] = "53"
        result = self.invoke(inputs=dict(self.inputs, submodules='["references/B"]'))
        self.assertEqual(53, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.commands())

    def test_windows_case_colliding_destinations_fail_before_sdk_or_update(self):
        self.submodule_controls()
        revision = self.git("rev-parse", "HEAD")
        for name, path in (("Upper", "references/A"), ("Lower", "references/a")):
            self.git("config", "--file", ".gitmodules", f"submodule.{name}.path", path)
            self.git("update-index", "--add", "--cacheinfo", f"160000,{revision},{path}")
        result = self.invoke(inputs=dict(self.inputs, submodules='["references/A","references/a"]'))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Duplicate direct submodule paths", result.stderr)
        self.assertEqual([], self.commands())
        self.assertEqual(1, len(self.external))
        self.assertFalse((self.root / "submodules-initialized.json").exists())

    def test_windows_exact_gitlink_names_remain_case_sensitive(self):
        self.submodule_controls()
        result = self.invoke(inputs=dict(self.inputs, submodules='["references/b"]'))
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Submodule is not declared in root .gitmodules: references/b", result.stderr)
        self.assertEqual([], self.commands())
        self.assertEqual(1, len(self.external))

    def test_linux_reuses_loader_build_and_environment_without_tests(self):
        result = self.invoke("Linux", inputs={})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertEqual(1, sum(args[0] == "restore" for args in commands))
        self.assertEqual(1, sum(args[0] == "build" for args in commands))
        self.assertFalse(any(args[0] == "test" for args in commands))
        self.assertTrue((self.root / "build-completed").exists())

    def test_linux_entrypoint_initializes_recorded_direct_gitlinks_before_build(self):
        fixture = FIXTURE.ClassifierAndSubmoduleTests()
        self.addCleanup(fixture.doCleanups)

        def prepare(consumer, environment):
            environment.update(CI_REPOSITORY="Example/Consumer", GITHUB_WORKSPACE=str(consumer),
                               PATH=str(self.fixture.bin) + os.pathsep + environment["PATH"])
            (consumer / "fake-sdk.json").write_text(json.dumps(self.fixture.data))
            for project in self.fixture.data["projects"]:
                target = consumer / project
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("<Project />")
            executable = self.fixture.bin / "dotnet"
            # Check the initialized contents at every product command boundary,
            # not merely after both entrypoint phases have already completed.
            executable.write_text(f"#!{sys.executable}\n" + r'''
from pathlib import Path
for path in ("references/Hexalith.Builds", "references/Extra dependency"):
    checkout=Path(path)
    assert (checkout / ".git").is_file()
    assert (checkout / "revision").read_text()=="recorded commit"
    assert not (checkout / "references/Nested/.git").exists()
    assert not (checkout / "references/Nested/revision").exists()
assert not Path("references/Unused/.git").exists()
assert not Path("references/Unused/revision").exists()
''' + FIXTURE.FAKE_DOTNET)
            executable.chmod(0o755)
            prepared = subprocess.run([sys.executable, "-I", "-S", str(ACTION / "prepare_linux.py"),
                                       "--phase", "prepare"], env=environment,
                                      text=True, capture_output=True, check=False)
            self.assertEqual(0, prepared.returncode, prepared.stdout + prepared.stderr)
            self.assertFalse((consumer / "commands.jsonl").exists())
            built = subprocess.run([sys.executable, "-I", "-S", str(ACTION / "prepare_linux.py"),
                                    "--phase", "build"], env=environment,
                                   text=True, capture_output=True, check=False)
            self.assertEqual(0, built.returncode, built.stdout + built.stderr)
            commands = [json.loads(line) for line in (consumer / "commands.jsonl").read_text().splitlines()]
            self.assertEqual(["restore", "build"], [command[0] for command in commands[:2]])
            self.assertTrue((consumer / "build-completed").is_file())
            self.assertFalse(any(command[0] == "test" for command in commands))
            return prepared

        fixture.check_recorded_consumer_gitlinks(prepare)

    def test_linux_invalid_config_and_build_failure_stop_before_environment(self):
        self.fixture.document["dapr"] = "false"
        result = self.invoke("Linux")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.commands())
        self.assertEqual(1, len(self.external))
        self.fixture.document["dapr"] = True
        self.fixture.data["failure"] = "build"
        result = self.invoke("Linux")
        self.assertEqual(17, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["restore", "build"], [args[0] for args in self.commands()])

    def test_linux_none_still_validates_without_sdk_or_environment(self):
        self.fixture.document["solution"] = "none"
        (self.root / "global.json").unlink()
        result = self.invoke("Linux")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.commands())
        self.assertEqual(1, len(self.external))
        self.fixture.document["hooks"] = ["after-unit-tests"]
        result = self.invoke("Linux")
        self.assertNotEqual(0, result.returncode)

    def test_linux_environment_inference_setup_once_after_build_no_hooks(self):
        self.fixture.data["projects"]["test/Fixture.E2E.Tests.csproj"] = True
        (self.root / "test/Fixture.E2E.Tests.csproj").write_text("<Project />")
        self.fixture.document["hooks"] = ["after-unit-tests"]
        self.fixture.document["registry"] = True
        output = self.root / "test/A space/bin/Release/net10.0"
        (output / ".playwright/package").mkdir(parents=True)
        (output / "playwright.ps1").write_text("# tooling")
        (output / "Microsoft.Playwright.dll").touch()
        (output / ".playwright/package/package.json").write_text('{"version":"1.64.1"}')
        for name in ("pwsh", "docker"):
            command = self.fixture.bin / name
            command.write_text(f"#!{sys.executable}\n" + r'''
import pathlib, sys
root=pathlib.Path.cwd()
assert (root / "build-completed").read_text()=="Release"
with (root / "setup-effects").open("a") as stream:
    stream.write(pathlib.Path(sys.argv[0]).name+"\n")
''')
            command.chmod(0o755)
        self.environment.update(REGISTRY_PULL_USERNAME="pull-only", REGISTRY_PULL_PASSWORD="synthetic")
        result = self.invoke("Linux")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["pwsh", "docker"], (self.root / "setup-effects").read_text().splitlines())
        self.assertFalse(any(args[0] == "test" for args in self.commands()))

    def test_root_sample_actions_execute_built_tests_and_propagate_failure(self):
        module = self.root / "Samples/Hexalith.Sample"
        module.mkdir(parents=True)
        executable = self.fixture.bin / "dotnet"
        executable.write_text(f"#!{sys.executable}\n" + r'''
import json, os, pathlib, sys
args=sys.argv[1:]
assert args[:2]==["test", "--project"]
project=pathlib.Path(args[2])
assert project.is_file()
assert (project.parent / "bin/Release/net10.0" / (project.stem+".dll")).is_file()
assert args[3:]==["--no-build", "--no-restore", "--configuration", "Release", "--stop-on-fail", "off"]
with pathlib.Path("delegated-tests.jsonl").open("a") as stream:
    stream.write(json.dumps(args)+"\n")
sys.exit(int(os.environ.get("FAIL_DELEGATED_TEST", "0")))
''')
        executable.chmod(0o755)
        for name, lane in (("after-unit-tests", "HookTests"), ("sample-windows", "WindowsTests")):
            with self.subTest(action=name):
                assembly = module / f"test/Hexalith.Sample.{lane}/bin/Release/net10.0/Hexalith.Sample.{lane}.dll"
                assembly.parent.mkdir(parents=True)
                assembly.touch()
                (module / f"test/Hexalith.Sample.{lane}/Hexalith.Sample.{lane}.csproj").touch()
                target = self.root / f".github/actions/{name}"
                shutil.copytree(ROOT / f".github/actions/{name}", target)
                for code in (0, 61):
                    environment = dict(self.environment, FAIL_DELEGATED_TEST=str(code))
                    harness = CompositeHarness(self.root, environment)
                    result = harness.steps([
                        {"uses": f"./.github/actions/{name}"},
                        {"shell": "bash", "run": "touch successor"},
                    ], harness.context, harness.environment)
                    self.assertEqual(code, result.returncode, result.stdout + result.stderr)
                    self.assertEqual(code == 0, (self.root / "successor").exists())
                    (self.root / "successor").unlink(missing_ok=True)
                observed = [json.loads(line) for line in (module / "delegated-tests.jsonl").read_text().splitlines()]
                self.assertEqual(2, len(observed))
                self.assertTrue(all(Path(args[2]).stem == assembly.stem for args in observed))
                (module / "delegated-tests.jsonl").unlink()


if __name__ == "__main__":
    unittest.main()
