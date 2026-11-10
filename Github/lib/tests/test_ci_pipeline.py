"""Behavioral coverage for shared CI; no workflow-source assertions."""

from __future__ import annotations

import importlib.util
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "Github/lib/ci_pipeline.py"
SPEC = importlib.util.spec_from_file_location("ci_pipeline", SCRIPT)
PIPE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = PIPE
SPEC.loader.exec_module(PIPE)
BUILDS = "Hexalith/Hexalith.Builds"
# Deliberately independent of the implementation's flag factories: removing a
# mandatory property or switch must fail this contract.
EXPECTED_PROPERTIES = ("-p:Configuration=Release", "-p:UseHexalithProjectReferences=false",
                       "-p:NuGetAudit=false", f"-p:RestoreConfigFile={ROOT / 'NuGet.ci.Config'}",
                       "-p:EnableNETAnalyzers=true", "-p:RunAnalyzers=true", "-p:RunAnalyzersDuringBuild=true")
EXPECTED_ENGINE_FLAGS = ("-noAutoResponse", "-warnaserror")
EXPECTED_MTP_FLAGS = ("-p:TreatWarningsAsErrors=true", "-p:MSBuildTreatWarningsAsErrors=true")


def configuration(solution="Fixture.slnx"):
    return {"schema": "hexalith.ci.v1", "solution": solution, "dapr": False,
            "browsers": False, "registry": False, "codeql": [],
            "tests": {"integration": [], "exclude": {}}, "hooks": [], "jobs": {}, "submodules": []}


FAKE_DOTNET = r'''
import json, os, pathlib, sys
root=pathlib.Path.cwd()
data=json.loads((root / "fake-sdk.json").read_text())
args=sys.argv[1:]
with (root / "commands.jsonl").open("a") as log:
    log.write(json.dumps(args)+"\n")
command=args[0]
if data.get("failure")==command:
    print("fake-sdk-stdout-marker")
    print("fake-sdk-stderr-marker", file=sys.stderr)
    sys.exit(17)
if command=="sln":
    if data.get("broken_list"):
        print("unsupported discovery output")
    else:
        print("Project(s)\n----------")
        print("\n".join(data["projects"]))
elif command=="msbuild":
    if data.get("broken_metadata"):
        print('{"Properties":{"IsTestProject":"maybe"}}')
    else:
        project=args[1]
        inner=next((arg.split("=",1)[1] for arg in args if arg.startswith("-p:TargetFramework=")), "")
        values={"IsTestProject":str(data["projects"][project]).lower(), "TargetFramework":"net10.0",
                "TargetFrameworks":"", "IsCrossTargetingBuild":"", "XunitTestProject":str(data.get("xunit",True)).lower(),
                "TargetPath":str(root / pathlib.Path(project).parent / "bin/Release/net10.0" / (pathlib.Path(project).stem+".dll"))}
        if data.get("cross") and project.endswith("A.Tests.csproj"):
            values.update(TargetFrameworks=data.get("frameworks", "net10.0;net9.0"), IsCrossTargetingBuild="true")
            if inner:
                values["IsCrossTargetingBuild"]="false"
                values["TargetFramework"]=inner
                values["TargetPath"]=str(root / pathlib.Path(project).parent / "bin/Release" / inner / (pathlib.Path(project).stem+".dll"))
                if data.get("inconsistent") and inner=="net9.0":
                    values["IsTestProject"]="false"
                if data.get("outer_only"):
                    values["IsTestProject"]="false"
            else:
                # Like SDK 10/xunit.v3: package imports apply only to inner builds.
                values["XunitTestProject"]=""
        values.update(data.get("metadata",{}).get(project,{}))
        values.update(data.get("framework_metadata",{}).get(project,{}).get(inner,{}))
        print(json.dumps({"Properties":values}))
elif command=="test":
    project=args[2] if args[1]=="--project" else args[1]
    with (root / "tests-completed").open("a") as results:
        results.write(project+":failing-test-completed\n")
        results.write(project+":passing-test-completed\n")
    if data.get("fail_project")==project:
        sys.exit(23)
elif command=="build":
    (root / "build-completed").write_text("Release")
'''


class SubprocessPipelineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ci-pipeline-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.document = configuration()
        self.data = {"projects": {"test/Z.Tests/Z.Tests.csproj": True,
                                  "src/Ordinary.csproj": False,
                                  "test/A space/A.Tests.csproj": True,
                                  "test/Fixture.IntegrationTests.csproj": True}}
        self.bin = self.root / "sdk"
        self.bin.mkdir()
        fake = self.bin / "dotnet"
        fake.write_text(f"#!{sys.executable}\n" + FAKE_DOTNET, encoding="utf-8")
        fake.chmod(0o755)
        for project in self.data["projects"]:
            path = self.root / project
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("<Project />", encoding="utf-8")
        (self.root / "Fixture.slnx").write_text("<Solution />", encoding="utf-8")
        (self.root / "global.json").write_text(json.dumps({"sdk": {"version": "10.0.403", "rollForward": "latestPatch"},
                                                           "test": {"runner": "Microsoft.Testing.Platform"}}))
        (self.root / ".github").mkdir()
        self.environment = os.environ.copy()
        self.environment["PATH"] = str(self.bin) + os.pathsep + self.environment.get("PATH", "")
        self.environment.pop("GITHUB_OUTPUT", None)
        self.environment.pop("CI_MODULE_ROOT", None)
        self.environment.pop("CI_EXPECTED_SOURCE", None)

    def invoke(self, *arguments, isolation="-I"):
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.document))
        (self.root / "fake-sdk.json").write_text(json.dumps(self.data))
        return subprocess.run([sys.executable, isolation, "-S", str(SCRIPT), "--workspace", str(self.root),
                               "--repository", BUILDS, *arguments], env=self.environment,
                              text=True, capture_output=True, check=False)

    def commands(self):
        path = self.root / "commands.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def test_one_restore_release_package_flags_classification_and_order(self):
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertEqual(["restore", "build"], [command[0] for command in commands[:2]])
        self.assertEqual(1, sum(command[0] == "restore" for command in commands))
        tests = [command for command in commands if command[0] == "test"]
        self.assertEqual(["test/A space/A.Tests.csproj", "test/Z.Tests/Z.Tests.csproj"],
                         [command[2] for command in tests])
        for command in commands:
            if command[0] == "sln":
                continue
            extra = EXPECTED_MTP_FLAGS if command[0] == "test" else EXPECTED_ENGINE_FLAGS
            for flag in (*EXPECTED_PROPERTIES, *extra):
                self.assertIn(flag, command)
            self.assertNotIn("--fail-fast", command)
        for command in tests:
            self.assertIn("--no-build", command)
            self.assertIn("--no-restore", command)
            self.assertNotIn("-noAutoResponse", command)
            self.assertNotIn("-warnaserror", command)
            self.assertEqual(["--stop-on-fail", "off"], command[-2:])
        self.assertIn("--no-restore", commands[1])
        self.assertIn('"integration": ["test/Fixture.IntegrationTests.csproj"]', result.stdout)

    def test_failure_completes_the_project_and_preserves_status_without_starting_later_project(self):
        self.data["fail_project"] = "test/A space/A.Tests.csproj"
        result = self.invoke()
        self.assertEqual(23, result.returncode, result.stdout + result.stderr)
        completed = (self.root / "tests-completed").read_text().splitlines()
        self.assertEqual(["test/A space/A.Tests.csproj:failing-test-completed",
                          "test/A space/A.Tests.csproj:passing-test-completed"], completed)
        self.assertEqual(1, sum(command[0] == "test" for command in self.commands()))

    def test_restore_and_build_failures_stop_before_discovery(self):
        for failing_command in ("restore", "build"):
            with self.subTest(command=failing_command):
                (self.root / "commands.jsonl").unlink(missing_ok=True)
                self.data["failure"] = failing_command
                result = self.invoke()
                self.assertEqual(17, result.returncode, result.stderr)
                self.assertIn(f"dotnet {failing_command} Fixture.slnx ", result.stderr)
                self.assertEqual(["restore"] if failing_command == "restore" else ["restore", "build"],
                                 [command[0] for command in self.commands()])

    def test_failed_discovery_commands_report_their_captured_output(self):
        for failing_command in ("sln", "msbuild"):
            with self.subTest(command=failing_command):
                (self.root / "commands.jsonl").unlink(missing_ok=True)
                self.data["failure"] = failing_command
                result = self.invoke()
                self.assertEqual(17, result.returncode, result.stdout + result.stderr)
                self.assertIn("fake-sdk-stdout-marker", result.stderr)
                self.assertIn("fake-sdk-stderr-marker", result.stderr)
                self.assertIn(f"dotnet {failing_command} ", result.stderr)
                self.assertFalse(any(command[0] == "test" for command in self.commands()))

    def test_composite_phase_sequence_restores_once_and_tests_only_in_unit(self):
        phases = {}
        for phase in ("prepare", "build", "unit"):
            before = len(self.commands())
            result = self.invoke("--phase", phase)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            phases[phase] = [command[0] for command in self.commands()[before:]]
        self.assertEqual([], phases["prepare"])
        self.assertEqual(["restore", "build"], phases["build"])
        self.assertNotIn("restore", phases["unit"])
        self.assertNotIn("build", phases["unit"])
        self.assertEqual(2, phases["unit"].count("test"))
        self.assertEqual(1, phases["unit"].count("sln"))
    def test_no_solution_does_not_invoke_sdk_or_require_global_json(self):
        self.document["solution"] = "none"
        (self.root / "global.json").unlink()
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], self.commands())
        self.document["unknown"] = True
        result = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertIn("unknown keys", result.stderr)
        self.assertEqual([], self.commands())

    def test_vstest_is_positional_and_missing_runner_defaults_to_vstest(self):
        (self.root / "global.json").write_text(json.dumps({"sdk": {"version": "10.0.403"}}))
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        for command in self.commands():
            if command[0] == "test":
                self.assertTrue(command[1].endswith(".csproj"))
                self.assertNotIn("--project", command)
                for flag in (*EXPECTED_PROPERTIES, *EXPECTED_ENGINE_FLAGS):
                    self.assertIn(flag, command)
                self.assertEqual(["--", "xUnit.StopOnFail=false"], command[-2:])

    def test_other_frameworks_receive_no_xunit_specific_arguments(self):
        self.data["xunit"] = False
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        for command in self.commands():
            if command[0] == "test":
                self.assertNotIn("--stop-on-fail", command)
                self.assertNotIn("xUnit.StopOnFail=false", command)

    def test_unknown_overrides_and_disabled_exclusions_fail_without_tests(self):
        self.document["tests"]["integration"] = ["test/Unknown.csproj"]
        result = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertIn("unknown test projects", result.stderr)
        self.assertFalse(any(command[0] == "test" for command in self.commands()))
        self.document["tests"]["integration"] = []
        self.document["tests"]["exclude"] = {"test/Z.Tests/Z.Tests.csproj": "windows-tests"}
        result = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertIn("not an enabled hook or job", result.stderr)

    def test_integration_override_and_enabled_exclusion_are_the_only_overrides(self):
        self.document["tests"]["integration"] = ["test/Z.Tests/Z.Tests.csproj"]
        self.document["tests"]["exclude"] = {"test/A space/A.Tests.csproj": "after-unit-tests"}
        self.document["hooks"] = ["after-unit-tests"]
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any(command[0] == "test" for command in self.commands()))
        classification = next(json.loads(line) for line in result.stdout.splitlines() if line.startswith('{"unit"'))
        self.assertEqual({"unit": [], "integration": ["test/Fixture.IntegrationTests.csproj", "test/Z.Tests/Z.Tests.csproj"],
                          "excluded": ["test/A space/A.Tests.csproj"]}, classification)
        # An exclusion takes precedence over an integration-stage name.
        document = configuration()
        document.update(hooks=["after-unit-tests"],
                        tests={"integration": [], "exclude": {"test/A.IntegrationTests.csproj": "after-unit-tests"}})
        result = PIPE.classify_projects(("test/A.IntegrationTests.csproj", "test/B.Tests.csproj"), document,
                                        repository=BUILDS)
        self.assertEqual(PIPE.TestProjects(("test/B.Tests.csproj",), (), ("test/A.IntegrationTests.csproj",)), result)

    def test_broken_sdk_discovery_and_metadata_fail_closed(self):
        for option in ("broken_list", "broken_metadata"):
            with self.subTest(option=option):
                self.data[option] = True
                result = self.invoke()
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertNotIn("Traceback", result.stderr)
                self.assertFalse(any(command[0] == "test" for command in self.commands()))
                del self.data[option]

    def test_cross_targeting_evaluates_each_inner_build_and_rejects_disagreement(self):
        self.data["cross"] = True
        # MSBuild preserves property whitespace; the SDK trims and drops empty entries.
        for frameworks in ("net10.0;net9.0", "\n    net10.0;\n    net9.0;\n  "):
            with self.subTest(frameworks=frameworks):
                (self.root / "commands.jsonl").unlink(missing_ok=True)
                self.data["frameworks"] = frameworks
                result = self.invoke()
                self.assertEqual(0, result.returncode, result.stderr)
                evaluations = [command for command in self.commands() if command[0] == "msbuild"]
                inner = {argument for command in evaluations for argument in command
                         if argument.startswith("-p:TargetFramework=")}
                self.assertEqual({"-p:TargetFramework=net10.0", "-p:TargetFramework=net9.0"}, inner)
                # xUnit identity comes from the inner builds, so completion still holds.
                tests = {command[2]: command for command in self.commands() if command[0] == "test"}
                self.assertEqual(["--stop-on-fail", "off"], tests["test/A space/A.Tests.csproj"][-2:])
        self.data["inconsistent"] = True
        result = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertIn("differs across target frameworks", result.stderr)

    def test_outer_and_inner_test_identity_disagreement_fails_without_tests(self):
        self.data.update(cross=True, outer_only=True)
        result = self.invoke()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("outer and inner IsTestProject disagree", result.stderr)
        self.assertFalse(any(command[0] == "test" for command in self.commands()))

    def test_unit_execution_order_is_ordinal_not_case_insensitive(self):
        self.data["projects"] = {"test/b.Tests/b.Tests.csproj": True, "test/C.Tests/C.Tests.csproj": True}
        for project in self.data["projects"]:
            (self.root / project).parent.mkdir(parents=True, exist_ok=True)
            (self.root / project).write_text("<Project />", encoding="utf-8")
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["test/C.Tests/C.Tests.csproj", "test/b.Tests/b.Tests.csproj"],
                         [command[2] for command in self.commands() if command[0] == "test"])

    def test_prepare_publishes_validated_workflow_outputs(self):
        output = self.root / "github-output"
        self.environment["GITHUB_OUTPUT"] = str(output)
        result = self.invoke("--phase", "prepare")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["solution=Fixture.slnx", "global-json=global.json", "submodules=[]"],
                         output.read_text().splitlines())
        output.unlink()
        self.document["solution"] = "none"
        result = self.invoke("--phase", "prepare")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["solution=none", "global-json=", "submodules=[]"], output.read_text().splitlines())
        self.assertEqual([], self.commands())

    def test_unit_outputs_are_published_only_on_success_and_include_e2e_inference(self):
        output = self.root / "github-output"
        self.environment["GITHUB_OUTPUT"] = str(output)
        self.document.update(dapr=True, registry=True, hooks=["after-unit-tests", "nightly"])
        project = "test/Browser.E2E.csproj"
        self.data["projects"][project] = True
        (self.root / project).write_text("<Project />")
        result = self.invoke("--phase", "unit")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual({"dapr": "true", "registry": "true", "browsers": "true",
                          "after-unit-tests": "true", "after-integration-tests": "false"},
                         dict(line.split("=", 1) for line in output.read_text().splitlines()))
        output.unlink()
        self.data["fail_project"] = "test/A space/A.Tests.csproj"
        result = self.invoke("--phase", "unit")
        self.assertEqual(23, result.returncode, result.stderr)
        self.assertFalse(output.exists())

    def test_integration_phase_orders_projects_and_preserves_first_failure(self):
        self.document["tests"]["integration"] = ["test/A space/A.Tests.csproj", "test/Z.Tests/Z.Tests.csproj"]
        self.data["fail_project"] = "test/Fixture.IntegrationTests.csproj"
        result = self.invoke("--phase", "integration")
        self.assertEqual(23, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertFalse(any(command[0] in ("restore", "build") for command in commands))
        tests = [command for command in commands if command[0] == "test"]
        self.assertEqual(["test/A space/A.Tests.csproj", "test/Fixture.IntegrationTests.csproj"],
                         [command[2] for command in tests])
        self.assertEqual(4, len((self.root / "tests-completed").read_text().splitlines()))
        self.assertTrue(all("--no-build" in command and "--no-restore" in command for command in tests))

    def test_no_solution_integration_and_stage_outputs_require_no_sdk(self):
        self.document.update(solution="none", registry=True)
        (self.root / "global.json").unlink()
        output = self.root / "github-output"
        self.environment["GITHUB_OUTPUT"] = str(output)
        for phase in ("unit", "integration"):
            result = self.invoke("--phase", phase)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue(all(line.endswith("=false") for line in output.read_text().splitlines()))
        self.assertEqual([], self.commands())

    def test_builds_module_root_is_workspace_relative_without_builds_gitlink(self):
        fixture = self.root / "Samples/Fixture"
        (fixture / ".github").mkdir(parents=True)
        document = configuration()
        document.update(buildsTag="v1.0.0", submodules=["references/Extra"])
        (fixture / ".github/hexalith-ci.json").write_text(json.dumps(document))
        (fixture / "Fixture.slnx").write_text("<Solution />")
        (fixture / "global.json").write_bytes((self.root / "global.json").read_bytes())
        (self.root / "Samples/Expected").mkdir()
        prepared = PIPE.prepare_config(self.root, repository=BUILDS, module_root="Samples/Fixture",
                                       expected_source="Samples/Expected")
        self.assertEqual(fixture.resolve(), prepared.root)
        self.assertEqual("Samples/Fixture/Fixture.slnx", prepared.solution)
        self.assertEqual("Samples/Fixture/global.json", prepared.global_json)
        self.assertEqual(("Samples/Fixture/references/Extra",), prepared.submodules)
        with self.assertRaisesRegex(PIPE.PipelineError, "requires module-root"):
            PIPE.prepare_config(self.root, repository=BUILDS, expected_source="Samples/Expected")
        with self.assertRaisesRegex(PIPE.PipelineError, "expected-source does not exist"):
            PIPE.prepare_config(self.root, repository=BUILDS, module_root="Samples/Fixture",
                                expected_source="Samples/Missing")

    def test_workflow_environment_supplies_repository_and_test_only_context(self):
        fixture = self.root / "Samples/Fixture"
        (fixture / ".github").mkdir(parents=True)
        document = configuration()
        document["buildsTag"] = "v1.0.0"
        (fixture / ".github/hexalith-ci.json").write_text(json.dumps(document))
        (fixture / "Fixture.slnx").write_text("<Solution />")
        (fixture / "global.json").write_bytes((self.root / "global.json").read_bytes())
        (self.root / "Samples/Expected").mkdir()
        output = self.root / "github-output"
        environment = dict(self.environment, GITHUB_OUTPUT=str(output), CI_REPOSITORY=BUILDS,
                           CI_MODULE_ROOT="Samples/Fixture", CI_EXPECTED_SOURCE="Samples/Expected")

        def prepare():
            return subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), "--workspace", str(self.root),
                                   "--phase", "prepare"], env=environment, text=True,
                                  capture_output=True, check=False)

        result = prepare()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["solution=Samples/Fixture/Fixture.slnx", "global-json=Samples/Fixture/global.json",
                          "submodules=[]"], output.read_text().splitlines())
        environment["CI_REPOSITORY"] = "Example/Module"
        result = prepare()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("test-only", result.stderr)
        self.assertEqual([], self.commands())

    def test_unsafe_paths_and_symlink_escapes_fail_closed(self):
        for value in ("", "/absolute.slnx", "a\\b.slnx", "C:drive.slnx", "a/../b.slnx", "./a.slnx",
                      "a//b.slnx", "${{ github.token }}.slnx", "a\nb.slnx", "a\x85b.slnx"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(PIPE.PipelineError, "unsafe solution path"):
                    PIPE.contained_path(self.root, value, kind="solution")
        outside = tempfile.TemporaryDirectory(prefix="ci-outside-")
        self.addCleanup(outside.cleanup)
        (Path(outside.name) / "Escape.slnx").write_text("<Solution />")
        (Path(outside.name) / "Out.Tests.csproj").write_text("<Project />")
        (self.root / "Escape.slnx").symlink_to(Path(outside.name) / "Escape.slnx")
        with self.assertRaisesRegex(PIPE.PipelineError, "escapes its repository"):
            PIPE.contained_path(self.root, "Escape.slnx", kind="solution")
        (self.root / "linked").symlink_to(outside.name, target_is_directory=True)
        self.data["projects"]["linked/Out.Tests.csproj"] = True
        result = self.invoke()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("unsupported or missing solution project", result.stderr)
        self.assertFalse(any(command[0] == "test" for command in self.commands()))

    def test_runner_metadata_and_test_only_inputs_fail_before_product_commands(self):
        for value in ({"runner": "unsupported"}, [], {"runner": False}):
            with self.subTest(value=value):
                (self.root / "global.json").write_text(json.dumps({"sdk": {"version": "10.0.403"}, "test": value}))
                result = self.invoke()
                self.assertEqual(1, result.returncode)
                self.assertEqual([], self.commands())
        with self.assertRaisesRegex(PIPE.PipelineError, "test-only"):
            PIPE.prepare_config(self.root, repository="Example/Module", module_root="Samples/Fixture")

    def test_workflow_owned_code_ignores_module_gitlink_and_pythonpath(self):
        poisoned = self.root / "references/Hexalith.Builds/Github/lib"
        poisoned.mkdir(parents=True)
        for name in ("ci_config.py", "ci_pipeline.py"):
            (poisoned / name).write_text("raise RuntimeError('module code was executed')")
        self.environment["PYTHONPATH"] = str(poisoned)
        # -P honors PYTHONPATH but omits the script directory, so only the
        # implementation's own leading import path can keep the poison out.
        for isolation in ("-I", "-P"):
            with self.subTest(isolation=isolation):
                result = self.invoke(isolation=isolation)
                self.assertEqual(0, result.returncode, result.stderr)


class ClassifierAndSubmoduleTests(unittest.TestCase):
    def test_vstest_output_overrides_cover_each_target_and_restore_exact_bytes(self):
        with tempfile.TemporaryDirectory(prefix="ci-output-config-") as temporary:
            root = Path(temporary)
            subprocess.run(["git", "init", "--quiet", "--initial-branch=main", str(root)], check=True)
            project = "test/Probe/Probe.csproj"
            (root / project).parent.mkdir(parents=True)
            (root / project).write_text("<Project />")
            original = b'{ "stopOnFail": true, "culture": "fr-FR", "parallelizeTestCollections": false }\n'
            configs = {}
            targets = {}
            for framework in ("net10.0", "net9.0"):
                output = root / "artifacts" / framework
                output.mkdir(parents=True)
                targets[framework] = {"TargetPath": str(output / "Probe.dll")}
                # Exercise both supported xUnit JSON configuration file names.
                config = output / ("Probe.xunit.runner.json" if framework == "net9.0" else "xunit.runner.json")
                config.write_bytes(original)
                configs[framework] = config
            properties = {"TargetFrameworks": "net10.0;net9.0"}
            with mock.patch.object(PIPE, "evaluated_properties",
                                   side_effect=lambda root, project, framework: targets[framework]):
                with self.assertRaisesRegex(PIPE.PipelineError, "product failed"):
                    with PIPE.complete_xunit_vstest_configuration(root, project, properties):
                        for config in configs.values():
                            value = json.loads(config.read_text())
                            self.assertIs(False, value["stopOnFail"])
                            self.assertEqual("fr-FR", value["culture"])
                            self.assertIs(False, value["parallelizeTestCollections"])
                        raise PIPE.PipelineError("product failed", 23)
            for config in configs.values():
                self.assertEqual(original, config.read_bytes())
            subprocess.run(["git", "add", "--", str(configs["net10.0"])], cwd=root, check=True)
            with self.assertRaisesRegex(PIPE.PipelineError, "tracked runner configuration"):
                with PIPE.complete_xunit_vstest_configuration(root, project, {"TargetFrameworks": "", **targets["net10.0"]}):
                    self.fail("tracked build-output configuration must remain untouched")
            self.assertEqual(original, configs["net10.0"].read_bytes())
            source_config = (root / project).parent / "xunit.runner.json"
            source_config.write_bytes(original)
            with self.assertRaisesRegex(PIPE.PipelineError, "source runner configuration"):
                with PIPE.complete_xunit_vstest_configuration(root, project,
                        {"TargetFrameworks": "", "TargetPath": str(source_config.parent / "Probe.dll")}):
                    self.fail("source configuration must remain untouched")
            self.assertEqual(original, source_config.read_bytes())
            # Escaping output configs: a symlink to a repository source config, a
            # symlink outside the repository, and an output directory outside it.
            outside = tempfile.TemporaryDirectory(prefix="ci-outside-config-")
            self.addCleanup(outside.cleanup)
            outside_config = Path(outside.name) / "xunit.runner.json"
            outside_config.write_bytes(original)
            for name, target in (("linked-source", source_config), ("linked-outside", outside_config)):
                output = root / "artifacts" / name
                output.mkdir(parents=True)
                (output / "xunit.runner.json").symlink_to(target)
                with self.subTest(case=name):
                    with self.assertRaisesRegex(PIPE.PipelineError, "refusing to override"):
                        with PIPE.complete_xunit_vstest_configuration(root, project,
                                {"TargetFrameworks": "", "TargetPath": str(output / "Probe.dll")}):
                            self.fail("escaping configuration must remain untouched")
            with self.assertRaisesRegex(PIPE.PipelineError, "refusing to override"):
                with PIPE.complete_xunit_vstest_configuration(root, project,
                        {"TargetFrameworks": "", "TargetPath": str(Path(outside.name) / "Probe.dll")}):
                    self.fail("configuration outside the repository must remain untouched")
            self.assertEqual(original, source_config.read_bytes())
            self.assertEqual(original, outside_config.read_bytes())

    def test_consumer_cli_initializes_recorded_direct_gitlinks_only(self):
        self.check_recorded_consumer_gitlinks(lambda consumer, environment: subprocess.run(
            [sys.executable, "-I", "-S", str(SCRIPT), "--phase", "prepare",
             "--workspace", str(consumer), "--repository", "Example/Consumer"],
            env=environment, text=True, capture_output=True, check=False))

    def check_recorded_consumer_gitlinks(self, prepare):
        """Share real HTTP Git setup and checkout assertions across entrypoints."""
        with tempfile.TemporaryDirectory(prefix="ci-gitlinks-") as temporary:
            base = Path(temporary)
            served = base / "served"
            served.mkdir()

            class QuietHandler(SimpleHTTPRequestHandler):
                def log_message(self, *arguments):
                    pass

            server = ThreadingHTTPServer(("127.0.0.1", 0), partial(QuietHandler, directory=str(served)))
            self.addCleanup(server.server_close)
            url = f"http://127.0.0.1:{server.server_port}"

            def git(directory, *arguments):
                result = subprocess.run(["git", *arguments], cwd=directory, text=True,
                                        capture_output=True, check=False)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                return result.stdout.strip()

            def repository(name, nested=""):
                source = base / name
                source.mkdir()
                git(source, "init", "--initial-branch=main")
                git(source, "config", "user.name", "CI fixture")
                git(source, "config", "user.email", "fixture@example.invalid")
                (source / "revision").write_text("recorded commit")
                git(source, "add", "revision")
                if nested:
                    (source / ".gitmodules").write_text(
                        '[submodule "Nested"]\n\tpath = references/Nested\n'
                        f'\turl = {url}/Nested.git\n')
                    git(source, "add", ".gitmodules")
                    git(source, "update-index", "--add", "--cacheinfo", f"160000,{nested},references/Nested")
                git(source, "commit", "-m", "test: record fixture revision")
                recorded = git(source, "rev-parse", "HEAD")
                (source / "revision").write_text("newer remote tip")
                git(source, "commit", "-am", "test: advance fixture remote")
                tip = git(source, "rev-parse", "HEAD")
                git(base, "clone", "--bare", "--no-local", str(source), str(served / (name + ".git")))
                git(base, "--git-dir", str(served / (name + ".git")), "update-server-info")
                return recorded, tip

            nested, _ = repository("Nested")
            builds, builds_tip = repository("Builds", nested)
            dependency, dependency_tip = repository("Dependency", nested)
            unused, _ = repository("Unused")
            consumer = base / "consumer"
            consumer.mkdir()
            git(consumer, "init", "--initial-branch=main")
            git(consumer, "config", "user.name", "CI fixture")
            git(consumer, "config", "user.email", "fixture@example.invalid")
            # An ambient recursive preference and update=none must not change CI's
            # explicit checkout behavior or cause a nested dependency to initialize.
            git(consumer, "config", "submodule.recurse", "true")
            entries = (("Builds", "references/Hexalith.Builds", builds),
                       ("Dependency", "references/Extra dependency", dependency),
                       ("Unused", "references/Unused", unused))
            # Like `git submodule add`, name one submodule after its spaced path.
            sections = {"Dependency": "references/Extra dependency"}
            (consumer / ".gitmodules").write_text("".join(
                f'[submodule "{sections.get(name, name)}"]\n\tpath = {path}\n\turl = {url}/{name}.git\n\tupdate = none\n'
                for name, path, _ in entries))
            (consumer / ".github").mkdir()
            document = configuration()
            document.update(buildsTag="v1.0.0", submodules=["references/Extra dependency"])
            (consumer / ".github/hexalith-ci.json").write_text(json.dumps(document))
            (consumer / "Fixture.slnx").write_text("<Solution />")
            (consumer / "global.json").write_bytes((ROOT / "global.json").read_bytes())
            git(consumer, "add", ".gitmodules", ".github", "Fixture.slnx", "global.json")
            for _, path, revision in entries:
                git(consumer, "update-index", "--add", "--cacheinfo", f"160000,{revision},{path}")
            git(consumer, "commit", "-m", "test: record consumer dependency graph")
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                environment = os.environ.copy()
                for key in ("CI_MODULE_ROOT", "CI_EXPECTED_SOURCE", "GITHUB_OUTPUT"):
                    environment.pop(key, None)
                result = prepare(consumer, environment)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                outputs = json.loads(result.stdout.splitlines()[-1])
                self.assertEqual([path for _, path, _ in entries[:2]], json.loads(outputs["submodules"]))
                registered = git(consumer, "config", "--local", "--name-only", "--get-regexp", r"^submodule\..*\.url$")
                self.assertEqual({"submodule.Builds.url", "submodule.references/Extra dependency.url"},
                                 set(registered.splitlines()))
                for (_, path, revision), tip in zip(entries[:2], (builds_tip, dependency_tip)):
                    checkout = consumer / path
                    self.assertTrue((checkout / ".git").is_file())
                    self.assertEqual(revision, git(checkout, "rev-parse", "HEAD"))
                    self.assertNotEqual(tip, revision)
                    self.assertEqual("recorded commit", (checkout / "revision").read_text())
                    self.assertFalse((checkout / "references/Nested/.git").exists())
                    self.assertFalse((checkout / "references/Nested/revision").exists())
                    nested_config = subprocess.run(["git", "config", "--local", "--get", "submodule.Nested.url"],
                                                   cwd=checkout, text=True, capture_output=True, check=False)
                    self.assertEqual(1, nested_config.returncode, nested_config.stdout + nested_config.stderr)
                self.assertFalse((consumer / "references/Unused/.git").exists())
                self.assertFalse((consumer / "references/Unused/revision").exists())
            finally:
                server.shutdown()
                thread.join()

    def test_names_are_case_sensitive_and_ordinal(self):
        projects = ("tests/z.Tests.csproj", "test/B.Integration.Tests.csproj", "test/C.AppHost.Tests.csproj",
                    "test/D.E2E.Checks.csproj", "tests/A.Tests.csproj", "test/E.IntegrationTests.csproj",
                    "test/F.integrationtests.csproj", "test/G.e2e.Tests.csproj", "test/H.apphost.Tests.csproj",
                    "tests/a.Tests.csproj", "tests/B.Tests.csproj")
        result = PIPE.classify_projects(projects, configuration(), repository=BUILDS)
        # Lowercase markers are unit projects; ordinal order puts B before a.
        self.assertEqual(("test/F.integrationtests.csproj", "test/G.e2e.Tests.csproj", "test/H.apphost.Tests.csproj",
                          "tests/A.Tests.csproj", "tests/B.Tests.csproj", "tests/a.Tests.csproj",
                          "tests/z.Tests.csproj"), result.unit)
        self.assertEqual(tuple(sorted(set(projects) - set(result.unit))), result.integration)

    def test_only_explicit_root_declared_gitlinks_initialize_without_recursion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = PIPE.PreparedConfig(root, root, configuration(), "Fixture.slnx", "global.json",
                                           ("references/Builds", "references/Extra"))
            calls = []
            declared = ["references/Builds", "references/Extra", "references/Unused"]

            def command(arguments, directory, *, capture=False):
                calls.append(arguments)
                if "config" in arguments[1:3]:
                    return "".join(f"submodule.{path}.path\n{path}\0" for path in declared)
                if "ls-files" in arguments[1:3]:
                    self.assertEqual("--literal-pathspecs", arguments[1])
                    return "160000 " + "a" * 40 + " 0\t" + arguments[-1] + "\0"
                return ""

            with mock.patch.object(PIPE, "run_command", side_effect=command):
                PIPE.initialize_submodules(prepared)
            self.assertEqual(["git", "--literal-pathspecs", "-c", "protocol.file.allow=never", "-c", "submodule.recurse=false",
                              "submodule", "update", "--init", "--checkout", "--",
                              "references/Builds", "references/Extra"], calls[-1])
            self.assertFalse(any("--recursive" in call for call in calls))
            self.assertNotIn("references/Unused", calls[-1])
            invalid = PIPE.PreparedConfig(root, root, configuration(), "Fixture.slnx", "global.json", ("nested/Unknown",))
            with mock.patch.object(PIPE, "run_command", side_effect=command):
                with self.assertRaisesRegex(PIPE.PipelineError, "not declared in root"):
                    PIPE.initialize_submodules(invalid)
            # Git would silently use the last URL for a path declared twice.
            declared.append("references/Extra")
            calls.clear()
            with mock.patch.object(PIPE, "run_command", side_effect=command):
                with self.assertRaisesRegex(PIPE.PipelineError, "duplicate paths"):
                    PIPE.initialize_submodules(prepared)
            self.assertFalse(any("update" in call for call in calls))

    def test_declared_path_that_is_not_a_gitlink_is_rejected_before_update(self):
        with tempfile.TemporaryDirectory(prefix="ci-not-gitlink-") as temporary:
            root = Path(temporary)
            subprocess.run(["git", "init", "--quiet", "--initial-branch=main", str(root)], check=True)
            (root / ".gitmodules").write_text('[submodule "Regular"]\n\tpath = references/Regular\n'
                                              '\turl = https://example.invalid/Regular.git\n')
            (root / "references").mkdir()
            (root / "references/Regular").write_text("ordinary tracked file")
            subprocess.run(["git", "add", ".gitmodules", "references/Regular"], cwd=root, check=True)
            prepared = PIPE.PreparedConfig(root, root, configuration(), "Fixture.slnx", "global.json",
                                           ("references/Regular",))
            with self.assertRaisesRegex(PIPE.PipelineError, "not a direct root gitlink"):
                PIPE.initialize_submodules(prepared)
            self.assertFalse((root / ".git/modules").exists())
            # A declared directory whose only index entry is a deeper gitlink is
            # not itself a gitlink; its pathspec would initialize the unlisted child.
            with (root / ".gitmodules").open("a") as stream:
                stream.write('[submodule "Holder"]\n\tpath = references/Holder\n'
                             '\turl = https://example.invalid/Holder.git\n'
                             '[submodule "Inner"]\n\tpath = references/Holder/Inner\n'
                             '\turl = https://example.invalid/Inner.git\n')
            subprocess.run(["git", "update-index", "--add", "--cacheinfo",
                            "160000," + "a" * 40 + ",references/Holder/Inner"], cwd=root, check=True)
            prepared = PIPE.PreparedConfig(root, root, configuration(), "Fixture.slnx", "global.json",
                                           ("references/Holder",))
            with self.assertRaisesRegex(PIPE.PipelineError, "not a direct root gitlink: references/Holder"):
                PIPE.initialize_submodules(prepared)
            self.assertFalse((root / ".git/modules").exists())


class RealSdkTests(unittest.TestCase):
    """An actual SDK solution/evaluation/xUnit failure proves project boundaries."""

    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="ci-real-sdk-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.root = Path(cls.temporary.name)
        subprocess.run(["git", "init", "--quiet", "--initial-branch=main", str(cls.root)], check=True)
        (cls.root / "global.json").write_bytes((ROOT / "global.json").read_bytes())
        # SDK 10 MTP evaluates via the MSBuild API and must never read these.
        # Restore/build use -noAutoResponse and must also ignore them.
        for name in ("MSBuild.rsp", "Directory.Build.rsp"):
            (cls.root / name).write_text("-invalid-poisoned-response-file-switch")
        (cls.root / ".github").mkdir()
        (cls.root / ".github/hexalith-ci.json").write_text(json.dumps(configuration("solution space/Fixture.slnx")))
        catalog = ET.parse(ROOT / "Props/Directory.Packages.props")
        versions = {item.attrib["Include"]: item.attrib["Version"] for item in catalog.iter("PackageVersion")}
        cls.runner_configuration = json.dumps({"stopOnFail": True, "parallelizeTestCollections": False,
                                                "culture": "fr-FR"}).encode()
        paths = []
        for name in ("A.Tests", "Z.Tests", "Fixture.IntegrationTests", "Ordinary"):
            directory = cls.root / "test dir" / name
            directory.mkdir(parents=True)
            for response_file in ("MSBuild.rsp", "Directory.Build.rsp"):
                (directory / response_file).write_text("-invalid-poisoned-response-file-switch")
            project = directory / (name + ".csproj")
            properties = "<TargetFramework>net10.0</TargetFramework>"
            references = ""
            if name != "Ordinary":
                properties += '''<OutputType>Exe</OutputType>
<UseHexalithProjectReferences Condition="'$(UseHexalithProjectReferences)' == ''">true</UseHexalithProjectReferences>
<IsTestProject Condition="'$(UseHexalithProjectReferences)' == 'false'">true</IsTestProject>
<IsTestProject Condition="'$(UseHexalithProjectReferences)' != 'false'">false</IsTestProject>'''
                references = '<ItemGroup Condition="\'$(UseHexalithProjectReferences)\' == \'false\'">' + ''.join(
                    f'<PackageReference Include="{package}" Version="{versions[package]}" />'
                    for package in ("xunit.v3", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk")) + '''</ItemGroup>
<ItemGroup Condition="'$(UseHexalithProjectReferences)' != 'false'">
  <ProjectReference Include="../../source-mode/Unavailable.Source.csproj" />
</ItemGroup>
<ItemGroup><Content Include="xunit.runner.json" CopyToOutputDirectory="Always" /></ItemGroup>'''
                (directory / "xunit.runner.json").write_bytes(cls.runner_configuration)
                body = '''using Xunit;
public sealed class ProbeTests
{
    [Fact]
    public void FailingTestCompletes()
    {
        System.IO.File.AppendAllText(System.Environment.GetEnvironmentVariable("CI_TEST_MARKER")!, "NAME:first:" + System.Globalization.CultureInfo.CurrentCulture.Name + "\\n");
        FAIL
    }
    [Fact]
    public void PassingTestCompletes()
    {
        System.IO.File.AppendAllText(System.Environment.GetEnvironmentVariable("CI_TEST_MARKER")!, "NAME:second:" + System.Globalization.CultureInfo.CurrentCulture.Name + "\\n");
    }
    [Fact]
    public void AnotherFailingTestCompletes()
    {
        System.IO.File.AppendAllText(System.Environment.GetEnvironmentVariable("CI_TEST_MARKER")!, "NAME:third:" + System.Globalization.CultureInfo.CurrentCulture.Name + "\\n");
        FAIL
    }
}
'''.replace("NAME", name).replace("FAIL", 'Assert.Fail("intentional product failure");' if name == "A.Tests" else "")
                (directory / "ProbeTests.cs").write_text(body)
            project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>{properties}</PropertyGroup>{references}</Project>')
            paths.append(project.relative_to(cls.root).as_posix())
        solution = cls.root / "solution space/Fixture.slnx"
        solution.parent.mkdir()
        solution.write_text('<Solution><Folder Name="/Nested/">' + ''.join(f'<Project Path="../{path}" />' for path in paths) + '</Folder></Solution>')
        source_project = cls.root / "source-mode/Unavailable.Source.csproj"
        source_project.parent.mkdir()
        source_project.write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<Target Name="RejectSourceModeRestore" BeforeTargets="_GenerateRestoreGraphProjectEntry">
  <Error Text="Unavailable.Source.csproj must never be restored in package mode" />
</Target>
</Project>''')
        cls.prepared = PIPE.prepare_config(cls.root, repository=BUILDS)
        PIPE.restore_and_build(cls.prepared)
        # A genuine VSTest fixture uses xUnit's MTP-off variant. SDK 10 rejects
        # VSTest for applications that import the MTP package's targets.
        cls.vstest_root = cls.root / "vstest"
        files = ["global.json", ".github/hexalith-ci.json", "solution space/Fixture.slnx",
                 "source-mode/Unavailable.Source.csproj", "MSBuild.rsp", "Directory.Build.rsp"]
        for project in paths:
            files.extend((project, str(Path(project).parent / "MSBuild.rsp"),
                          str(Path(project).parent / "Directory.Build.rsp")))
            if "Ordinary" not in project:
                files.extend((str(Path(project).parent / "ProbeTests.cs"),
                              str(Path(project).parent / "xunit.runner.json")))
        for relative in files:
            target = cls.vstest_root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((cls.root / relative).read_bytes())
            if target.suffix == ".csproj":
                target.write_text(target.read_text().replace('Include="xunit.v3"', 'Include="xunit.v3.mtp-off"'))
        global_json = json.loads((cls.vstest_root / "global.json").read_text())
        global_json["test"]["runner"] = "VSTest"
        (cls.vstest_root / "global.json").write_text(json.dumps(global_json))
        cls.vstest_prepared = PIPE.prepare_config(cls.vstest_root, repository=BUILDS)
        PIPE.restore_and_build(cls.vstest_prepared)
        # Treat the source runner configuration as tracked product data. Runtime
        # overrides must touch only untracked evaluated build-output copies.
        subprocess.run(["git", "add", "--", "test dir/A.Tests/xunit.runner.json",
                        "vstest/test dir/A.Tests/xunit.runner.json"], cwd=cls.root, check=True)

    def test_package_mode_controls_restore_build_and_evaluated_test_identity(self):
        flags = [flag for flag in (*EXPECTED_ENGINE_FLAGS, *EXPECTED_PROPERTIES)
                 if flag != "-p:UseHexalithProjectReferences=false"]
        try:
            for phase in ("build", "restore"):
                with self.subTest(phase=phase):
                    result = subprocess.run(["dotnet", phase, "solution space/Fixture.slnx",
                                             *(["--no-restore"] if phase == "build" else []), *flags],
                                            cwd=self.root, text=True, capture_output=True, check=False)
                    self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertIn("Unavailable.Source.csproj", result.stdout + result.stderr)
            result = subprocess.run(["dotnet", "msbuild", "test dir/A.Tests/A.Tests.csproj", "-nologo",
                                     "-getProperty:IsTestProject", *flags], cwd=self.root,
                                    text=True, capture_output=True, check=False)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual("false", result.stdout.strip())
        finally:
            # A rejected source-mode restore can update obj state. Restore the
            # valid package branch before the execution/discovery regressions.
            PIPE.restore_and_build(self.prepared)

    def test_real_solution_membership_and_evaluated_is_test_project(self):
        projects = PIPE.discover_test_projects(self.root, "solution space/Fixture.slnx")
        self.assertEqual(("test dir/A.Tests/A.Tests.csproj", "test dir/Fixture.IntegrationTests/Fixture.IntegrationTests.csproj",
                          "test dir/Z.Tests/Z.Tests.csproj"), projects)
        classified = PIPE.classify_projects(projects, self.prepared.document, repository=BUILDS)
        self.assertEqual(("test dir/A.Tests/A.Tests.csproj", "test dir/Z.Tests/Z.Tests.csproj"), classified.unit)
        self.assertEqual(("test dir/Fixture.IntegrationTests/Fixture.IntegrationTests.csproj",), classified.integration)

    def test_real_failing_project_overrides_stop_on_fail_for_both_runners(self):
        for prepared in (self.prepared, self.vstest_prepared):
            with self.subTest(runner=PIPE.test_runner(prepared.root)):
                marker = prepared.root / "completed-tests"
                with mock.patch.dict(os.environ, {"CI_TEST_MARKER": str(marker)}):
                    with self.assertRaises(PIPE.PipelineError) as caught:
                        PIPE.run_unit_tests(prepared, repository=BUILDS)
                self.assertGreater(caught.exception.returncode, 0)
                # Each test runs exactly once; none is cancelled or repeated.
                self.assertEqual(["A.Tests:first:fr-FR", "A.Tests:second:fr-FR", "A.Tests:third:fr-FR"],
                                 sorted(marker.read_text().splitlines()))
                for path in (prepared.root / "test dir/A.Tests/xunit.runner.json",
                             prepared.root / "test dir/A.Tests/bin/Release/net10.0/xunit.runner.json"):
                    self.assertEqual(self.runner_configuration, path.read_bytes())

    def test_real_integration_override_completes_failing_project_for_both_runners(self):
        from dataclasses import replace

        for prepared in (self.prepared, self.vstest_prepared):
            with self.subTest(runner=PIPE.test_runner(prepared.root)):
                marker = prepared.root / "integration-completed-tests"
                document = dict(prepared.document, tests={"integration": ["test dir/A.Tests/A.Tests.csproj",
                                                                        "test dir/Z.Tests/Z.Tests.csproj"],
                                                       "exclude": {}})
                with mock.patch.dict(os.environ, {"CI_TEST_MARKER": str(marker)}):
                    with self.assertRaises(PIPE.PipelineError) as caught:
                        PIPE.run_integration_tests(replace(prepared, document=document), repository=BUILDS)
                self.assertGreater(caught.exception.returncode, 0)
                self.assertEqual(["A.Tests:first:fr-FR", "A.Tests:second:fr-FR", "A.Tests:third:fr-FR"],
                                 sorted(marker.read_text().splitlines()))
                self.assertEqual(self.runner_configuration,
                                 (prepared.root / "test dir/A.Tests/bin/Release/net10.0/xunit.runner.json").read_bytes())


class SinglePropertySdkTests(unittest.TestCase):
    def test_real_sdk_single_property_values_remain_strings(self):
        with tempfile.TemporaryDirectory(prefix="ci-single-property-") as directory:
            root = Path(directory)
            (root / "global.json").write_bytes((ROOT / "global.json").read_bytes())
            (root / "Fixture.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<TargetFramework>net10.0</TargetFramework><PackageId>123</PackageId>'
                '<IsPackable>true</IsPackable></PropertyGroup></Project>')
            for name, expected in (("PackageId", "123"), ("IsPackable", "true"),
                                   ("TargetFramework", "net10.0"), ("MissingProperty", "")):
                with self.subTest(property=name):
                    self.assertEqual({name: expected}, PIPE.evaluated_properties(root, "Fixture.csproj",
                        properties=(name,)))


class NuGetTrustPolicyTests(unittest.TestCase):
    """The real trusted config rejects unsigned and unmapped packages."""

    PACKAGE = "Hexalith.Ci.Unsigned.Probe"

    def derived_config(self, root: Path, name: str, *, mapped: bool, signatures: bool) -> Path:
        # Each variant changes exactly one control of the real config, so
        # relaxing that control in NuGet.ci.Config changes the outcome.
        tree = ET.parse(PIPE.NUGET_CONFIG)
        document = tree.getroot()
        ET.SubElement(document.find("packageSources"), "add", key="probe", value=str(root / "feed"))
        if mapped:
            source = ET.SubElement(document.find("packageSourceMapping"), "packageSource", key="probe")
            ET.SubElement(source, "package", pattern=self.PACKAGE)
        if not signatures:
            settings = document.find("config")
            for item in list(settings):
                if item.get("key") == "signatureValidationMode":
                    settings.remove(item)
        path = root / f"{name}.config"
        tree.write(path, encoding="utf-8", xml_declaration=True)
        return path

    def test_unsigned_local_package_is_rejected_by_signature_and_source_mapping(self):
        with tempfile.TemporaryDirectory(prefix="ci-nuget-trust-") as temporary:
            root = Path(temporary)
            (root / "global.json").write_bytes((ROOT / "global.json").read_bytes())
            (root / "Probe").mkdir()
            (root / "Probe/Probe.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                f'<PackageId>{self.PACKAGE}</PackageId><Version>1.0.0</Version><Authors>CI</Authors>'
                '<Description>Unsigned trust probe</Description></PropertyGroup></Project>')
            (root / "Probe/Marker.cs").write_text("namespace Probe;\npublic static class Marker { }\n")
            result = subprocess.run(["dotnet", "pack", "Probe/Probe.csproj", "-o", "feed",
                                     f"-p:RestorePackagesPath={root / 'packages-pack'}", *PIPE.build_flags()],
                                    cwd=root, text=True, capture_output=True, check=False)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            (root / "Consumer").mkdir()
            (root / "Consumer/Consumer.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                f'</PropertyGroup><ItemGroup><PackageReference Include="{self.PACKAGE}" Version="1.0.0" />'
                '</ItemGroup></Project>')
            cases = (("signed", True, True, "NU3004"), ("control", True, False, None),
                     ("unmapped", False, False, "NU1101"))
            for name, mapped, signatures, error in cases:
                with self.subTest(policy=name):
                    config = self.derived_config(root, name, mapped=mapped, signatures=signatures)
                    # A fresh package folder forces source selection and signature verification.
                    result = subprocess.run(["dotnet", "restore", "Consumer/Consumer.csproj",
                                             f"-p:RestorePackagesPath={root / ('packages-' + name)}",
                                             *PIPE.build_flags(config)],
                                            cwd=root, text=True, capture_output=True, check=False)
                    output = result.stdout + result.stderr
                    if error is None:
                        self.assertEqual(0, result.returncode, output)
                    else:
                        self.assertNotEqual(0, result.returncode, output)
                        self.assertIn(error, output)


if __name__ == "__main__":
    unittest.main()
