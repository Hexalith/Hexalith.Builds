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


def install_guard_tool_doubles(directory, workspace):
    """Control external tools while executing the real guard's tool orchestration.

    These subprocess controls prove argv/stdin isolation and fail-fast wiring.
    They do not claim npm signature, YAML-version, or conventional-policy evidence.
    """
    directory.mkdir(parents=True, exist_ok=True)
    ledger = workspace / "guard-tools.jsonl"
    common = f"#!{sys.executable}\n" + f'''
import json, os, pathlib, sys
workspace=pathlib.Path({str(workspace)!r})
ledger=pathlib.Path({str(ledger)!r})
args=sys.argv[1:]
name=pathlib.Path(sys.argv[0]).name
cwd=pathlib.Path.cwd()
assert not cwd.is_relative_to(workspace), "tools installed inside module"
assert not any(k.upper().startswith(("GITHUB_", "GH_", "NODE_")) for k in os.environ)
assert {{k for k in os.environ if k.upper().startswith("GIT_")}} <= {{"GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "GIT_TERMINAL_PROMPT"}}
assert not any("secret-marker" in value or "poison-marker" in value for value in os.environ.values())
data=sys.stdin.read() if name=="node" else ""
with ledger.open("a") as stream:
    stream.write(json.dumps({{"tool":name,"args":args,"cwd":str(cwd),"stdin":data}})+"\\n")
if name=="npm":
    assert args in (["ci", "--ignore-scripts"], ["audit", "signatures"]), args
    package=json.loads((cwd / "package.json").read_text())
    assert package["dependencies"]=={{"@commitlint/cli":"21.2.4","@commitlint/config-conventional":"21.2.4","js-yaml":"4.3.2"}}
    if args==["audit", "signatures"] and (workspace / "fail-guard-signatures").exists():
        print("private-tool-error\\x1b[31m", file=sys.stderr)
        sys.exit(31)
elif args[0].endswith("js-yaml.js"):
    import yaml
    assert data.startswith("# guard YAML input\\n")
    print(json.dumps(yaml.safe_load(data)))
elif args[0].endswith("cli.js"):
    assert "--config" in args and "--color=false" in args
    config=pathlib.Path(args[args.index("--config")+1])
    assert config.is_file() and not config.is_relative_to(workspace)
    if (workspace / "fail-guard-commitlint").exists():
        print("controlled failure [fixture-controlled-rule]", file=sys.stderr)
        sys.exit(32)
else:
    raise AssertionError(args)
'''
    for name in ("npm", "node"):
        executable = directory / name
        executable.write_text(common)
        executable.chmod(0o755)
    return directory


class CompositeHarness:
    """Run the real shared job/composites in a disposable workspace.

    This small Actions interpreter supports this job's literal steps and gates.
    External checkout/SDK actions are supplied by the fixture, while every local
    composite and enabled hook is actually executed with its own action path.
    """

    def __init__(self, root, environment, *, module_root="", expected_source="", secrets=None,
                 dapr_archive=None, mutate_workflow=None, runner_os="Linux", external_action=None,
                 event_name="workflow_dispatch", event=None, repository=FIXTURE.BUILDS, runner_environment="github-hosted"):
        self.root = root
        self.environment = dict(environment, GITHUB_WORKSPACE=str(root))
        self.environment.pop("CI_EVENT_NAME", None)
        self.environment.pop("CI_EVENT_PATH", None)
        self.path = self.environment["PATH"]
        self.stdout = []
        self.stderr = []
        self.mutate_workflow = mutate_workflow
        self.external_action = external_action
        self.external_calls = []
        tools = install_guard_tool_doubles(root / "guard-tool-controls", root)
        self.path = str(tools) + os.pathsep + self.path
        runner_temp = root / "runner-temp"
        runner_temp.mkdir(exist_ok=True)
        self.environment["RUNNER_TEMP"] = str(runner_temp)
        self.environment["GIT_CONFIG_GLOBAL"] = str(runner_temp / "git-global-config")
        self.environment["GIT_CONFIG_NOSYSTEM"] = "1"
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
        event_path = runner_temp / "event.json"
        event_path.write_text(json.dumps({} if event is None else event))
        self.environment.update(GITHUB_EVENT_NAME=event_name, GITHUB_EVENT_PATH=str(event_path))
        self.context = {"github": {"repository": repository, "event_name": event_name,
                                    "event": {} if event is None else event},
                        "runner": {"temp": str(runner_temp), "os": runner_os, "environment": runner_environment},
                        "inputs": {"module-root": module_root, "expected-source": expected_source},
                        "secrets": secrets or {"registry-pull-username": "", "registry-pull-password": ""},
                        "steps": {}}
        self.counter = 0
        self.exported_environment = {}

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
        expression = expression.strip()
        if expression.startswith("${{") and expression.endswith("}}"):
            expression = expression[3:-2].strip()
        if " || " in expression:
            return any(self.enabled(part, context) for part in expression.split(" || "))
        if " && " in expression:
            return all(self.enabled(part, context) for part in expression.split(" && "))
        match = re.fullmatch(r"(.*?) (==|!=) '([^']*)'", expression)
        if not match:
            raise AssertionError(f"unsupported harness condition: {expression}")
        actual = self.value(match[1], context)
        return (actual == match[3]) == (match[2] == "==")

    def steps(self, steps, context, environment):
        for step in steps:
            context["env"] = environment
            if "if" in step and not self.enabled(step["if"], context):
                continue
            env = dict(environment)
            env.update(self.exported_environment)
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
                    inputs = {name: self.expand(value, context) for name, value in step.get("with", {}).items()}
                    self.external_calls.append((reference, inputs, dict(env)))
                    if reference.startswith("actions/setup-node@"):
                        assert reference == "actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1", reference
                        assert inputs.get("node-version") == "24", inputs
                        assert inputs.get("package-manager-cache") == "False" or inputs.get("package-manager-cache") == "false", inputs
                        assert not inputs.get("cache"), inputs
                    if self.external_action:
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
                outputs = {name: self.expand(entry["value"], child)
                           for name, entry in document.get("outputs", {}).items()}
                if "id" in step:
                    context["steps"][step["id"]] = {"outputs": outputs}
                if result.returncode:
                    return result
            else:
                self.counter += 1
                output = self.root / f"action-output-{self.counter}"
                path = self.root / f"action-path-{self.counter}"
                exported = self.root / f"action-env-{self.counter}"
                output.unlink(missing_ok=True)
                path.unlink(missing_ok=True)
                exported.unlink(missing_ok=True)
                env.update(GITHUB_OUTPUT=str(output), GITHUB_PATH=str(path), GITHUB_ENV=str(exported))
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
                if output.exists():
                    outputs = dict(line.split("=", 1) for line in output.read_text().splitlines())
                if path.exists():
                    self.path = os.pathsep.join(path.read_text().splitlines()) + os.pathsep + self.path
                if exported.exists():
                    self.exported_environment.update(line.split("=", 1) for line in exported.read_text().splitlines())
                if "id" in step:
                    context["steps"][step["id"]] = {"outputs": outputs}
                if result.returncode:
                    return self.result(result.returncode)
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
        result = self.steps(document["runs"]["steps"], context,
                            dict(self.environment, GITHUB_ACTION_PATH=str(action)))
        self.action_outputs = {name: self.expand(entry["value"], context)
                               for name, entry in document.get("outputs", {}).items()}
        return result


class EnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURE.SubprocessPipelineTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        self.effects = self.root / "environment-effects.jsonl"
        self.fixture.environment.update(GITHUB_REPOSITORY=FIXTURE.BUILDS, GITHUB_RUN_ID="1234",
                                        GITHUB_RUN_ATTEMPT="2", GITHUB_JOB="ci")
        # Real subprocess doubles record observable effects, not Python call counts.
        for name in ("pwsh", "docker"):
            script = self.fixture.bin / name
            script.write_text(f"#!{sys.executable}\n" + r'''
import datetime, json, os, pathlib, sys
root=pathlib.Path.cwd()
name=pathlib.Path(sys.argv[0]).name
args=sys.argv[1:]
if name=="docker":
    now=datetime.datetime.now(datetime.timezone.utc).isoformat()
    if args[:2]==["volume", "ls"]:
        print("old-volume")
        sys.exit(0)
    if args[:2]==["ps", "--all"]:
        print("a"*64)
        if (root / "dapr-initialized").exists() or (root / "dapr-init").exists():
            print("b"*64)
        sys.exit(0)
    if args[0]=="inspect":
        record={"Id":"b"*64,"Name":"/dapr_redis","Created":now,"State":{"Running":True},
                "Config":{"Image":"redis:7","Env":["private-marker"]},
                "Mounts":[{"Type":"volume","Name":"fresh-volume","Source":"/var/lib/docker/volumes/fresh-volume/_data",
                           "Destination":"/data","RW":True}]}
        print(json.dumps([record]))
        if os.environ.get("FAIL_DOCKER_INSPECT"):
            print("private-marker-from-inspect-stderr", file=sys.stderr)
            sys.exit(55)
        sys.exit(0)
    if args[:2]==["volume", "inspect"]:
        print(json.dumps([{"Name":"fresh-volume","Driver":"local","Scope":"local","CreatedAt":now,
                           "Mountpoint":"/var/lib/docker/volumes/fresh-volume/_data"}]))
        sys.exit(0)
assert (root / "build-completed").read_text()=="Release"
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
    import json
    receipt=pathlib.Path(os.environ["RUNNER_TEMP"]) / "hexalith-dapr-initialization.json"
    document=json.loads(receipt.read_text())
    assert document["status"]=="initializing" and document["preexistingVolumes"]==["old-volume"]
    assert "HEXALITH_DAPR_INITIALIZATION_RECEIPT" not in os.environ
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
            "assert pathlib.Path(shutil.which('dapr')).is_relative_to(pathlib.Path(os.environ['RUNNER_TEMP'])); "
            "import json; p=pathlib.Path(os.environ['HEXALITH_DAPR_INITIALIZATION_RECEIPT']); "
            "assert p.is_relative_to(pathlib.Path(os.environ['RUNNER_TEMP'])); "
            "assert json.loads(p.read_text())['status']=='complete'\" && dapr probe"})
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
        runner = self.root / "first-dapr-runner"
        runner.mkdir()
        environment = dict(self.fixture.environment, GITHUB_PATH=str(path), RUNNER_TEMP=str(runner))
        with mock.patch.object(ENV, "urlopen", return_value=io.BytesIO(valid)) as download, \
                mock.patch.dict(ENV.DAPR_CLI_DIGESTS, {"1.19.1": hashlib.sha256(valid).hexdigest()}), \
                mock.patch.dict(os.environ, environment):
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
        runner = self.root / "second-dapr-runner"
        runner.mkdir()
        environment = dict(self.fixture.environment, GITHUB_PATH=str(path), RUNNER_TEMP=str(runner),
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

    def receipt_fixture(self, **extra):
        runner = self.root / "receipt-runner"
        runner.mkdir(exist_ok=True)
        archive = self.controlled_dapr_archive()
        binary = ENV.install_dapr_cli(archive, hashlib.sha256(archive).hexdigest(), self.root / "receipt-bin")
        environment = dict(self.fixture.environment, RUNNER_TEMP=str(runner),
                           GITHUB_ENV=str(runner / "github-env"), **extra)
        return runner, binary, environment

    def test_receipt_precedes_init_and_exports_only_selected_actual_resource_metadata(self):
        runner, binary, environment = self.receipt_fixture()
        with mock.patch.dict(os.environ, environment):
            receipt = ENV.initialize_dapr(binary, "1.19.1", self.root)
            self.assertEqual(str(receipt), os.environ[ENV.DAPR_RECEIPT_ENV])
        document = json.loads(receipt.read_text())
        self.assertEqual("hexalith.dapr-initialization.v1", document["schema"])
        self.assertEqual("complete", document["status"])
        self.assertEqual({"repository": FIXTURE.BUILDS, "runId": "1234", "runAttempt": "2", "job": "ci"},
                         document["identity"])
        self.assertEqual(["old-volume"], document["preexistingVolumes"])
        self.assertEqual(["a" * 64], document["preexistingContainerIds"])
        self.assertEqual(["b" * 64], [item["Id"] for item in document["createdContainers"]])
        container = document["createdContainers"][0]
        self.assertEqual("/dapr_redis", container["Name"])
        self.assertTrue(container["Running"])
        self.assertEqual("fresh-volume", container["Mounts"][0]["Name"])
        volume = document["mountedVolumes"][0]
        self.assertEqual(("fresh-volume", "local", "local"), (volume["Name"], volume["Driver"], volume["Scope"]))
        self.assertLessEqual(document["startedAt"], container["Created"])
        self.assertLessEqual(container["Created"], document["completedAt"])
        self.assertLessEqual(volume["CreatedAt"], document["completedAt"])
        self.assertEqual(0o600, receipt.stat().st_mode & 0o777)
        self.assertNotIn("private-marker", receipt.read_text())
        self.assertEqual(f"{ENV.DAPR_RECEIPT_ENV}={receipt}\n", (runner / "github-env").read_text())
        self.assertEqual(["init --runtime-version 1.19.1"], (self.root / "dapr-events").read_text().splitlines())

    def test_retained_old_malformed_mismatched_and_symlink_receipts_are_never_overwritten(self):
        runner, binary, environment = self.receipt_fixture()
        receipt = runner / ENV.DAPR_RECEIPT_NAME
        for retained in (b"", b"{malformed", b'{"status":"complete","identity":{"runId":"old"}}'):
            with self.subTest(retained=retained), mock.patch.dict(os.environ, environment):
                receipt.write_bytes(retained)
                with self.assertRaisesRegex(ENV.PipelineError, "exclusively create"):
                    ENV.initialize_dapr(binary, "1.19.1", self.root)
                self.assertEqual(retained, receipt.read_bytes())
                receipt.unlink()
        target = runner / "private-evidence"
        target.write_text("retain")
        receipt.symlink_to(target)
        with mock.patch.dict(os.environ, environment), self.assertRaisesRegex(ENV.PipelineError, "exclusively create"):
            ENV.initialize_dapr(binary, "1.19.1", self.root)
        self.assertEqual("retain", target.read_text())
        self.assertFalse((self.root / "dapr-events").exists())
        self.assertFalse((runner / "github-env").exists())

    def test_repeated_init_and_supplied_receipt_paths_refuse_before_another_init(self):
        runner, binary, environment = self.receipt_fixture()
        with mock.patch.dict(os.environ, environment):
            receipt = ENV.initialize_dapr(binary, "1.19.1", self.root)
            original = receipt.read_bytes()
            with self.assertRaisesRegex(ENV.PipelineError, "already supplied"):
                ENV.initialize_dapr(binary, "1.19.1", self.root)
            os.environ.pop(ENV.DAPR_RECEIPT_ENV)
            with self.assertRaisesRegex(ENV.PipelineError, "exclusively create"):
                ENV.initialize_dapr(binary, "1.19.1", self.root)
            self.assertEqual(original, receipt.read_bytes())
        self.assertEqual(1, len((self.root / "dapr-events").read_text().splitlines()))

    def test_missing_or_noncanonical_job_identity_refuses_before_receipt_or_init(self):
        runner, binary, environment = self.receipt_fixture()
        for variable, invalid in (("GITHUB_REPOSITORY", "missing"), ("GITHUB_RUN_ID", "0"),
                                  ("GITHUB_RUN_ATTEMPT", "01"), ("GITHUB_JOB", "ci\nother")):
            with self.subTest(variable=variable), mock.patch.dict(os.environ, dict(environment, **{variable: invalid})):
                with self.assertRaisesRegex(ENV.PipelineError, variable):
                    ENV.initialize_dapr(binary, "1.19.1", self.root)
        with mock.patch.dict(os.environ, environment):
            os.environ.pop("GITHUB_RUN_ID", None)
            with self.assertRaisesRegex(ENV.PipelineError, "GITHUB_RUN_ID"):
                ENV.initialize_dapr(binary, "1.19.1", self.root)
        self.assertFalse((runner / ENV.DAPR_RECEIPT_NAME).exists())
        self.assertFalse((self.root / "dapr-events").exists())

    def test_failed_init_or_metadata_inspection_leaves_unpublished_incomplete_receipt(self):
        runner, binary, environment = self.receipt_fixture(FAIL_DAPR_INIT="true")
        with mock.patch.dict(os.environ, environment):
            with self.assertRaises(ENV.PipelineError) as caught:
                ENV.initialize_dapr(binary, "1.19.1", self.root)
            self.assertEqual(37, caught.exception.returncode)
            self.assertNotIn(ENV.DAPR_RECEIPT_ENV, os.environ)
        receipt = runner / ENV.DAPR_RECEIPT_NAME
        self.assertEqual("initializing", json.loads(receipt.read_text())["status"])
        self.assertFalse((runner / "github-env").exists())
        receipt.unlink()
        environment.pop("FAIL_DAPR_INIT")
        with mock.patch.dict(os.environ, environment), \
                mock.patch.object(ENV, "dapr_created_resources", side_effect=ENV.PipelineError("malformed metadata")):
            with self.assertRaisesRegex(ENV.PipelineError, "malformed metadata"):
                ENV.initialize_dapr(binary, "1.19.1", self.root)
            self.assertNotIn(ENV.DAPR_RECEIPT_ENV, os.environ)
        self.assertEqual("initializing", json.loads(receipt.read_text())["status"])
        self.assertFalse((runner / "github-env").exists())

    def test_malformed_docker_inventories_and_metadata_fail_closed(self):
        with mock.patch.object(ENV, "docker_output", return_value="duplicate\nduplicate\n"):
            with self.assertRaisesRegex(ENV.PipelineError, "malformed Docker inventory"):
                ENV.docker_inventory(self.root, ["volume", "ls"], r"[a-z]+")
        for raw in ("{malformed", "{}", "[null]"):
            with self.subTest(raw=raw), mock.patch.object(ENV, "docker_output", return_value=raw):
                with self.assertRaisesRegex(ENV.PipelineError, "malformed Docker metadata"):
                    ENV.docker_records(self.root, ["inspect", "b" * 64])
        for created in (None, "yesterday", "2026-11-16T10:00:00", "2026-11-16T10:00:00+01:00"):
            with self.subTest(created=created), self.assertRaisesRegex(ENV.PipelineError, "creation time"):
                ENV.docker_created(created)

    def test_partial_failed_inspection_never_logs_private_metadata_or_exports_a_receipt(self):
        self.fixture.document.update(dapr=True, hooks=["after-unit-tests"])
        self.dapr_hook()
        self.fixture.environment["FAIL_DOCKER_INSPECT"] = "true"
        result = self.harness(dapr_archive=self.controlled_dapr_archive())
        self.assertEqual(55, result.returncode, result.stdout + result.stderr)
        self.assertIn("Docker container inspection failed (55)", result.stderr)
        self.assertNotIn("private-marker", result.stdout + result.stderr)
        receipt = self.root / "runner-temp" / ENV.DAPR_RECEIPT_NAME
        self.assertEqual("initializing", json.loads(receipt.read_text())["status"])
        self.assertNotIn("private-marker", receipt.read_text())
        self.assertFalse((self.root / "hook-after-unit-tests").exists())
        for path in self.root.glob("action-env-*"):
            self.assertNotIn(ENV.DAPR_RECEIPT_ENV, path.read_text())

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
            "exclude": {"test/A space/A.Tests.csproj": "after-unit-tests"},
            "minimumExpectedTests": {}, "failSkips": False})
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
            "integration": [], "exclude": {project: "after-unit-tests"},
            "minimumExpectedTests": {}, "failSkips": False})
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
        self.assertEqual("true", harness.context["steps"]["guard"]["outputs"]["drift"])
        self.assertNotIn("prepare", harness.context["steps"])
        self.assertFalse((fixture / "commands.jsonl").exists())
        self.assertFalse((fixture / "build-completed").exists())
        self.assertFalse((self.root / "hook-after-unit-tests").exists())
        self.assertFalse(self.effects.exists())

    def test_sample_lint_failures_stop_actual_job_before_prepare_sdk_build_tests_and_hooks(self):
        fixture = self.sample_fixture()
        for failure in ("action", "audit", "commitlint", "signatures"):
            with self.subTest(failure=failure):
                for name in ("extensionless",):
                    (fixture / name).unlink(missing_ok=True)
                for name in ("fail-guard-commitlint", "fail-guard-signatures", "guard-tools.jsonl"):
                    (self.root / name).unlink(missing_ok=True)
                shutil.rmtree(self.root / ".github/actions/dormant", ignore_errors=True)
                if failure == "action":
                    action = self.root / ".github/actions/dormant"
                    action.mkdir(parents=True)
                    (action / "action.yaml").write_text("runs: {using: node24}\n")
                elif failure == "audit":
                    (fixture / "extensionless").write_text("<Project><NuGetAuditSuppress Include='fixture'/></Project>")
                else:
                    (self.root / ("fail-guard-" + failure)).touch()
                harness = CompositeHarness(self.root, self.fixture.environment,
                                           module_root="Samples/Hexalith.Sample",
                                           expected_source="Samples/Hexalith.Sample/expected-source")
                result = harness.run()
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                self.assertIn("CI/CD is owned by Hexalith.CiCd.", result.stderr)
                expected = {"action": ".github/actions/dormant/action.yaml", "audit": "extensionless",
                            "signatures": "npm signature verification", "commitlint": "fixture-controlled-rule"}[failure]
                self.assertIn(expected, result.stderr)
                self.assertEqual("false", harness.context["steps"]["guard"]["outputs"]["drift"])
                self.assertNotIn("prepare", harness.context["steps"])
                self.assertFalse(any(reference.startswith("actions/setup-dotnet@")
                                     for reference, _, _ in harness.external_calls))
                self.assertFalse((fixture / "commands.jsonl").exists())
                self.assertFalse((fixture / "build-completed").exists())
                self.assertFalse((self.root / "hook-after-unit-tests").exists())
                self.assertFalse(self.effects.exists())

    def test_shared_sample_job_maps_pr_base_head_and_title_from_real_event_payload(self):
        fixture = self.sample_fixture()

        def git(*arguments):
            result = subprocess.run([shutil.which("git"), "-c", "core.hooksPath=" + os.devnull,
                                     "-c", "user.name=Event fixture", "-c", "user.email=fixture@example.invalid",
                                     "-c", "commit.gpgsign=false", "-C", str(self.root), *arguments],
                                    env=self.fixture.environment, text=True, capture_output=True, check=True)
            return result.stdout.strip()

        git("init", "-q", "-b", "main")
        git("add", "--all", ".github", "Samples")
        git("commit", "-qm", "test: event base")
        base = git("rev-parse", "HEAD")
        (self.root / "outside-sample.txt").write_text("repository-wide PR change")
        git("add", "outside-sample.txt")
        git("commit", "-qm", "fix: range change outside sample")
        head = git("rev-parse", "HEAD")
        merge = git("commit-tree", "HEAD^{tree}", "-p", base, "-p", head,
                    "-m", "synthetic checkout must not be linted")
        git("update-ref", "refs/heads/main", merge)
        event = {"pull_request": {"base": {"sha": base}, "head": {"sha": head},
                                  "title": "fix: shared sample PR title"}}
        harness = CompositeHarness(self.root, self.fixture.environment, module_root="Samples/Hexalith.Sample",
                                   expected_source="Samples/Hexalith.Sample/expected-source",
                                   event_name="pull_request", event=event)
        result = harness.run()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = [json.loads(line) for line in (self.root / "guard-tools.jsonl").read_text().splitlines()]
        selected = [call["stdin"].strip() for call in calls
                    if call["tool"] == "node" and call["args"][0].endswith("cli.js")]
        self.assertEqual(["fix: range change outside sample", "fix: shared sample PR title"], selected)
        self.assertTrue((fixture / "build-completed").is_file())
        self.assertTrue((self.root / "hook-after-unit-tests").is_file())

    def test_no_solution_sample_executes_guard_tools_and_event_policy_without_product_work(self):
        fixture = self.sample_fixture()
        document = FIXTURE.configuration("none")
        document["buildsTag"] = "v1.0.0"
        for path in (fixture / ".github/hexalith-ci.json",
                     fixture / "expected-source/rendered/Hexalith.Sample/.github/hexalith-ci.json"):
            path.write_text(json.dumps(document))
        harness = CompositeHarness(self.root, self.fixture.environment, module_root="Samples/Hexalith.Sample",
                                   expected_source="Samples/Hexalith.Sample/expected-source")
        result = harness.run()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = [json.loads(line) for line in (self.root / "guard-tools.jsonl").read_text().splitlines()]
        self.assertEqual(2, sum(call["tool"] == "npm" for call in calls))
        self.assertEqual(1, sum(call["tool"] == "node" and call["args"][0].endswith("cli.js") for call in calls))
        self.assertFalse((fixture / "commands.jsonl").exists())
        self.assertFalse((self.root / "hook-after-unit-tests").exists())
        self.assertFalse(self.effects.exists())

    def test_no_solution_shared_job_never_calls_sdk_environment_or_hooks(self):
        self.fixture.document.update(solution="none", registry=True)
        (self.root / "global.json").unlink()
        result = self.harness()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.fixture.commands())
        self.assertFalse(self.effects.exists())


class NativeAspireTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="native-aspire-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def archive(self, payload=b"authenticated-cli"):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w:gz") as archive:
            item = tarfile.TarInfo("aspire")
            item.size = len(payload)
            archive.addfile(item, io.BytesIO(payload))
        return stream.getvalue()

    def test_both_archive_and_executable_are_authenticated_before_execution(self):
        data = self.archive()
        directory = self.root / "cli"
        with self.assertRaisesRegex(ENV.PipelineError, "archive SHA-256"):
            ENV.install_aspire_cli(data, directory)
        self.assertFalse(directory.exists())
        with mock.patch.object(ENV, "ASPIRE_ARCHIVE_SHA256", hashlib.sha256(data).hexdigest()):
            with self.assertRaisesRegex(ENV.PipelineError, "executable SHA-256"):
                ENV.install_aspire_cli(data, directory)
            with mock.patch.object(ENV, "ASPIRE_BINARY_SHA256", hashlib.sha256(b"authenticated-cli").hexdigest()):
                binary = ENV.install_aspire_cli(data, directory)
                self.assertEqual(b"authenticated-cli", binary.read_bytes())
                self.assertTrue(os.access(binary, os.X_OK))

    def test_embedded_bundle_detects_changed_missing_extra_and_symlink_bytes(self):
        expected, installed = self.root / "expected", self.root / "installed"
        for directory in (expected, installed):
            for name in ("dcp/dcp", "dashboard/Aspire.Dashboard", "dashboard/runtime.json"):
                path = directory / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(name.encode())
        ENV.verify_aspire_bundle(expected, installed)
        changed = installed / "dashboard/runtime.json"
        original = changed.read_bytes()
        changed.write_bytes(b"changed")
        with self.assertRaisesRegex(ENV.PipelineError, "bundle differs"):
            ENV.verify_aspire_bundle(expected, installed)
        changed.unlink()
        with self.assertRaisesRegex(ENV.PipelineError, "bundle differs"):
            ENV.verify_aspire_bundle(expected, installed)
        changed.write_bytes(original)
        extra = installed / "extra"
        extra.touch()
        with self.assertRaisesRegex(ENV.PipelineError, "bundle differs"):
            ENV.verify_aspire_bundle(expected, installed)
        extra.unlink()
        changed.unlink()
        changed.symlink_to(expected / "dashboard/runtime.json")
        with self.assertRaisesRegex(ENV.PipelineError, "symbolic link"):
            ENV.verify_aspire_bundle(expected, installed)

    def prerequisites(self, *, hosted=False, runtimes=None):
        inventory = runtimes or ("Microsoft.NETCore.App 11.0.0 [/sdk]\n"
                                 "Microsoft.AspNetCore.App 11.0.0 [/sdk]\n")
        calls = []
        def command(arguments, root, **kwargs):
            calls.append(arguments)
            if "--list-runtimes" in arguments:
                return inventory
            if "unshare" in arguments and sum("unshare" in call for call in calls) == 1:
                raise ENV.PipelineError("controlled namespace refusal", 27)
            return ""
        with mock.patch.dict(os.environ, {"GITHUB_ACTIONS": "true" if hosted else "false",
                                         "RUNNER_ENVIRONMENT": "github-hosted" if hosted else "self-hosted"}), \
                mock.patch.object(ENV.shutil, "which", return_value="/tool"), \
                mock.patch.object(ENV.ctypes, "CDLL"), \
                mock.patch.object(ENV.ctypes.util, "find_library", return_value="/library"), \
                mock.patch.object(ENV, "run_command", side_effect=command):
            if not hosted and "11.0.1" not in inventory:
                with self.assertRaisesRegex(ENV.PipelineError, "namespace refusal"):
                    ENV.native_prerequisites(self.root)
            else:
                ENV.native_prerequisites(self.root)
        return calls

    def test_namespace_adjustment_requires_disposable_hosted_runner_and_reprobe(self):
        local = self.prerequisites()
        self.assertFalse(any("sysctl" in call for call in local))
        hosted = self.prerequisites(hosted=True)
        self.assertEqual(2, sum("unshare" in call for call in hosted))
        self.assertEqual(1, sum("sysctl" in call for call in hosted))
        self.assertTrue(all(call[:3] == ["timeout", "--signal=TERM", "--kill-after=30s"] for call in hosted))

    def test_exact_managed_runtime_fails_before_namespace_adjustment(self):
        with self.assertRaisesRegex(ENV.PipelineError, "highest installed.*11.0.0"):
            self.prerequisites(hosted=True, runtimes="Microsoft.NETCore.App 11.0.1 [/sdk]\n"
                               "Microsoft.AspNetCore.App 11.0.0 [/sdk]\n")

    def test_environment_enables_native_setup_once_after_dapr_and_propagates_failure(self):
        (self.root / "Fixture.slnx").touch()
        calls = []
        with mock.patch.object(ENV, "setup_dapr", side_effect=lambda root: calls.append("dapr")), \
                mock.patch.object(ENV, "setup_aspire", side_effect=lambda root: calls.append("aspire")):
            ENV.setup_environment(self.root, solution="Fixture.slnx", dapr=True, aspire=True)
        self.assertEqual(["dapr", "aspire"], calls)
        with mock.patch.object(ENV, "setup_dapr"), \
                mock.patch.object(ENV, "setup_aspire", side_effect=ENV.PipelineError("native failure", 29)), \
                mock.patch.object(ENV, "playwright_tools") as browsers:
            with self.assertRaisesRegex(ENV.PipelineError, "native failure") as error:
                ENV.setup_environment(self.root, solution="Fixture.slnx", dapr=True, aspire=True, browsers=True)
            self.assertEqual(29, error.exception.returncode)
            browsers.assert_not_called()

    def test_controlled_install_exports_pinned_tools_and_keeps_module_outputs_untouched(self):
        data = self.archive()
        calls = []
        module = self.root / "module"
        module.mkdir()
        output = module / "built.dll"
        output.write_bytes(b"shared-build")
        path_file, env_file = self.root / "github-path", self.root / "github-env"
        def command(arguments, root, **kwargs):
            self.assertEqual(module, root)
            calls.append(arguments)
            if "setup" in arguments:
                install_root = arguments[arguments.index("--install-path") + 1]
                self.assertIn("ASPIRE_LAYOUT_PATH=" + install_root, arguments)
                target = Path(install_root) / "bundle"
                for name in ("dcp/dcp", "dashboard/Aspire.Dashboard"):
                    path = target / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(name.encode())
            if "venv" in arguments:
                executable = Path(arguments[-1]) / "bin/python"
                executable.parent.mkdir(parents=True)
                executable.touch()
            return ""
        with mock.patch.dict(os.environ, {"RUNNER_TEMP": str(self.root),
                                         "GITHUB_PATH": str(path_file), "GITHUB_ENV": str(env_file)}), \
                mock.patch.object(ENV, "native_prerequisites") as prerequisites, \
                mock.patch.object(ENV, "urlopen", return_value=io.BytesIO(data)) as download, \
                mock.patch.object(ENV.Path, "home", return_value=self.root / "home"), \
                mock.patch.object(ENV, "ASPIRE_ARCHIVE_SHA256", hashlib.sha256(data).hexdigest()), \
                mock.patch.object(ENV, "ASPIRE_BINARY_SHA256", hashlib.sha256(b"authenticated-cli").hexdigest()), \
                mock.patch.object(ENV, "run_command", side_effect=command):
            ENV.setup_aspire(module)
        prerequisites.assert_called_once_with(module)
        download.assert_called_once_with(ENV.ASPIRE_URL, timeout=120)
        self.assertEqual(4, len(calls))
        self.assertTrue(all(call[:3] == ["timeout", "--signal=TERM", "--kill-after=30s"] for call in calls))
        self.assertIn("--require-hashes", calls[-1])
        self.assertIn("--only-binary=:all:", calls[-1])
        self.assertEqual(str(ENV.NATIVE_PYTHON_REQUIREMENTS), calls[-1][-1])
        paths = path_file.read_text().splitlines()
        self.assertEqual(2, len(paths))
        self.assertTrue(all(Path(path).is_relative_to(self.root) for path in paths))
        exported = dict(line.split("=", 1) for line in env_file.read_text().splitlines())
        self.assertEqual(f"{paths[-1]}/python", exported["HEXALITH_CI_PYTHON"])
        qualified = Path(exported["HEXALITH_CI_ASPIRE_QUALIFIED_BUNDLE"])
        self.assertTrue(qualified.is_relative_to(self.root))
        ENV.verify_aspire_bundle(qualified, self.root / "home/.aspire/bundle")
        self.assertEqual(b"shared-build", output.read_bytes())
        self.assertEqual([output], list(module.iterdir()))


if __name__ == "__main__":
    unittest.main()
