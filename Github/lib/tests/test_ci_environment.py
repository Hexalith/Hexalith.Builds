"""Environment effects and executable composite boundaries; no YAML text assertions."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock

import yaml

import test_ci_pipeline as FIXTURE


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "Github/lib/ci_environment.py"
SPEC = importlib.util.spec_from_file_location("ci_environment", SCRIPT)
ENV = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ENV)


class CompositeHarness:
    """Run the real shared job/composites in a disposable workspace.

    This small Actions interpreter supports this job's literal steps and gates.
    External checkout/SDK actions are supplied by the fixture, while every local
    composite and enabled hook is actually executed with its own action path.
    """

    def __init__(self, root, environment, *, module_root="", expected_source="", secrets=None,
                 dapr_archive=None, mutate_workflow=None, runner_os="Linux", external_action=None):
        self.root = root
        self.environment = dict(environment, GITHUB_WORKSPACE=str(root))
        self.path = self.environment["PATH"]
        self.stdout = []
        self.stderr = []
        self.mutate_workflow = mutate_workflow
        self.external_action = external_action
        runner_temp = root / "runner-temp"
        runner_temp.mkdir(exist_ok=True)
        self.environment["RUNNER_TEMP"] = str(runner_temp)
        if dapr_archive is not None:
            # Instrument only the environment Python process. The actual action
            # command, inputs, stdlib script and extracted executable still run.
            archive = root / "controlled-dapr.tar.gz"
            archive.write_bytes(dapr_archive)
            shim = root / "harness-bin/python3"
            shim.parent.mkdir(exist_ok=True)
            shim.write_text(f"#!{sys.executable}\n" + r'''
import hashlib, importlib.util, io, os, pathlib, sys
args=sys.argv[1:]
if len(args)>=2 and args[0]=="-I" and pathlib.Path(args[1]).name=="ci_environment.py":
    spec=importlib.util.spec_from_file_location("controlled_ci_environment", args[1])
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    data=pathlib.Path(os.environ["HARNESS_DAPR_ARCHIVE"]).read_bytes()
    module.urlopen=lambda *args, **kwargs: io.BytesIO(data)
    module.DAPR_CLI_DIGESTS={"1.19.1":hashlib.sha256(data).hexdigest()}
    sys.argv=args[1:]
    raise SystemExit(module.main())
os.execv(sys.executable, [sys.executable, *args])
''')
            shim.chmod(0o755)
            self.path = str(shim.parent) + os.pathsep + self.path
            self.environment["HARNESS_DAPR_ARCHIVE"] = str(archive)
        self.context = {"github": {"repository": FIXTURE.BUILDS},
                        "runner": {"temp": str(runner_temp), "os": runner_os},
                        "inputs": {"module-root": module_root, "expected-source": expected_source},
                        "secrets": secrets or {"registry-pull-username": "", "registry-pull-password": ""},
                        "steps": {}}
        self.counter = 0

    def result(self, code):
        return subprocess.CompletedProcess([], code, "".join(self.stdout), "".join(self.stderr))

    def value(self, expression, context):
        joined = re.fullmatch(r"join\(fromJSON\((.*?)\), '([\s\S]*)'\)", expression.strip())
        if joined:
            return joined[2].join(json.loads(self.value(joined[1], context)))
        value = context
        for name in expression.strip().split("."):
            value = value.get(name, "") if isinstance(value, dict) else ""
        return str(value)

    def expand(self, value, context):
        return re.sub(r"\$\{\{\s*(.*?)\s*\}\}", lambda match: self.value(match[1], context),
                      str(value), flags=re.DOTALL)

    def enabled(self, expression, context):
        if " && " in expression:
            return all(self.enabled(part, context) for part in expression.split(" && "))
        match = re.fullmatch(r"(.*?) (==|!=) '([^']*)'", expression)
        if not match:
            raise AssertionError(f"unsupported harness condition: {expression}")
        actual = self.value(match[1], context)
        return (actual == match[3]) == (match[2] == "==")

    def steps(self, steps, context, environment):
        for step in steps:
            if "if" in step and not self.enabled(step["if"], context):
                continue
            env = dict(environment)
            env["PATH"] = self.path
            env.update({name: self.expand(value, context) for name, value in step.get("env", {}).items()})
            outputs = {}
            if "uses" in step:
                reference = step["uses"]
                if reference.startswith("$/"):
                    action = ROOT / reference[2:]
                elif reference.startswith("./"):
                    action = self.root / reference[2:]
                else:
                    # Hosted implementations remain external controls, distinct
                    # from the local composite/script execution evidence.
                    if self.external_action:
                        inputs = {name: self.expand(value, context) for name, value in step.get("with", {}).items()}
                        code = self.external_action(reference, inputs, env)
                        if code:
                            return self.result(code)
                    continue
                document = yaml.safe_load((action / "action.yml").read_text())
                if document["runs"]["using"] != "composite":
                    raise AssertionError("hook must be composite")
                inputs = {name: entry.get("default", "") for name, entry in document.get("inputs", {}).items()}
                inputs.update({name: self.expand(value, context) for name, value in step.get("with", {}).items()})
                child = dict(context, inputs=inputs, steps={})
                result = self.steps(document["runs"]["steps"], child, dict(env, GITHUB_ACTION_PATH=str(action)))
                if result.returncode:
                    return result
                outputs = {name: self.expand(entry["value"], child)
                           for name, entry in document.get("outputs", {}).items()}
            else:
                self.counter += 1
                output = self.root / f"action-output-{self.counter}"
                path = self.root / f"action-path-{self.counter}"
                output.unlink(missing_ok=True)
                path.unlink(missing_ok=True)
                env.update(GITHUB_OUTPUT=str(output), GITHUB_PATH=str(path))
                if step.get("shell", "bash") == "pwsh":
                    # A dotnet-tool installation of pwsh can launch through
                    # dotnet on PATH. Bootstrap the real shell first, then
                    # install the command-double PATH inside PowerShell.
                    env["HARNESS_COMMAND_PATH"] = env["PATH"]
                    env["PATH"] = os.environ["PATH"]
                    body = ("$ErrorActionPreference = 'Stop'; $env:PATH = $env:HARNESS_COMMAND_PATH;\n"
                            + step["run"] + "\nif (Test-Path -LiteralPath variable:\\LASTEXITCODE) { exit $LASTEXITCODE }")
                    command = [shutil.which("pwsh"), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", body]
                else:
                    command = ["bash", "--noprofile", "--norc", "-eo", "pipefail", "-c", step["run"]]
                directory = self.root / self.expand(step.get("working-directory", "."), context)
                result = subprocess.run(command, cwd=directory, env=env,
                                        text=True, capture_output=True, check=False)
                self.stdout.append(result.stdout)
                self.stderr.append(result.stderr)
                if result.returncode:
                    return self.result(result.returncode)
                if output.exists():
                    outputs = dict(line.split("=", 1) for line in output.read_text().splitlines())
                if path.exists():
                    self.path = os.pathsep.join(path.read_text().splitlines()) + os.pathsep + self.path
            if "id" in step:
                context["steps"][step["id"]] = {"outputs": outputs}
        return self.result(0)

    def run(self):
        # The real guard reads committed objects. Publish the fixture inputs
        # before executing this job, just as checkout supplies a committed SHA.
        # Repeated harness calls intentionally test freshly changed inputs.
        def git(*arguments):
            subprocess.run([shutil.which("git"), "-c", "core.hooksPath=" + os.devnull,
                            "-c", "user.name=Composite fixture", "-c", "user.email=fixture@example.invalid",
                            "-c", "commit.gpgsign=false", "-C", str(self.root), *arguments],
                           env=self.environment, capture_output=True, check=True)

        git("init", "-q", "-b", "main")
        inputs = [name for name in (".github", "Samples") if (self.root / name).exists()]
        git("add", "--all", "--", *inputs)
        git("commit", "-qm", "test: commit composite fixture inputs", "--allow-empty")
        document = yaml.safe_load((ROOT / ".github/workflows/ci.yml").read_text())
        if self.mutate_workflow:
            self.mutate_workflow(document)
        job = document["jobs"]["ci"]
        self.environment.update({name: self.expand(value, self.context) for name, value in job.get("env", {}).items()})
        return self.steps(job["steps"], self.context, self.environment)

    def run_action(self, action, inputs=None):
        """Execute real composite metadata with controlled external actions."""
        document = yaml.safe_load((action / "action.yml").read_text())
        values = {name: entry.get("default", "") for name, entry in document.get("inputs", {}).items()}
        values.update(inputs or {})
        context = dict(self.context, inputs=values, steps={})
        return self.steps(document["runs"]["steps"], context,
                          dict(self.environment, GITHUB_ACTION_PATH=str(action)))


class EnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURE.SubprocessPipelineTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        self.effects = self.root / "environment-effects.jsonl"
        # Real subprocess doubles record observable effects, not Python call counts.
        for name in ("pwsh", "docker"):
            script = self.fixture.bin / name
            script.write_text(f"#!{sys.executable}\n" + r'''
import json, os, pathlib, sys
root=pathlib.Path.cwd()
name=pathlib.Path(sys.argv[0]).name
assert (root / "build-completed").read_text()=="Release"
args=sys.argv[1:]
with (root / "environment-effects.jsonl").open("a") as stream:
    stream.write(json.dumps([name,args,sys.stdin.read() if name=="docker" else ""])+"\n")
if name=="pwsh":
    assert pathlib.Path(args[args.index("-File")+1]).is_file()
if os.environ.get("FAIL_ENVIRONMENT_TOOL")==name:
    sys.exit(29)
(root / (name+"-ready")).touch()
''')
            script.chmod(0o755)

    def save_fixture(self):
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.fixture.document))
        (self.root / "fake-sdk.json").write_text(json.dumps(self.fixture.data))

    def built_playwright(self, version="1.64.1", project="test/A space/A.Tests.csproj", framework="net10.0"):
        output = self.root / Path(project).parent / "bin/Release" / framework
        (output / ".playwright/package").mkdir(parents=True, exist_ok=True)
        (output / "playwright.ps1").write_text("# built package tooling")
        (output / "Microsoft.Playwright.dll").write_bytes(b"built-package")
        (output / ".playwright/package/package.json").write_text(json.dumps({"version": version}))
        return output / "playwright.ps1"

    def invoke(self, *arguments, environment=None):
        self.save_fixture()
        return subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), "--workspace", str(self.root),
                               "--solution", "Fixture.slnx", *arguments],
                              env=environment or self.fixture.environment, text=True, capture_output=True, check=False)

    def hook(self, name, *, fail=False, expect_tools=False):
        directory = self.root / ".github/actions" / name
        directory.mkdir(parents=True)
        command = ("from pathlib import Path; "
                   "assert Path('build-completed').read_text() == 'Release'; "
                   + ("assert Path('pwsh-ready').is_file() and Path('docker-ready').is_file(); " if expect_tools else "")
                   + ("assert 'test/Fixture.IntegrationTests.csproj:passing-test-completed' "
                      "in Path('tests-completed').read_text().splitlines(); "
                      if name == "after-integration-tests" else "")
                   + f"Path('hook-{name}').touch(); "
                   + ("raise SystemExit(31)" if fail else ""))
        (directory / "action.yml").write_text(yaml.safe_dump({"name": name, "description": "probe",
            "runs": {"using": "composite", "steps": [{"shell": "bash", "run": f"python3 -c \"{command}\""}]}}))

    def harness(self, **kwargs):
        self.save_fixture()
        return CompositeHarness(self.root, self.fixture.environment, **kwargs).run()

    def controlled_dapr_archive(self):
        payload = (f"#!{sys.executable}\n" + r'''
import os, pathlib, sys
root=pathlib.Path.cwd()
args=sys.argv[1:]
with (root / "dapr-events").open("a") as stream:
    stream.write(" ".join(args)+"\n")
if args[0]=="init":
    assert args==["init", "--runtime-version", "1.19.1"]
    if os.environ.get("FAIL_DAPR_INIT"):
        sys.exit(37)
    (root / "dapr-initialized").touch()
elif args[0]=="probe":
    assert (root / "dapr-initialized").exists()
    print("dapr-hook-stdout")
    print("dapr-hook-stderr", file=sys.stderr)
else:
    sys.exit(43)
''').encode()
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w:gz") as bundle:
            item = tarfile.TarInfo("dapr")
            item.size = len(payload)
            bundle.addfile(item, io.BytesIO(payload))
        return stream.getvalue()

    def dapr_hook(self):
        self.hook("after-unit-tests")
        path = self.root / ".github/actions/after-unit-tests/action.yml"
        document = yaml.safe_load(path.read_text())
        document["runs"]["steps"].insert(0, {"shell": "bash", "run":
            "python3 -c \"import os, pathlib, shutil; "
            "assert pathlib.Path(shutil.which('dapr')).is_relative_to(pathlib.Path(os.environ['RUNNER_TEMP']))\" && dapr probe"})
        path.write_text(yaml.safe_dump(document))

    def test_disabled_flags_make_no_sdk_download_or_tool_calls(self):
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.fixture.commands())
        self.assertFalse(self.effects.exists())
        with mock.patch.object(ENV, "setup_dapr", side_effect=AssertionError("disabled download")):
            ENV.setup_environment(self.root, solution="none", dapr=True, browsers=True, registry=True)

    def test_browsers_use_built_version_and_install_once_for_matching_outputs(self):
        first = self.built_playwright(version="1.60.0")
        self.built_playwright(version="1.60.0", project="test/Z.Tests/Z.Tests.csproj")
        (self.root / "build-completed").write_text("Release")
        result = self.invoke("--browsers", "true")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        effects = [json.loads(line) for line in self.effects.read_text().splitlines()]
        self.assertEqual([["pwsh", ["-NoLogo", "-NoProfile", "-File", str(first), "install", "--with-deps"], ""]], effects)
        self.assertFalse(any(command[0] in ("restore", "build", "test") for command in self.fixture.commands()))

    def test_distinct_browser_versions_and_cross_targeted_repeated_outputs_install_once(self):
        first = self.built_playwright(version="1.60.0")
        second = self.built_playwright(version="1.61.0", framework="net9.0")
        duplicate = self.built_playwright(version="1.61.0", project="test/Z.Tests/Z.Tests.csproj")
        self.fixture.data.update(cross=True, metadata={"test/Fixture.IntegrationTests.csproj": {
            "TargetPath": str(duplicate.parent / "Fixture.IntegrationTests.dll")}})
        (self.root / "build-completed").write_text("Release")
        result = self.invoke("--browsers", "true")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        effects = [json.loads(line) for line in self.effects.read_text().splitlines()]
        self.assertEqual([str(first), str(second)], [effect[1][3] for effect in effects])
        evaluated = {argument for command in self.fixture.commands() for argument in command
                     if argument.startswith("-p:TargetFramework=")}
        self.assertEqual({"-p:TargetFramework=net10.0", "-p:TargetFramework=net9.0"}, evaluated)

    def test_mixed_complete_and_incomplete_playwright_outputs_fail_before_installing_any_version(self):
        self.built_playwright(version="1.60.0")
        script = self.built_playwright(version="1.61.0", project="test/Z.Tests/Z.Tests.csproj")
        for relative in ("playwright.ps1", "Microsoft.Playwright.dll", ".playwright/package/package.json"):
            with self.subTest(missing=relative):
                target = script.parent / relative
                original = target.read_bytes()
                target.unlink()
                try:
                    result = self.invoke("--browsers", "true")
                    self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                    self.assertIn("Playwright tooling is incomplete", result.stderr)
                    self.assertFalse(self.effects.exists())
                finally:
                    target.write_bytes(original)

    def test_playwright_output_containment_and_escaping_artifacts_fail_before_tool_execution(self):
        script = self.built_playwright()
        with tempfile.TemporaryDirectory(prefix="outside-playwright-") as temporary:
            outside = Path(temporary)
            for relative in ("playwright.ps1", "Microsoft.Playwright.dll", ".playwright/package/package.json"):
                external = outside / relative
                external.parent.mkdir(parents=True, exist_ok=True)
                target = script.parent / relative
                content = target.read_bytes()
                external.write_bytes(content)
                with self.subTest(escaping=relative):
                    target.unlink()
                    target.symlink_to(external)
                    try:
                        result = self.invoke("--browsers", "true")
                        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                        self.assertIn("Playwright tooling", result.stderr)
                        self.assertFalse(self.effects.exists())
                    finally:
                        target.unlink()
                        target.write_bytes(content)
            self.fixture.data["metadata"] = {"test/A space/A.Tests.csproj": {"TargetPath": str(outside / "A.Tests.dll")}}
            result = self.invoke("--browsers", "true")
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn("not a contained build output", result.stderr)
            self.assertFalse(self.effects.exists())

    def test_unsupported_trusted_dapr_pin_fails_before_download_or_execution(self):
        trusted = self.root / "trusted-builds"
        (trusted / "Props").mkdir(parents=True)
        (trusted / "Props/Directory.Packages.props").write_text(
            "<Project><PropertyGroup><HexalithDaprRuntimeVersion>1.19.1</HexalithDaprRuntimeVersion>"
            "<HexalithDaprCliVersion>9.9.9</HexalithDaprCliVersion></PropertyGroup></Project>")
        pins = ENV.dapr_versions(trusted)
        with mock.patch.object(ENV, "dapr_versions", return_value=pins), \
                mock.patch.object(ENV, "urlopen", side_effect=AssertionError("unsupported pin downloaded")):
            with self.assertRaisesRegex(ENV.PipelineError, "9.9.9 has no trusted archive digest"):
                ENV.setup_dapr(self.root)
        self.assertFalse((self.root / "dapr-initialized").exists())
        self.assertFalse(self.effects.exists())

    def test_missing_built_tooling_and_bad_flags_fail_clearly(self):
        result = self.invoke("--browsers", "true")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("no built Playwright tooling", result.stderr)
        result = self.invoke("--dapr", "yes")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("expected true or false", result.stderr)

    def test_registry_missing_secrets_fails_before_any_optional_work(self):
        environment = dict(self.fixture.environment)
        for name in ("REGISTRY_PULL_USERNAME", "REGISTRY_PULL_PASSWORD"):
            environment.pop(name, None)
        result = self.invoke("--registry", "true", "--dapr", "true", "--browsers", "true", environment=environment)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("dedicated pull-only", result.stderr)
        self.assertEqual([], self.fixture.commands())
        self.assertFalse(self.effects.exists())

    def test_dapr_integrity_and_archive_paths_are_verified_before_execution(self):
        def archive(name="dapr", kind=tarfile.REGTYPE):
            stream = io.BytesIO()
            with tarfile.open(fileobj=stream, mode="w:gz") as bundle:
                item = tarfile.TarInfo(name)
                item.type = kind
                payload = f"#!{sys.executable}\nimport pathlib,sys\npathlib.Path('dapr-init').write_text(' '.join(sys.argv[1:]))\n".encode()
                item.size = len(payload) if kind == tarfile.REGTYPE else 0
                bundle.addfile(item, io.BytesIO(payload) if item.size else None)
            return stream.getvalue()

        valid = archive()
        with self.assertRaisesRegex(ENV.PipelineError, "SHA-256 mismatch"):
            ENV.install_dapr_cli(valid, "0" * 64, self.root / "rejected")
        self.assertFalse((self.root / "rejected").exists())
        for invalid in (archive("../dapr"), archive(kind=tarfile.SYMTYPE)):
            with self.assertRaisesRegex(ENV.PipelineError, "regular dapr executable"):
                ENV.install_dapr_cli(invalid, hashlib.sha256(invalid).hexdigest(), self.root / "rejected")
        path = self.root / "github-path"
        with mock.patch.object(ENV, "urlopen", return_value=io.BytesIO(valid)) as download, \
                mock.patch.dict(ENV.DAPR_CLI_DIGESTS, {"1.19.1": hashlib.sha256(valid).hexdigest()}), \
                mock.patch.dict(os.environ, {"GITHUB_PATH": str(path), "RUNNER_TEMP": str(self.root)}):
            ENV.setup_dapr(self.root)
        self.assertEqual("init --runtime-version 1.19.1", (self.root / "dapr-init").read_text())
        self.assertTrue(Path(path.read_text().strip(), "dapr").is_file())
        self.assertEqual("https://github.com/dapr/cli/releases/download/v1.19.1/dapr_linux_amd64.tar.gz",
                         download.call_args.args[0])
        poisoned = self.root / "references/Hexalith.Builds/Props"
        poisoned.mkdir(parents=True)
        (poisoned / "Directory.Packages.props").write_text("poison")
        self.assertEqual(("1.19.1", "1.19.1"), ENV.dapr_versions())

        # Enable all three tools together: CLI/runtime initialization and the
        # built browser/registry subprocesses each execute exactly once.
        self.built_playwright()
        self.save_fixture()
        (self.root / "build-completed").write_text("Release")
        environment = dict(self.fixture.environment, GITHUB_PATH=str(path), RUNNER_TEMP=str(self.root),
                           REGISTRY_PULL_USERNAME="pull-user", REGISTRY_PULL_PASSWORD="pull-secret")
        with mock.patch.object(ENV, "urlopen", return_value=io.BytesIO(valid)) as download, \
                mock.patch.dict(ENV.DAPR_CLI_DIGESTS, {"1.19.1": hashlib.sha256(valid).hexdigest()}), \
                mock.patch.dict(os.environ, environment):
            ENV.setup_environment(self.root, solution="Fixture.slnx", dapr=True, browsers=True, registry=True)
        self.assertEqual(1, download.call_count)
        self.assertEqual("init --runtime-version 1.19.1", (self.root / "dapr-init").read_text())
        self.assertEqual(["pwsh", "docker"], [json.loads(line)[0] for line in self.effects.read_text().splitlines()])

    def test_actual_actions_run_enabled_hooks_with_existing_build_and_environment(self):
        self.fixture.document.update(browsers=True, registry=True, hooks=["after-unit-tests", "after-integration-tests"])
        self.built_playwright()
        self.hook("after-unit-tests", expect_tools=True)
        self.hook("after-integration-tests", expect_tools=True)
        self.hook("nightly", fail=True)
        # Observe the integration process only after the first hook.
        fake = self.fixture.bin / "dotnet"
        fake.write_text(fake.read_text().replace('elif command=="test":',
            'elif command=="test":\n    if "IntegrationTests" in " ".join(args):\n'
            '        assert (root / "hook-after-unit-tests").exists()'))
        secrets = {"registry-pull-username": "pull-user", "registry-pull-password": "pull-password-marker"}
        result = self.harness(secrets=secrets)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue((self.root / "hook-after-unit-tests").exists())
        self.assertTrue((self.root / "hook-after-integration-tests").exists())
        self.assertFalse((self.root / "hook-nightly").exists())
        effects = [json.loads(line) for line in self.effects.read_text().splitlines()]
        self.assertEqual(["pwsh", "docker"], [effect[0] for effect in effects])
        self.assertEqual(["login", "registry.hexalith.com", "--username=pull-user", "--password-stdin"], effects[1][1])
        self.assertEqual("pull-password-marker", effects[1][2])
        self.assertNotIn("pull-password-marker", result.stdout + result.stderr)
        commands = self.fixture.commands()
        self.assertEqual(1, sum(command[0] == "restore" for command in commands))
        self.assertEqual(1, sum(command[0] == "build" for command in commands))
        self.assertEqual(3, sum(command[0] == "test" for command in commands))

    def test_enabled_dapr_reaches_actual_action_and_hook_path_and_dropped_forwarding_fails(self):
        self.fixture.document.update(dapr=True, hooks=["after-unit-tests", "after-integration-tests"])
        self.dapr_hook()
        self.hook("after-integration-tests")
        archive = self.controlled_dapr_archive()
        result = self.harness(dapr_archive=archive)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["init --runtime-version 1.19.1", "probe"], (self.root / "dapr-events").read_text().splitlines())
        self.assertIn("dapr-hook-stdout", result.stdout)
        self.assertIn("dapr-hook-stderr", result.stderr)
        self.assertTrue((self.root / "hook-after-integration-tests").exists())

        def drop_dapr_forwarding(document):
            for step in document["jobs"]["ci"]["steps"]:
                if step.get("uses") == "$/Github/ci-environment":
                    step["with"].pop("dapr")

        for name in ("hook-after-unit-tests", "hook-after-integration-tests", "dapr-initialized", "dapr-events",
                     "commands.jsonl", "tests-completed"):
            (self.root / name).unlink(missing_ok=True)
        result = self.harness(dapr_archive=archive, mutate_workflow=drop_dapr_forwarding)
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse((self.root / "dapr-events").exists())
        self.assertFalse((self.root / "hook-after-integration-tests").exists())
        self.assertFalse(any("IntegrationTests" in " ".join(command)
                             for command in self.fixture.commands() if command[0] == "test"))

    def test_successful_nested_password_print_is_observed_and_rejected_by_secrecy_assertion(self):
        self.fixture.document.update(registry=True)
        docker = self.fixture.bin / "docker"
        docker.write_text(docker.read_text().replace('if name=="pwsh":',
            'if name=="docker":\n    print(os.environ["REGISTRY_PULL_PASSWORD"])\n'
            '    print("nested-stderr-marker", file=sys.stderr)\nif name=="pwsh":'))
        result = self.harness(secrets={"registry-pull-username": "pull-user",
                                       "registry-pull-password": "deliberately-printed-password"})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("nested-stderr-marker", result.stderr)
        with self.assertRaises(AssertionError):
            self.assertNotIn("deliberately-printed-password", result.stdout + result.stderr)

    def test_hook_changes_to_config_runner_or_classification_fail_before_integration(self):
        mutations = {
            "solution-none": "p=Path('.github/hexalith-ci.json'); p.write_text(" + repr(json.dumps(FIXTURE.configuration("none"))) + ")",
            "configuration": "p=Path('.github/hexalith-ci.json'); d=json.loads(p.read_text()); d['browsers']=True; p.write_text(json.dumps(d))",
            "classification": "p=Path('fake-sdk.json'); d=json.loads(p.read_text()); del d['projects']['test/Fixture.IntegrationTests.csproj']; p.write_text(json.dumps(d))",
            "runner": "p=Path('global.json'); d=json.loads(p.read_text()); d['test']['runner']='VSTest'; p.write_text(json.dumps(d))",
        }
        original_global = (self.root / "global.json").read_bytes()
        for name, mutation in mutations.items():
            with self.subTest(mutation=name):
                shutil.rmtree(self.root / ".github/actions", ignore_errors=True)
                (self.root / "global.json").write_bytes(original_global)
                for relative in ("hook-after-unit-tests", "hook-after-integration-tests", "commands.jsonl", "tests-completed"):
                    (self.root / relative).unlink(missing_ok=True)
                self.fixture.document.update(hooks=["after-unit-tests", "after-integration-tests"])
                self.hook("after-unit-tests")
                self.hook("after-integration-tests")
                path = self.root / ".github/actions/after-unit-tests/action.yml"
                action = yaml.safe_load(path.read_text())
                action["runs"]["steps"].append({"shell": "bash", "run": "python3 -c " + shlex.quote(
                    "import json; from pathlib import Path; " + mutation)})
                path.write_text(yaml.safe_dump(action))
                result = self.harness()
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                self.assertIn("changed after unit tests", result.stderr)
                self.assertFalse((self.root / "hook-after-integration-tests").exists())
                self.assertFalse(any("IntegrationTests" in " ".join(command)
                                     for command in self.fixture.commands() if command[0] == "test"))

    def test_minimal_module_still_runs_integration_and_dormant_hooks_stay_dormant(self):
        self.hook("after-unit-tests", fail=True)
        self.hook("after-integration-tests", fail=True)
        result = self.harness()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(3, sum(command[0] == "test" for command in self.fixture.commands()))
        self.assertFalse(self.effects.exists())
        self.assertFalse(any(self.root.glob("hook-*")))

    def test_override_and_enabled_exclusion_execute_no_project_twice(self):
        self.fixture.document.update(hooks=["after-unit-tests"], tests={
            "integration": ["test/Z.Tests/Z.Tests.csproj"],
            "exclude": {"test/A space/A.Tests.csproj": "after-unit-tests"}})
        self.hook("after-unit-tests")
        result = self.harness()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        tests = [command[2] for command in self.fixture.commands() if command[0] == "test"]
        self.assertEqual(["test/Fixture.IntegrationTests.csproj", "test/Z.Tests/Z.Tests.csproj"], tests)
        self.assertEqual(len(tests), len(set(tests)))

    def test_excluded_e2e_project_does_not_enable_browser_setup(self):
        project = "test/Browser.E2E.csproj"
        self.fixture.data["projects"][project] = True
        (self.root / project).write_text("<Project />")
        self.fixture.document.update(hooks=["after-unit-tests"], tests={
            "integration": [], "exclude": {project: "after-unit-tests"}})
        self.hook("after-unit-tests")
        result = self.harness()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse(self.effects.exists())

    def test_actual_action_failures_stop_every_successor_and_preserve_status(self):
        for failure, code in (("unit", 23), ("environment", 29), ("dapr", 37), ("registry", 29),
                              ("hook", 31), ("integration", 23), ("final-hook", 31)):
            with self.subTest(failure=failure):
                # Each boundary needs its own fresh command/event ledger.
                for path in self.root.glob("hook-*"):
                    path.unlink()
                for name in ("commands.jsonl", "tests-completed", "environment-effects.jsonl"):
                    (self.root / name).unlink(missing_ok=True)
                self.fixture.data.pop("fail_project", None)
                self.fixture.document.update(browsers=True, dapr=failure == "dapr", registry=failure == "registry",
                                             hooks=["after-unit-tests", "after-integration-tests"])
                self.built_playwright()
                for name in ("after-unit-tests", "after-integration-tests"):
                    directory = self.root / ".github/actions" / name
                    if directory.exists():
                        (directory / "action.yml").unlink()
                        directory.rmdir()
                    self.hook(name, fail=(failure == "hook" and name == "after-unit-tests")
                              or (failure == "final-hook" and name == "after-integration-tests"))
                self.fixture.environment.pop("FAIL_ENVIRONMENT_TOOL", None)
                self.fixture.environment.pop("FAIL_DAPR_INIT", None)
                if failure == "unit":
                    self.fixture.data["fail_project"] = "test/A space/A.Tests.csproj"
                elif failure == "environment":
                    self.fixture.environment["FAIL_ENVIRONMENT_TOOL"] = "pwsh"
                elif failure == "dapr":
                    self.fixture.environment["FAIL_DAPR_INIT"] = "true"
                elif failure == "registry":
                    self.fixture.environment["FAIL_ENVIRONMENT_TOOL"] = "docker"
                elif failure == "integration":
                    self.fixture.data["fail_project"] = "test/Fixture.IntegrationTests.csproj"
                result = self.harness(dapr_archive=self.controlled_dapr_archive() if failure == "dapr" else None,
                                      secrets={"registry-pull-username": "pull-user", "registry-pull-password": "pull-secret"})
                self.assertEqual(code, result.returncode, result.stdout + result.stderr)
                self.assertEqual(failure == "final-hook", (self.root / "hook-after-integration-tests").exists())
                self.assertEqual(failure in ("hook", "integration", "final-hook"), (self.root / "hook-after-unit-tests").exists())
                tests = [command for command in self.fixture.commands() if command[0] == "test"]
                self.assertEqual({"unit": 1, "environment": 2, "dapr": 2, "registry": 2,
                                  "hook": 2, "integration": 3, "final-hook": 3}[failure], len(tests))
                if failure == "unit":
                    self.assertFalse(self.effects.exists())

    def test_e2e_inference_from_successful_units_reaches_environment_action(self):
        project = "test/Browser.E2E.csproj"
        self.fixture.data["projects"][project] = True
        (self.root / project).write_text("<Project />")
        self.built_playwright(project=project)
        result = self.harness()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue((self.root / "pwsh-ready").exists())

    def sample_fixture(self):
        fixture = self.root / "Samples/Hexalith.Sample"
        fixture.mkdir(parents=True)
        # Move only module inputs/outputs; actions intentionally stay at workspace root.
        self.fixture.document.update(buildsTag="v1.0.0", hooks=["after-unit-tests"])
        self.save_fixture()
        for name in (".github/hexalith-ci.json", "global.json", "Fixture.slnx", "fake-sdk.json"):
            destination = fixture / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes((self.root / name).read_bytes())
        for project in self.fixture.data["projects"]:
            destination = fixture / project
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes((self.root / project).read_bytes())
        # This root hook observes the fixture build via a relative workspace path.
        action = self.root / ".github/actions/after-unit-tests"
        action.mkdir(parents=True)
        (action / "action.yml").write_text(yaml.safe_dump({"runs": {"using": "composite", "steps": [
            {"shell": "bash", "run": "test -f Samples/Hexalith.Sample/build-completed && touch hook-after-unit-tests"}]}}))
        expected = fixture / "expected-source/rendered"
        (expected / "Hexalith.Sample/.github").mkdir(parents=True)
        (expected / "Hexalith.Sample/.github/hexalith-ci.json").write_bytes(
            (fixture / ".github/hexalith-ci.json").read_bytes())
        (expected / "Hexalith.Sample.perimeter.json").write_text(json.dumps({
            "schema": "hexalith.perimeter.v1", "entries": [{"kind": "whole", "path": ".github/hexalith-ci.json"}]}))
        return fixture

    def test_module_root_hooks_resolve_from_workspace_root(self):
        self.sample_fixture()
        result = CompositeHarness(self.root, self.fixture.environment, module_root="Samples/Hexalith.Sample",
                                  expected_source="Samples/Hexalith.Sample/expected-source").run()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertTrue((self.root / "hook-after-unit-tests").is_file())

    def test_shared_sample_guard_blocks_committed_owned_drift_before_prepare_build_or_hooks(self):
        fixture = self.sample_fixture()
        path = fixture / ".github/hexalith-ci.json"
        document = json.loads(path.read_text())
        document["codeql"] = ["ruby"]
        path.write_text(json.dumps(document))
        harness = CompositeHarness(self.root, self.fixture.environment, module_root="Samples/Hexalith.Sample",
                                   expected_source="Samples/Hexalith.Sample/expected-source")
        result = harness.run()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("CI/CD is owned by Hexalith.CiCd. Fix the product code, not the pipeline.", result.stderr)
        self.assertIn(".github/hexalith-ci.json", result.stderr)
        self.assertNotIn("prepare", harness.context["steps"])
        self.assertFalse((fixture / "commands.jsonl").exists())
        self.assertFalse((fixture / "build-completed").exists())
        self.assertFalse((self.root / "hook-after-unit-tests").exists())
        self.assertFalse(self.effects.exists())

    def test_no_solution_shared_job_never_calls_sdk_environment_or_hooks(self):
        self.fixture.document.update(solution="none", registry=True)
        (self.root / "global.json").unlink()
        result = self.harness()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.fixture.commands())
        self.assertFalse(self.effects.exists())


if __name__ == "__main__":
    unittest.main()
