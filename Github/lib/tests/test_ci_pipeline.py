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
            values.update(TargetFrameworks="net10.0;net9.0", IsCrossTargetingBuild="true")
            if inner:
                values["IsCrossTargetingBuild"]="false"
                values["TargetFramework"]=inner
                if data.get("inconsistent") and inner=="net9.0":
                    values["IsTestProject"]="false"
        print(json.dumps({"Properties":values}))
elif command=="test":
    project=args[2] if args[1]=="--project" else args[1]
    with (root / "tests-completed").open("a") as results:
        results.write(project+":failing-test-completed\n")
        results.write(project+":passing-test-completed\n")
    if data.get("fail_project")==project:
        sys.exit(23)
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

    def invoke(self, *arguments):
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.document))
        (self.root / "fake-sdk.json").write_text(json.dumps(self.data))
        return subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), "--workspace", str(self.root),
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
                self.assertEqual(["restore"] if failing_command == "restore" else ["restore", "build"],
                                 [command[0] for command in self.commands()])

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
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        evaluations = [command for command in self.commands() if command[0] == "msbuild"]
        for framework in ("net10.0", "net9.0"):
            self.assertTrue(any(f"-p:TargetFramework={framework}" in command for command in evaluations))
        self.data["inconsistent"] = True
        result = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertIn("differs across target frameworks", result.stderr)

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
        result = self.invoke()
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

    def test_consumer_cli_initializes_recorded_direct_gitlinks_only(self):
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
            (consumer / ".gitmodules").write_text("".join(
                f'[submodule "{name}"]\n\tpath = {path}\n\turl = {url}/{name}.git\n\tupdate = none\n'
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
                result = subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), "--phase", "prepare",
                                         "--workspace", str(consumer), "--repository", "Example/Consumer"],
                                        env=environment, text=True, capture_output=True, check=False)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                outputs = json.loads(result.stdout.splitlines()[-1])
                self.assertEqual([path for _, path, _ in entries[:2]], json.loads(outputs["submodules"]))
                registered = git(consumer, "config", "--local", "--name-only", "--get-regexp", r"^submodule\..*\.url$")
                self.assertEqual({"submodule.Builds.url", "submodule.Dependency.url"}, set(registered.splitlines()))
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
                    "test/D.E2E.Checks.csproj", "tests/A.Tests.csproj", "test/E.IntegrationTests.csproj")
        result = PIPE.classify_projects(projects, configuration(), repository=BUILDS)
        self.assertEqual(("tests/A.Tests.csproj", "tests/z.Tests.csproj"), result.unit)
        self.assertEqual(tuple(sorted(set(projects) - set(result.unit))), result.integration)

    def test_only_explicit_root_declared_gitlinks_initialize_without_recursion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = PIPE.PreparedConfig(root, root, configuration(), "Fixture.slnx", "global.json",
                                           ("references/Builds", "references/Extra"))
            calls = []

            def command(arguments, directory, *, capture=False):
                calls.append(arguments)
                if arguments[1] == "config":
                    return "submodule.builds.path references/Builds\nsubmodule.extra.path references/Extra\nsubmodule.unused.path references/Unused\n"
                if arguments[1] == "ls-files":
                    return "160000 " + "a" * 40 + " 0\t" + arguments[-1] + "\n"
                return ""

            with mock.patch.object(PIPE, "run_command", side_effect=command):
                PIPE.initialize_submodules(prepared)
            self.assertEqual(["git", "-c", "protocol.file.allow=never", "-c", "submodule.recurse=false",
                              "submodule", "update", "--init", "--checkout", "--",
                              "references/Builds", "references/Extra"], calls[-1])
            self.assertFalse(any("--recursive" in call for call in calls))
            self.assertNotIn("references/Unused", calls[-1])
            invalid = PIPE.PreparedConfig(root, root, configuration(), "Fixture.slnx", "global.json", ("nested/Unknown",))
            with mock.patch.object(PIPE, "run_command", side_effect=command):
                with self.assertRaisesRegex(PIPE.PipelineError, "not declared in root"):
                    PIPE.initialize_submodules(invalid)


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
                self.assertEqual({"A.Tests:first:fr-FR", "A.Tests:second:fr-FR", "A.Tests:third:fr-FR"},
                                 set(marker.read_text().splitlines()))
                for path in (prepared.root / "test dir/A.Tests/xunit.runner.json",
                             prepared.root / "test dir/A.Tests/bin/Release/net10.0/xunit.runner.json"):
                    self.assertEqual(self.runner_configuration, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
