"""Executable controls for exact ownership, blocking integration and finalization."""

import importlib.util
import hashlib
import copy
import errno
import json
import os
from pathlib import Path
import subprocess  # nosec B404 -- only a fixed interpreter and this test's temporary fixture are executed.
import signal
import sys
import time
import tempfile
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("integration", Path(__file__).parents[1] / "run_module_integration.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
CI = {
    "GITHUB_ACTIONS": "true",
    "GITHUB_REPOSITORY": "o/r",
    "GITHUB_RUN_ID": "101",
    "GITHUB_RUN_ATTEMPT": "2",
    "GITHUB_JOB": "ci",
    "RUNNER_ENVIRONMENT": "github-hosted",
    "RUNNER_OS": "Linux",
}


FAKE_TOOL = r'''
import fcntl, json, os, pathlib, signal, subprocess, sys, time
root = pathlib.Path.cwd()
args = sys.argv[1:]
scenario = os.environ.get('FAKE_SCENARIO', 'success')
if '--version' in args:
    print('9.9.9')
    sys.exit(0)
owner = json.loads(os.environ['FAKE_CI_OWNER'])
def resources(**changes):
    value = {key: [] for key in ('containerIds', 'processIds', 'workspaceIds', 'claimIds', 'discoveryFailures')}
    value['unverified'] = False
    value.update(changes)
    return value
if 'down' in args:
    number = len(list(root.glob('actual-down-*.json'))) + 1
    record = {'runId': owner['runId'], 'jobStatus': os.environ.get('HEXALITH_CI_JOB_STATUS'), 'args': args}
    lock = os.environ.get('FAKE_LOCK')
    if lock:
        handle = open(lock, 'a+')
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            record['locked'] = True
            (root / f'actual-down-{number}.json').write_text(json.dumps(record))
            print(json.dumps({'runId': owner['runId'], 'outcome': {'exitCode': 'TopologyOrLifecycle', 'ruleId': 'HXR037'},
                              'remainingResources': resources(unverified=True)}))
            sys.exit(3)
    if scenario == 'owned-survivor':
        apphost = int((root / 'actual-apphost-ready').read_text())
        record['startedAtNs'] = time.time_ns()
        try:
            os.kill(apphost, 0)
            record['appHostAlive'] = True
        except ProcessLookupError:
            record['appHostAlive'] = False
        (root / f'actual-down-{number}.json').write_text(json.dumps(record))
        if record['appHostAlive']:
            # The real down stops owned processes only after it recorded attachment withdrawal.
            os.kill(apphost, signal.SIGTERM)
            while (root / 'actual-apphost-alive').exists():
                time.sleep(0.01)
    if scenario == 'binary-down':
        sys.stdout.buffer.write(b'\xff\xfe not utf-8')
        (root / f'actual-down-{number}.json').write_text(json.dumps(record))
        sys.exit(0)
    (root / f'actual-down-{number}.json').write_text(json.dumps(record))
    print(json.dumps({'runId': owner['runId'], 'outcome': {'exitCode': 'Success'}, 'remainingResources': resources()}))
    sys.exit(0)
assert 'test' in args
receipt = pathlib.Path(next(arg.split('=', 1)[1] for arg in args if arg.startswith('--ci-owner-receipt=')))
(root / 'actual-tool-args.json').write_text(json.dumps(args))
(root / 'actual-tool-stdin.json').write_text(json.dumps(sys.stdin.read()))
def write_receipt():
    receipt.write_text(json.dumps(owner))
def report(code):
    print(json.dumps({'outcome': {'exitCode': code}}), flush=True)
if scenario == 'success':
    write_receipt(); report('Success'); sys.exit(0)
if scenario == 'success-without-receipt':
    report('Success'); sys.exit(0)
if scenario == 'zero-not-success':
    write_receipt(); report('ProductOrTest'); sys.exit(0)
if scenario == 'failure':
    write_receipt(); report('ProductOrTest'); sys.exit(17)
if scenario == 'invalid-output':
    write_receipt()
    sys.stderr.write('tool failed: password=hunter2 ' + 'Q' * 48 + '\n')
    print('interrupted')
    sys.exit(1)
def interrupted(signum, frame):
    (root / 'actual-tool-signal').write_text(str(signum))
    report('Cancelled')
    sys.exit(130)
def start_apphost():
    """A composed child of the tool, tagged with the owner run like the real AppHost; it records its first signal."""
    code = ("import os, pathlib, signal, sys, time\n"
            "root = pathlib.Path(sys.argv[1])\n"
            "def first(signum, frame):\n"
            "    path = root / 'actual-apphost-signal'\n"
            "    if not path.exists():\n"
            "        path.write_text(f'{signum} {time.time_ns()}')\n"
            "    (root / 'actual-apphost-alive').unlink(missing_ok=True)\n"
            "    os._exit(0)\n"
            "signal.signal(signal.SIGINT, first)\n"
            "signal.signal(signal.SIGTERM, first)\n"
            "(root / 'actual-apphost-alive').write_text('1')\n"
            "(root / 'actual-apphost-ready').write_text(str(os.getpid()))\n"
            "while True:\n"
            "    time.sleep(0.02)\n")
    environment = dict(os.environ, HEXALITH_G4_RUN_ID=owner['runId'])
    child = subprocess.Popen([sys.executable, '-c', code, str(root)], env=environment)
    while not (root / 'actual-apphost-ready').exists():
        time.sleep(0.01)
    return child
if scenario == 'muxer-apphost' and os.environ.get('FAKE_ROLE') != 'tool':
    # The gated `dotnet tool run` muxer: it keeps waiting on SIGINT and forwards SIGTERM to the tool it runs.
    tool = subprocess.Popen([sys.argv[0]] + args, env=dict(os.environ, FAKE_ROLE='tool'))
    signal.signal(signal.SIGINT, lambda signum, frame: None)
    signal.signal(signal.SIGTERM, lambda signum, frame: tool.send_signal(signal.SIGTERM))
    while True:
        try:
            sys.exit(tool.wait())
        except InterruptedError:
            continue
if scenario == 'muxer-apphost':
    write_receipt()
    apphost = start_apphost()
    def withdraw_then_stop(signum, frame):
        (root / 'actual-tool-signal').write_text(str(signum))
        (root / 'actual-withdrawn').write_text(str(time.time_ns()))
        apphost.send_signal(signal.SIGTERM)
        apphost.wait()
        report('Cancelled')
        sys.exit(130)
    signal.signal(signal.SIGINT, withdraw_then_stop)
    signal.signal(signal.SIGTERM, withdraw_then_stop)
if scenario == 'owned-survivor':
    # The tool fails after a refused withdrawal and deliberately leaves its owned AppHost for the exact down.
    write_receipt()
    start_apphost()
    report('TopologyOrLifecycle')
    os._exit(1)
if scenario == 'ignore-all':
    def logged(signum, frame):
        with open(root / 'actual-tool-signals', 'a') as stream:
            stream.write(f'{signum} {time.monotonic()}\n')
    signal.signal(signal.SIGINT, logged)
    signal.signal(signal.SIGTERM, logged)
    write_receipt()
if scenario in ('wait', 'wait-before-receipt'):
    signal.signal(signal.SIGINT, interrupted)
    signal.signal(signal.SIGTERM, interrupted)
    if scenario == 'wait':
        write_receipt()
elif scenario == 'timeout':
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    signal.signal(signal.SIGTERM, interrupted)
    write_receipt()
elif scenario == 'stubborn-late-receipt':
    def late(signum, frame):
        write_receipt()
        (root / 'actual-tool-signal').write_text(str(signum))
    signal.signal(signal.SIGINT, late)
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
elif scenario == 'lock-orphan':
    write_receipt()
    if os.fork() == 0:
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        handle = open(os.environ['FAKE_LOCK'], 'a+')
        fcntl.flock(handle, fcntl.LOCK_EX)
        (root / 'actual-orphan-ready').write_text(str(os.getpid()))
        while True:
            time.sleep(0.05)
    while not (root / 'actual-orphan-ready').exists():
        time.sleep(0.01)
    report('Success')
    sys.exit(0)
(root / 'actual-tool-ready').write_text(str(os.getpid()))
while True:
    time.sleep(0.02)
'''


class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.runner_temp = self.root / "runner-temp"
        self.runner_temp.mkdir()
        values = dict(
            CI,
            XDG_DATA_HOME=str(self.root / "data"),
            GITHUB_WORKSPACE=str(self.root),
            RUNNER_TEMP=str(self.runner_temp),
        )
        self.environment = patch.dict(os.environ, values, clear=False)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.state = self.root / "data" / "hexalith-builds" / "g4-runs"
        self.state.mkdir(parents=True)
        self.receipt = self.runner_temp / "hexalith-ci-owner.json"
        self.evidence = self.root / "TestResults" / "module-integration"
        self.owner = {
            "schema": helper.SCHEMA,
            "runId": "a" * 32,
            "attemptId": "b" * 32,
            "environmentId": "c" * 32,
            "executionContext": helper.ci_context(),
            "stateDirectory": str(self.state),
        }
        (self.root / "module.json").write_text(
            json.dumps({"modules": [{"tests": [{"name": "real", "mode": "package"}]}]})
        )

    def write_receipt(self):
        self.receipt.write_text(json.dumps(self.owner))

    def fake_tool(self, scenario, **extra):
        """Install a fake `dotnet` on PATH that records how the gated tool and its exact down were invoked."""
        tools = self.root / "fake-tools"
        tools.mkdir(exist_ok=True)
        executable = tools / "dotnet"
        executable.write_text("#!" + sys.executable + "\n" + FAKE_TOOL)
        executable.chmod(0o700)
        values = {"PATH": str(tools) + os.pathsep + os.environ["PATH"], "FAKE_SCENARIO": scenario,
                  "FAKE_CI_OWNER": json.dumps(self.owner), **extra}
        return patch.dict(os.environ, values)

    def latest_cleanup(self):
        records = sorted(self.evidence.glob("cleanup-*.json"), key=lambda path: int(path.stem.split("-")[1]))
        return json.loads(records[-1].read_text())

    def evidence_names(self):
        return {path.name for path in self.evidence.iterdir() if not path.name.startswith("cleanup-")}

    def downs(self):
        return [json.loads(path.read_text()) for path in sorted(self.root.glob("actual-down-*.json"))]

    def cleanup_record(self, number):
        return json.loads((self.evidence / helper.CLEANUP_RECORD.format(number)).read_text())

    def reset_job_records(self):
        for path in (self.receipt, helper.process_record_path(self.receipt)):
            path.unlink(missing_ok=True)
        for path in self.root.glob("actual-*"):
            path.unlink()
        if self.evidence.exists():
            for path in self.evidence.iterdir():
                path.unlink()

    def wait_until(self, condition, seconds=10):
        deadline = time.monotonic() + seconds
        while not condition():
            self.assertLess(time.monotonic(), deadline, "The fixture condition was not observed in time.")
            time.sleep(0.01)

    def wait_for(self, path, seconds=10):
        self.wait_until(path.exists, seconds)
        return path

    def workflow_step_body(self, name):
        workflow = (Path(__file__).resolve().parents[3] / ".github/workflows/domain-ci-v2.yml").read_text()
        block = workflow.split("      - name: " + name + "\n", 1)[1].split("\n      - ", 1)[0]
        body = block.split("        run: |\n", 1)[1]
        return "\n".join(line[10:] for line in body.splitlines()) + "\n"

    def step_environment(self, scenario):
        """The exact workflow-owned helper layout, verified-preparation inputs and fake tool for a v2 step."""
        execution = self.root / ".hexalith" / "builds-execution" / "Github"
        for relative in ("run-module-integration/run_module_integration.py", "prepare-module-build/prepare_module_build.py"):
            target = execution / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((Path(helper.__file__).resolve().parents[1] / relative).read_bytes())
        environment = self._real_helper_environment()
        with self.fake_tool(scenario):
            environment.update({key: os.environ[key] for key in ("PATH", "FAKE_SCENARIO", "FAKE_CI_OWNER")})
        environment.update(MODULE_MANIFEST="module.json", INTEGRATION_PROFILES="real", INTEGRATION_REFERENCE_OWNERS="",
                           JOB_TIMEOUT_MINUTES="45", JOB_STARTED_EPOCH=str(int(time.time()) - 300))
        return environment

    def start_step(self, scenario):
        environment = self.step_environment(scenario)
        script = self.root / "integration-step.sh"
        script.write_text(self.workflow_step_body("Runner-owned package integration"))
        return subprocess.Popen(["bash", "--noprofile", "--norc", "-eo", "pipefail", str(script)], cwd=self.root,  # nosec B603
                                env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                start_new_session=True)

    def run_finalizer_step(self, job_status):
        environment = self.step_environment(os.environ.get("FAKE_SCENARIO", "success"))
        environment["CI_JOB_STATUS"] = job_status
        script = self.root / "finalizer-step.sh"
        script.write_text(self.workflow_step_body("Finalize exact CI owner"))
        return subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", str(script)], cwd=self.root,  # nosec B603
                              env=environment, capture_output=True, text=True, timeout=120, check=False)

    def stop_step(self, step):
        if step.poll() is None:
            step.kill()
            step.communicate(timeout=10)
        record = helper.process_record_path(self.receipt)
        if record.exists():
            recorded = json.loads(record.read_text())
            helper.terminate_processes(lambda: helper.session_processes(recorded), recorded["session"], wait=0.5)

    def _real_helper_environment(self):
        configuration = self.root / ".config"
        configuration.mkdir(exist_ok=True)
        (configuration / "dotnet-tools.json").write_text(
            json.dumps(
                {
                    "isRoot": True,
                    "tools": {
                        "hexalith.builds.module.cli": {
                            "version": "9.9.9",
                            "rollForward": False,
                            "commands": ["hexalith-module"],
                        }
                    },
                }
            )
        )
        imports = self.root / "prepared" / "source-mapping"
        imports.mkdir(parents=True, exist_ok=True)
        environment = os.environ.copy()
        for key, filename in helper._prepare.IMPORTS.items():
            path = imports / filename
            path.write_text("<Project />")
            environment[key] = str(path)
        digest = "d" * 64
        (imports / "mapping.json").write_text(json.dumps({"Mode": "package", "HasGit": True, "ContentHash": digest}))
        environment.update(Configuration="Release", HEXALITH_SOURCE_MAPPING_HASH=digest)
        return environment

    def published_diagnostics(self):
        directory = self.state / "diagnostics" / self.owner["runId"]
        directory.mkdir(parents=True, exist_ok=True)
        bodies = {
            "lifecycle": ("lifecycle", ".json", json.dumps({
                "runId": self.owner["runId"], "outcome": "failed", "cause": "test-failure",
                "cleanup": "complete", "discoveryUnverified": False, "discoveryFailures": [],
                "ruleIds": ["HXT001"], "terminalRuleIds": ["HXT001"],
            }, indent=2).encode() + b"\n"),
            "native-report": ("native", ".trx", (
                b'<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                b'<Results><UnitTestResult testName="RequiredNativeFact" outcome="Passed" /></Results></TestRun>\n'
            )),
            "functional-observations": ("functional-observations", ".json", json.dumps({
                "runId": self.owner["runId"], "status": "functional-observations-passed",
                "qualificationCredit": False, "observations": {"persisted": True},
            }, indent=2).encode() + b"\n"),
        }
        references = []
        files = {}
        for identity, (prefix, suffix, body) in bodies.items():
            digest = hashlib.sha256(body).hexdigest()
            filename = prefix + "-" + digest + suffix
            path = directory / filename
            path.write_bytes(body)
            path.chmod(0o600)
            references.append({"id": identity, "classification": "internal", "redacted": True,
                               "reference": filename, "contentIdentity": "sha256:" + digest})
            files[filename] = body
        fixture = Path(__file__).resolve().parents[3] / "test/fixtures/module/test-environment-v1/result.json"
        result = json.loads(fixture.read_text())
        result["bindings"]["run"].update({key: self.owner[key] for key in ("runId", "attemptId", "environmentId")})
        result["bindings"]["run"]["ownerRunId"] = self.owner["runId"]
        result["bindings"]["environment"].update(mode="package", executionContext=self.owner["executionContext"])
        result["terminal"] = {"outcome": "failed", "recordedAt": "2026-11-04T05:28:06Z", "cause": "test-failure", "diagnosticIds": ["lifecycle"]}
        result["diagnostics"] = references
        self.publish_result(directory, result)
        return directory, result, files

    def publish_result(self, directory, result):
        path = directory / "test-result.json"
        path.write_text(json.dumps(result, indent=2))
        path.chmod(0o600)

    def test_published_redacted_diagnostics_survive_cleanup_byte_for_byte_and_retry(self):
        self.write_receipt()
        _, result, files = self.published_diagnostics()
        with patch.object(helper, "verify_tool"), patch.object(
            helper, "run_down", return_value=self.down_result()
        ):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
            for filename, body in files.items():
                with self.subTest(reference=filename):
                    self.assertTrue((self.evidence / filename).is_file(), "Published diagnostic must survive owned cleanup.")
                    self.assertEqual((self.evidence / filename).read_bytes(), body)
            before = {name: (self.evidence / name).stat().st_mtime_ns for name in files if (self.evidence / name).exists()}
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
        self.assertEqual(before, {name: (self.evidence / name).stat().st_mtime_ns for name in before})
        self.assertEqual(json.loads((self.evidence / "test-result.json").read_text())["terminal"], result["terminal"])

    def assert_declared_result_refuses_after_exact_cleanup(self, directory, result, terminal):
        self.publish_result(directory, result)
        with patch.object(helper, "verify_tool"), patch.object(
            helper, "run_down", return_value=self.down_result()
        ) as cleanup:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
            cleanup.assert_called_once()
            self.assertIn("--run-id=" + self.owner["runId"], cleanup.call_args.args[0])
            self.assertEqual(self.latest_cleanup()["result"]["outcome"]["exitCode"], "Success")
            self.assertEqual(json.loads((self.evidence / "test-result.json").read_text())["terminal"], terminal)

    def test_unclassified_or_unsafe_diagnostic_references_refuse_after_exact_cleanup(self):
        self.write_receipt()
        directory, baseline, _ = self.published_diagnostics()
        filename = baseline["diagnostics"][0]["reference"]
        mutations = {
            "id": {"id": "native-endpoint-credentials"},
            "name": {"reference": "native-endpoint-credentials.json"},
            "hash": {"contentIdentity": "sha256:" + "0" * 64},
            "digest-case": {"reference": filename.upper()},
            "redacted": {"redacted": "true"},
            "classification": {"classification": "secret"},
            "traversal": {"reference": "../" + filename},
            "absolute": {"reference": str(directory / filename)},
            "suffix": {"reference": filename + ".json"},
            "fields": {"path": "owner.json"},
        }
        for fault, changes in mutations.items():
            result = copy.deepcopy(baseline)
            result["diagnostics"][0].update(changes)
            with self.subTest(fault=fault):
                self.assert_declared_result_refuses_after_exact_cleanup(directory, result, baseline["terminal"])

    def test_duplicate_or_excess_diagnostic_references_refuse_after_exact_cleanup(self):
        self.write_receipt()
        directory, baseline, _ = self.published_diagnostics()
        for fault, count in (("duplicate", 2), ("count", 4)):
            result = copy.deepcopy(baseline)
            result["diagnostics"] = [result["diagnostics"][0]] * count
            with self.subTest(fault=fault):
                self.assert_declared_result_refuses_after_exact_cleanup(directory, result, baseline["terminal"])

    def test_foreign_diagnostic_owner_bindings_refuse_after_exact_cleanup(self):
        self.write_receipt()
        directory, baseline, _ = self.published_diagnostics()
        for fault, key in (("foreign-run", "runId"), ("foreign-attempt", "attemptId"), ("foreign-environment", "environmentId")):
            result = copy.deepcopy(baseline)
            with self.subTest(fault=fault):
                result["bindings"]["run"][key] = "f" * 32
                self.assert_declared_result_refuses_after_exact_cleanup(directory, result, baseline["terminal"])
        result = copy.deepcopy(baseline)
        result["bindings"]["environment"]["executionContext"] = {"kind": "local"}
        with self.subTest(fault="foreign-context"):
            self.assert_declared_result_refuses_after_exact_cleanup(directory, result, baseline["terminal"])
        for field, value in (("bindings", []), ("policyId", "foreign")):
            result = copy.deepcopy(baseline)
            result[field] = value
            with self.subTest(field=field):
                self.assert_declared_result_refuses_after_exact_cleanup(directory, result, baseline["terminal"])

    def test_missing_changed_foreign_oversized_or_nonprivate_diagnostic_bytes_refuse(self):
        self.write_receipt()
        directory, result, files = self.published_diagnostics()
        reference = result["diagnostics"][0]
        original = files[reference["reference"]]
        for fault in ("missing", "changed", "foreign", "size", "symlink", "hardlink", "permissions", "fifo"):
            path = directory / reference["reference"]
            if path.exists() or path.is_symlink():
                path.unlink()
            body = original
            if fault == "changed":
                body += b" "
            if fault == "foreign":
                body = original.replace(self.owner["runId"].encode(), b"f" * 32)
            if fault == "size":
                body = original + b" " * (1024 * 1024)
            if fault == "symlink":
                path.symlink_to(directory / result["diagnostics"][1]["reference"])
            elif fault == "hardlink":
                os.link(directory / result["diagnostics"][1]["reference"], path)
            elif fault == "fifo":
                os.mkfifo(path, 0o600)
            elif fault != "missing":
                path.write_bytes(body)
                path.chmod(0o644 if fault == "permissions" else 0o600)
            updated = copy.deepcopy(result)
            if fault in ("foreign", "size"):
                digest = hashlib.sha256(body).hexdigest()
                target = directory / ("lifecycle-" + digest + ".json")
                path.rename(target)
                updated["diagnostics"][0].update(reference=target.name, contentIdentity="sha256:" + digest)
            self.publish_result(directory, updated)
            with self.subTest(fault=fault), patch.object(helper, "verify_tool"), patch.object(
                helper, "run_down", return_value=self.down_result()
            ) as cleanup:
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
                cleanup.assert_called_once()
                self.assertEqual(self.latest_cleanup()["result"]["outcome"]["exitCode"], "Success")

    def test_native_and_functional_report_size_bounds_follow_the_actual_writers(self):
        self.write_receipt()
        directory, baseline, _ = self.published_diagnostics()
        for identity, limit in (("native-report", 4 * 1024 * 1024), ("functional-observations", 1024 * 1024)):
            for overflow in (False, True):
                result = copy.deepcopy(baseline)
                reference = next(item for item in result["diagnostics"] if item["id"] == identity)
                original = (directory / reference["reference"]).read_bytes()
                body = original + b" " * (limit - len(original) + int(overflow))
                digest = hashlib.sha256(body).hexdigest()
                prefix, suffix = ("native", ".trx") if identity == "native-report" else ("functional-observations", ".json")
                path = directory / (prefix + "-" + digest + suffix)
                path.write_bytes(body)
                path.chmod(0o600)
                reference.update(reference=path.name, contentIdentity="sha256:" + digest)
                self.publish_result(directory, result)
                with self.subTest(identity=identity, overflow=overflow), patch.object(helper, "verify_tool"), patch.object(
                    helper, "run_down", return_value=self.down_result()
                ):
                    self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), int(overflow))

    def test_diagnostic_destination_is_private_immutable_and_never_follows_links(self):
        self.write_receipt()
        _, _, files = self.published_diagnostics()
        self.evidence.mkdir(parents=True)
        filename, body = next(iter(files.items()))
        sentinel = self.root / "sentinel"
        sentinel.write_bytes(b"sentinel unchanged")
        sentinel.chmod(0o600)
        target = self.evidence / filename
        for fault in ("changed", "symlink", "hardlink", "permissions"):
            if target.exists() or target.is_symlink():
                target.unlink()
            if fault == "symlink":
                target.symlink_to(sentinel)
            elif fault == "hardlink":
                os.link(sentinel, target)
            else:
                target.write_bytes(b"changed" if fault == "changed" else body)
                target.chmod(0o644 if fault == "permissions" else 0o600)
            with self.subTest(fault=fault), patch.object(helper, "verify_tool"), patch.object(
                helper, "run_down", return_value=self.down_result()
            ):
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
                self.assertEqual(sentinel.read_bytes(), b"sentinel unchanged")
        target.unlink()
        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
        self.assertEqual(target.read_bytes(), body)
        self.assertEqual(target.stat().st_mode & 0o777, 0o600)

    def test_collection_refusal_preserves_native_failure_and_still_finishes_exact_cleanup(self):
        self.write_receipt()
        directory, result, _ = self.published_diagnostics()
        (directory / result["diagnostics"][0]["reference"]).unlink()
        with self.fake_tool("failure"), patch.object(helper, "verify_tool"):
            self.assertEqual(helper.execute_integration(helper.TOOL + ["test", "--ci-owner-receipt=" + str(self.receipt)],
                                                        self.root, self.receipt, self.evidence, 60), 17)
        self.assertEqual(len(self.downs()), 1)
        self.assertEqual(json.loads((self.evidence / "test-result.json").read_text())["terminal"], result["terminal"])
        self.assertEqual(self.cleanup_record(1)["result"]["outcome"]["exitCode"], "Success")

    def test_diagnostic_directory_symlinks_and_unrelated_metadata_never_escape_the_artifact_boundary(self):
        self.write_receipt()
        directory, _, files = self.published_diagnostics()
        for name in ("plan.json", "source-mapping.json", "native-endpoint-credentials.json", "apphost.log"):
            (directory / name).write_text("private-endpoint-sentinel")
        (directory / "owner.json").write_text(json.dumps(self.owner))
        held = directory.with_name("held-owner")

        def replace_after_owner_validation(*args, **kwargs):
            directory.rename(held)
            directory.symlink_to(held, target_is_directory=True)
            return self.down_result()

        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", side_effect=replace_after_owner_validation) as cleanup:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
            cleanup.assert_called_once()
        directory.unlink()
        held.rename(directory)
        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
        self.assertEqual(self.evidence_names(), {"test-result.json", *files})
        self.assertNotIn("private-endpoint-sentinel", b"".join(path.read_bytes() for path in self.evidence.iterdir()).decode())

    def assert_collection_failure_can_retry(self, fault):
        self.write_receipt()
        _, result, files = self.published_diagnostics()
        real_publication = helper.write_diagnostic

        def faulted_publication(*args, **kwargs):
            # The fault targets diagnostic publication only; evidence records keep their own durable writes.
            with fault:
                return real_publication(*args, **kwargs)

        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()):
            with patch.object(helper, "write_diagnostic", side_effect=faulted_publication):
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
            self.assertFalse(any((self.evidence / name).exists() for name in files), "Incomplete publication must not occupy an immutable final name.")
            self.assertEqual(self.evidence_names(), {"test-result.json"})
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
        self.assertEqual({name: (self.evidence / name).read_bytes() for name in files}, files)
        self.assertEqual(json.loads((self.evidence / "test-result.json").read_text())["terminal"], result["terminal"])
        self.assertEqual(self.latest_cleanup()["result"]["outcome"]["exitCode"], "Success")

    def test_partial_diagnostic_write_never_strands_a_final_name_and_identical_retry_succeeds(self):
        real_open = os.fdopen

        class InterruptedWrite:
            def __init__(self, stream):
                self.stream = stream

            def __enter__(self):
                return self

            def __exit__(self, *args):
                self.stream.close()

            def fileno(self):
                return self.stream.fileno()

            def write(self, body):
                self.stream.write(body[:11])
                self.stream.flush()
                raise OSError("simulated interrupted diagnostic write")

        def interrupted_open(descriptor, mode, *args, **kwargs):
            stream = real_open(descriptor, mode, *args, **kwargs)
            return InterruptedWrite(stream) if mode == "wb" else stream

        self.assert_collection_failure_can_retry(patch.object(helper.os, "fdopen", side_effect=interrupted_open))

    def test_failed_diagnostic_flush_never_publishes_a_final_name_and_identical_retry_succeeds(self):
        self.assert_collection_failure_can_retry(patch.object(helper.os, "fsync", side_effect=OSError("simulated durable flush failure")))

    def test_actual_collector_kill_after_publication_keeps_one_link_and_allows_identical_retry(self):
        body = b'{"runId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","outcome":"failed"}\n'
        filename = "lifecycle-" + hashlib.sha256(body).hexdigest() + ".json"
        # Exercise the real publication syscall; the fallback preserves this regression's old-helper red control.
        boundary_owner = helper if hasattr(helper, "promote_diagnostic") else helper.os
        boundary_name = "promote_diagnostic" if boundary_owner is helper else "link"
        real_publication = getattr(boundary_owner, boundary_name)
        child = os.fork()
        if child == 0:
            def killed_after_publication(*args, **kwargs):
                real_publication(*args, **kwargs)
                os.kill(os.getpid(), signal.SIGKILL)
            with patch.object(boundary_owner, boundary_name, side_effect=killed_after_publication):
                helper.write_diagnostic(self.evidence, filename, body, 1024 * 1024)
            os._exit(99)
        waited, status = os.waitpid(child, 0)
        self.assertEqual(waited, child)
        self.assertTrue(os.WIFSIGNALED(status))
        self.assertEqual(os.WTERMSIG(status), signal.SIGKILL)
        target = self.evidence / filename
        self.assertEqual(target.read_bytes(), body)
        self.assertEqual(target.stat().st_nlink, 1)
        helper.write_diagnostic(self.evidence, filename, body, 1024 * 1024)
        self.assertEqual(target.read_bytes(), body)
        self.assertFalse(list(self.evidence.glob(".diagnostic-*.tmp")))

    def test_failed_atomic_publication_cleans_temporary_bytes_and_identical_retry_succeeds(self):
        self.assert_collection_failure_can_retry(patch.object(helper, "promote_diagnostic", side_effect=OSError("simulated publication failure")))

    def test_concurrent_different_publication_is_never_overwritten(self):
        self.write_receipt()
        _, result, files = self.published_diagnostics()
        real_publication = helper.promote_diagnostic

        def concurrent_publication(directory, source, destination):
            descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600, dir_fd=directory)
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(b"concurrent immutable bytes")
            real_publication(directory, source, destination)

        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()), patch.object(
            helper, "promote_diagnostic", side_effect=concurrent_publication
        ):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
        self.assertEqual((self.evidence / next(iter(files))).read_bytes(), b"concurrent immutable bytes")
        self.assertFalse(list(self.evidence.glob(".diagnostic-*.tmp")))
        self.assertEqual(json.loads((self.evidence / "test-result.json").read_text())["terminal"], result["terminal"])

    def test_uncreated_temporary_name_is_never_removed(self):
        self.evidence.mkdir(parents=True)
        body = b'{"runId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}\n'
        filename = "lifecycle-" + hashlib.sha256(body).hexdigest() + ".json"
        nonce = helper.uuid.UUID(hex="0" * 32)
        sentinel = self.evidence / (".diagnostic-" + nonce.hex + ".tmp")
        sentinel.write_bytes(b"preexisting private bytes")
        sentinel.chmod(0o600)
        with patch.object(helper.uuid, "uuid4", return_value=nonce), self.assertRaises(FileExistsError):
            helper.write_diagnostic(self.evidence, filename, body, 1024 * 1024)
        self.assertEqual(sentinel.read_bytes(), b"preexisting private bytes")
        self.assertFalse((self.evidence / filename).exists())

    def test_fixed_metadata_above_one_mib_retains_the_owning_four_mib_bound(self):
        self.write_receipt()
        directory, _, _ = self.published_diagnostics()
        large = {"attachments": [], "padding": "x" * (1024 * 1024 + 17)}
        for name in ("attachments.json", "attachment-withdrawals.json"):
            path = directory / name
            path.write_text(json.dumps(large))
            path.chmod(0o600)
        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
        for name in ("attachments.json", "attachment-withdrawals.json"):
            self.assertEqual(json.loads((self.evidence / name).read_text()), large)
        path = directory / "attachments.json"
        path.write_bytes(b" " * (4 * 1024 * 1024 + 1))
        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()) as cleanup:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
            cleanup.assert_called_once()

    def down_result(self, remaining=None, exit_code=0):
        resources = {
            key: [] for key in ("containerIds", "processIds", "workspaceIds", "claimIds", "discoveryFailures")
        }
        resources["unverified"] = False
        resources.update(remaining or {})
        return subprocess.CompletedProcess(
            [],
            exit_code,
            json.dumps(
                {
                    "runId": self.owner["runId"],
                    "outcome": {"exitCode": "Success" if exit_code == 0 else "TopologyOrLifecycle"},
                    "remainingResources": resources,
                }
            ),
            "",
        )

    def test_additive_owners_use_canonical_literal_arguments_and_are_recorded(self):
        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper, "execute_integration", return_value=0
        ) as execute:
            self.assertEqual(helper.run_integration(
                self.root, "module.json", ["real"], self.receipt, self.evidence,
                owners=["tenants", "eventstore"],
            ), 0)
        command = execute.call_args.args[0]
        self.assertEqual(command[-2:], ["--with-owner=eventstore", "--with-owner=tenants"])
        self.assertIn("--mode=package", command)
        invocation = json.loads((self.evidence / "invocation.json").read_text())
        self.assertEqual(invocation["qualificationOwners"], ["eventstore", "tenants"])
        self.assertEqual(invocation["classification"], "baseline-only")

    def test_invalid_owners_refuse_before_context_paths_tool_or_evidence_effects(self):
        for owners in (["tenants", "tenants"], ["Tenants"], ["../tenants"], ["--mode=source"], ["future"], [""]):
            with self.subTest(owners=owners), patch.object(helper, "ci_context") as context, patch.object(
                helper, "checked_paths"
            ) as paths, patch.object(helper, "execute_integration") as execute:
                with self.assertRaises(ValueError):
                    helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, owners=owners)
                context.assert_not_called()
                paths.assert_not_called()
                execute.assert_not_called()
                self.assertFalse(self.evidence.exists())
                self.assertFalse(self.receipt.exists())

    def test_default_invocation_has_no_owner_request_or_arguments(self):
        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper, "execute_integration", return_value=0
        ) as execute:
            self.assertEqual(helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence), 0)
        self.assertFalse(any(value.startswith("--with-owner") for value in execute.call_args.args[0]))
        self.assertNotIn("qualificationOwners", json.loads((self.evidence / "invocation.json").read_text()))

    def test_no_receipt_means_no_allocation_and_does_not_invoke_tool(self):
        with patch.object(helper, "run_down") as run:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
            run.assert_not_called()
        self.assertEqual(self.cleanup_record(1)["status"], "not-allocated")

    def test_foreign_repository_run_attempt_or_job_cannot_cleanup(self):
        for key, value in (
            ("repository", "other/r"),
            ("workflowRunId", "99"),
            ("workflowAttempt", 3),
            ("jobId", "another"),
        ):
            with self.subTest(key=key), patch.object(helper, "run_down") as run:
                original = self.owner["executionContext"][key]
                self.owner["executionContext"][key] = value
                self.write_receipt()
                with self.assertRaises(ValueError):
                    helper.finalize(self.root, self.receipt, self.evidence)
                run.assert_not_called()
                self.owner["executionContext"][key] = original

    def test_mismatching_durable_owner_cannot_cleanup(self):
        self.write_receipt()
        (self.state / (self.owner["runId"] + ".json")).write_text(json.dumps(dict(self.owner, environmentId="d" * 32)))
        with self.assertRaises(ValueError), patch.object(helper, "run_down") as run:
            helper.finalize(self.root, self.receipt, self.evidence)
        run.assert_not_called()

    def test_cleanup_uses_exact_id_is_retryable_and_retains_only_safe_results(self):
        self.write_receipt()
        directory = self.state / "diagnostics" / self.owner["runId"]
        directory.mkdir(parents=True)
        (directory / "test-result.json").write_text('{"classification":"baseline-only"}')
        (directory / "test-result.json").chmod(0o600)
        (directory / "native-endpoint-credentials.json").write_text("private-key-sentinel")
        with patch.object(helper, "verify_tool"), patch.object(
            helper, "run_down", return_value=self.down_result()
        ) as run:
            for _ in range(2):
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
            self.assertEqual(
                run.call_args.args[0], helper.TOOL + ["down", "--run-id=" + self.owner["runId"], "--output=json"]
            )
        self.assertTrue((self.evidence / "test-result.json").exists())
        self.assertNotIn("private-key-sentinel", str([p.read_text() for p in self.evidence.iterdir()]))

    def test_cleanup_keeps_leading_zeroes_in_the_exact_canonical_owner_identity(self):
        self.owner["runId"] = "00000000000000001234567890abcdef"
        self.write_receipt()
        with patch.object(helper, "verify_tool"), patch.object(
            helper, "run_down", return_value=self.down_result()
        ) as process:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
        self.assertEqual(
            process.call_args.args[0],
            helper.TOOL
            + [
                "down",
                "--run-id=00000000000000001234567890abcdef",
                "--output=json",
            ],
        )
        record = self.cleanup_record(1)
        self.assertEqual(record["result"]["runId"], self.owner["runId"])
        self.assertTrue(record["verified"])

    def test_constructor_time_cancellation_never_releases_the_tool(self):
        real = helper.subprocess.Popen

        def constructing(*args, **kwargs):
            child = real(*args, **kwargs)
            helper.signal.raise_signal(helper.signal.SIGTERM)
            return child

        with self.fake_tool("wait"), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen", side_effect=constructing
        ):
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60), 130
            )
        self.assertFalse((self.root / "actual-tool-args.json").exists(), "The gated tool must never start.")
        self.assertEqual(self.downs(), [], "Exact cleanup after cancellation belongs to the job finalizer.")
        execution = json.loads((self.evidence / "execution.json").read_text())
        self.assertEqual((execution["launched"], execution["cancelled"], execution["timedOut"]), (False, True, False))
        self.assertTrue(helper.process_record_path(self.receipt).exists())
        self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence, job_status="cancelled"), 0)
        self.assertEqual(self.cleanup_record(1)["status"], "not-allocated")

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_runner_signal_to_the_step_shell_reaches_the_tool_session_and_the_finalizer_cleans_exactly(self):
        step = self.start_step("wait")
        try:
            self.wait_for(self.root / "actual-tool-ready")
            # The hosted runner signals only the step process.
            step.send_signal(signal.SIGINT)
            output, errors = step.communicate(timeout=10)
        finally:
            self.stop_step(step)
        self.assertEqual(step.returncode, 130, output + errors)
        self.assertEqual((self.root / "actual-tool-signal").read_text(), str(signal.SIGINT))
        self.assertEqual(self.downs(), [], "The helper leaves exact cleanup after cancellation to the job finalizer.")
        execution = json.loads((self.evidence / "execution.json").read_text())
        self.assertTrue(execution["cancelled"])
        self.assertEqual(execution["result"]["outcome"]["exitCode"], "Cancelled")
        finalizer = self.run_finalizer_step("cancelled")
        self.assertEqual(finalizer.returncode, 0, finalizer.stdout + finalizer.stderr)
        self.assertEqual([down["jobStatus"] for down in self.downs()], ["cancelled"])
        record = self.cleanup_record(1)
        self.assertEqual((record["status"], record["verified"]), ("completed", True))

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_signal_before_the_receipt_never_reports_not_allocated_while_a_tool_survives(self):
        step = self.start_step("stubborn-late-receipt")
        try:
            tool = int(self.wait_for(self.root / "actual-tool-ready").read_text())
            self.assertFalse(self.receipt.exists())
            # The runner's final process-tree kill can miss a reparented tool; model the orphan directly.
            step.kill()
            step.communicate(timeout=10)
            self.assertIsNotNone(helper.process_identity(tool), "The tool must survive the step as an orphan.")
            with self.fake_tool("stubborn-late-receipt"), patch.object(helper, "SESSION_SIGNAL_WAIT_SECONDS", 0.5), patch.object(
                helper, "verify_tool"
            ):
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence, job_status="cancelled"), 0)
        finally:
            self.stop_step(step)
        self.assertIsNone(helper.process_identity(tool))
        self.assertTrue(self.receipt.exists(), "The orphan allocated after the step ended.")
        record = self.cleanup_record(1)
        self.assertNotEqual(record["status"], "not-allocated")
        self.assertTrue(record["verified"])
        self.assertEqual([down["jobStatus"] for down in self.downs()], ["cancelled"])

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_a_live_orphan_cannot_hold_the_provisioning_lock_against_exact_cleanup(self):
        lock = self.root / "provisioning.lock"
        with self.fake_tool("lock-orphan", FAKE_LOCK=str(lock)), patch.object(helper, "verify_tool"), patch.object(
            helper, "verify_preparation"
        ), patch.object(helper, "SESSION_SIGNAL_WAIT_SECONDS", 0.5):
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60), 0
            )
        orphan = int((self.root / "actual-orphan-ready").read_text())
        self.assertIsNone(helper.process_identity(orphan))
        self.assertEqual([down.get("locked") for down in self.downs()], [None])
        self.assertTrue(self.cleanup_record(1)["verified"])

    def test_timeout_is_not_a_cancellation_and_is_finalized_by_the_helper(self):
        with self.fake_tool("timeout"), patch.object(helper, "verify_tool"):
            self.assertEqual(helper.execute_integration(helper.TOOL + ["test", "--ci-owner-receipt=" + str(self.receipt)],
                                                        self.root, self.receipt, self.evidence, 1), 124)
        self.assertEqual((self.root / "actual-tool-signal").read_text(), str(signal.SIGTERM))
        execution = json.loads((self.evidence / "execution.json").read_text())
        self.assertEqual((execution["timedOut"], execution["cancelled"]), (True, False))
        self.assertEqual([down["jobStatus"] for down in self.downs()], [""])
        self.assertTrue(self.cleanup_record(1)["verified"])

    def test_unterminated_session_refuses_every_allocation_decision(self):
        survivor = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"], start_new_session=True)  # nosec B603
        try:
            self.wait_until(lambda: helper.process_identity(survivor.pid) is not None)
            helper.write_process_record(self.receipt, helper.ci_context(), survivor.pid)
            with patch.object(helper, "terminate_processes", return_value=False), patch.object(helper, "run_down") as run:
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence, job_status="cancelled"), 1)
                run.assert_not_called()
            self.assertEqual(self.cleanup_record(1)["status"], "allocation-unverified")
            context = dict(helper.ci_context(), jobId="another")
            record = json.loads(helper.process_record_path(self.receipt).read_text())
            helper.process_record_path(self.receipt).write_text(json.dumps(dict(record, executionContext=context)))
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
            self.assertEqual(self.cleanup_record(2)["reason"], "integration-process-record-unverified")
        finally:
            survivor.kill()
            survivor.wait(timeout=10)

    def test_actual_job_cancellation_reaches_exact_down_without_rewriting_first_result(self):
        self.write_receipt()
        with patch.object(helper, "verify_tool"), patch.object(
            helper, "run_down", return_value=self.down_result()
        ) as run:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence, job_status="cancelled"), 0)
            self.assertEqual(run.call_args.kwargs["env"]["HEXALITH_CI_JOB_STATUS"], "cancelled")
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence, job_status="cancelled"), 0)
        first, second = self.cleanup_record(1), self.cleanup_record(2)
        self.assertEqual((first["jobStatus"], second["jobStatus"]), ("cancelled", "cancelled"))
        self.assertTrue(first["verified"] and second["verified"])

    def test_finalize_cli_bridges_cancelled_job_status_and_refuses_invocation_options(self):
        self._real_helper_environment()
        self.write_receipt()
        with self.fake_tool("success"):
            base = [sys.executable, str(Path(helper.__file__).resolve()), "finalize", "--receipt", str(self.receipt),
                    "--evidence", str(self.evidence)]
            for extra in (["--owners", "tenants"], ["--timeout-seconds", "60"], ["--step-timeout-minutes", "40"],
                          ["--job-timeout-minutes", "45"], ["--job-started-epoch", str(int(time.time()))]):
                with self.subTest(extra=extra):
                    refused = subprocess.run(base + extra, cwd=self.root, env=os.environ.copy(), capture_output=True,  # nosec B603
                                             text=True, timeout=60, check=False)
                    self.assertEqual(refused.returncode, 1)
                    self.assertEqual(self.downs(), [])
            completed = subprocess.run(base + ["--job-status", "cancelled"], cwd=self.root, env=os.environ.copy(),  # nosec B603
                                       capture_output=True, text=True, timeout=60, check=False)
        self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
        self.assertEqual([down["jobStatus"] for down in self.downs()], ["cancelled"])

    def test_unknown_discovery_or_any_owned_leftover_refuses_completion(self):
        self.write_receipt()
        for remaining in (
            {"unverified": True},
            {"processIds": [99]},
            {"containerIds": ["owned"]},
            {"workspaceIds": ["workspace:owned"]},
            {"claimIds": ["claim:owned"]},
            {"discoveryFailures": ["docker"]},
        ):
            with self.subTest(remaining=remaining), patch.object(helper, "verify_tool"), patch.object(
                helper, "run_down", return_value=self.down_result(remaining)
            ):
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)

    def test_missing_or_wrong_mode_suite_refuses_before_process(self):
        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen"
        ) as process:
            for profiles in ([], ["unknown"], ["real", "real"]):
                with self.assertRaises(ValueError):
                    helper.run_integration(self.root, "module.json", profiles, self.receipt, self.evidence)
            process.assert_not_called()

    def test_execution_failure_preserved_and_finalized_after_partial_startup(self):
        with self.fake_tool("failure"), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"):
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60), 17
            )
        self.assertEqual([down["jobStatus"] for down in self.downs()], [""])
        execution = json.loads((self.evidence / "execution.json").read_text())
        self.assertEqual((execution["processExitCode"], execution["result"]["outcome"]["exitCode"]), (17, "ProductOrTest"))

    def test_zero_exit_counts_only_with_a_receipt_and_a_success_outcome(self):
        for scenario, expected in (("success", 0), ("success-without-receipt", 1), ("zero-not-success", 1)):
            with self.subTest(scenario=scenario):
                self.reset_job_records()
                with self.fake_tool(scenario), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"):
                    self.assertEqual(
                        helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60),
                        expected,
                    )

    def test_invalid_child_output_is_recorded_unverified_and_still_finalizes(self):
        with self.fake_tool("invalid-output"), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"):
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60), 1
            )
        execution = json.loads((self.evidence / "execution.json").read_text())
        self.assertEqual((execution["processExitCode"], execution["outputParsed"], execution["result"]), (1, False, None))
        excerpt = execution["stderr"]["excerpt"]
        self.assertIn("password <redacted>", excerpt)
        self.assertNotIn("hunter2", excerpt)
        self.assertNotIn("Q" * 48, excerpt)
        self.assertEqual(len(self.downs()), 1)

    def test_cancellation_during_execution_defers_exact_cleanup_to_the_job_finalizer(self):
        def cancel_when_ready():
            self.wait_for(self.root / "actual-tool-ready")
            os.kill(os.getpid(), signal.SIGTERM)

        canceller = threading.Thread(target=cancel_when_ready, daemon=True)
        with self.fake_tool("wait"), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"):
            canceller.start()
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60), 130
            )
            canceller.join(timeout=10)
        # The first runner signal reaches the whole tool session as an interrupt.
        self.assertEqual((self.root / "actual-tool-signal").read_text(), str(signal.SIGINT))
        self.assertEqual(self.downs(), [])
        self.assertEqual(json.loads((self.evidence / "execution.json").read_text())["result"]["outcome"]["exitCode"], "Cancelled")

    def test_missing_package_preparation_refuses_before_process(self):
        with patch.object(helper, "verify_tool"), patch.object(helper.subprocess, "Popen") as process, patch.dict(
            os.environ, {"Configuration": ""}
        ):
            with self.assertRaises(ValueError):
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence)
            process.assert_not_called()

    def test_evidence_refuses_parent_traversal_and_foreign_roots_before_creating_directories(self):
        for destination in (self.root / "foreign", self.evidence / ".." / "escape"):
            with self.subTest(destination=destination), self.assertRaises(ValueError):
                helper.write_evidence(destination, "execution.json", {})
            with self.subTest(cleanup=destination), self.assertRaises(ValueError):
                helper.write_cleanup_record(destination, {})
        self.assertFalse((self.root / "foreign").exists())
        self.assertFalse((self.root / "TestResults").exists())

    def test_evidence_rejects_a_symlinked_ancestor_and_an_unlisted_filename(self):
        outside = self.root / "outside"
        outside.mkdir()
        (self.root / "TestResults").symlink_to(outside, target_is_directory=True)
        with self.assertRaises(ValueError):
            helper.write_evidence(self.evidence, "execution.json", {})
        with self.assertRaises(ValueError):
            helper.write_evidence(self.evidence, "../sentinel.json", {})
        with self.assertRaises(ValueError):
            helper.write_evidence(self.evidence, "cleanup.json", {})
        self.assertEqual(list(outside.iterdir()), [])

    def test_evidence_refuses_file_links_without_truncating_the_external_file(self):
        self.evidence.mkdir(parents=True)
        sentinel = self.root / "sentinel.json"
        sentinel.write_text("original")
        target = self.evidence / "execution.json"
        for hard_link in (False, True):
            with self.subTest(hard_link=hard_link):
                if hard_link:
                    os.link(sentinel, target)
                else:
                    target.symlink_to(sentinel)
                with self.assertRaises((OSError, ValueError)):
                    helper.write_evidence(self.evidence, "execution.json", {})
                self.assertEqual(sentinel.read_text(), "original")
                target.unlink()

    def test_evidence_publication_is_atomic_and_each_down_attempt_keeps_its_record(self):
        helper.write_evidence(self.evidence, "execution.json", {"first": True})
        with patch.object(helper.os, "replace", side_effect=OSError(errno.EIO, "interrupted")):
            with self.assertRaises(OSError):
                helper.write_evidence(self.evidence, "execution.json", {"second": True})
        self.assertEqual(json.loads((self.evidence / "execution.json").read_text()), {"first": True})
        self.assertEqual([path.name for path in self.evidence.iterdir() if path.name.startswith(".evidence-")], [])
        self.assertEqual(helper.write_cleanup_record(self.evidence, {"attempt": 1}), "cleanup-1.json")
        self.assertEqual(helper.write_cleanup_record(self.evidence, {"attempt": 2}), "cleanup-2.json")
        self.assertEqual(self.cleanup_record(1), {"attempt": 1})
        with self.assertRaises(ValueError):
            helper.write_cleanup_record(self.evidence, {}, "cleanup.json")

    def test_receipt_must_stay_under_actual_runner_temp_without_link_ancestors(self):
        foreign = self.root / "foreign"
        foreign.mkdir()
        link = self.runner_temp / "link"
        link.symlink_to(foreign, target_is_directory=True)
        for receipt in (foreign / "owner.json", link / "owner.json", self.runner_temp / ".." / "owner.json"):
            with self.subTest(receipt=receipt), patch.object(helper, "run_down") as tool:
                with self.assertRaises(ValueError):
                    helper.finalize(self.root, receipt, self.evidence)
                tool.assert_not_called()
        self.assertFalse((foreign / "owner.json").exists())

    def test_declared_dash_control_or_unicode_profile_cannot_turn_into_a_tool_option(self):
        for profile in ("--mode=source", "-x", "real\n--output=xml", "réal", "r١", "r" * 64):
            document = {"modules": [{"tests": [{"name": profile, "mode": "package"}]}]}
            (self.root / "module.json").write_text(json.dumps(document))
            with self.subTest(profile=profile), patch.object(helper.subprocess, "Popen") as child:
                with self.assertRaises(ValueError):
                    helper.run_integration(self.root, "module.json", [profile], self.receipt, self.evidence)
                child.assert_not_called()

    def test_fixed_job_receipt_rejects_a_different_name_and_a_canonical_symlink(self):
        alternate = self.runner_temp / "alternate.json"
        original = json.dumps(self.owner)
        alternate.write_text(original)
        with patch.object(helper, "run_down") as process:
            with self.assertRaises(ValueError):
                helper.finalize(self.root, alternate, self.evidence)
            self.receipt.symlink_to(alternate)
            with self.assertRaises(ValueError):
                helper.finalize(self.root, self.receipt, self.evidence)
            process.assert_not_called()
        self.assertEqual(alternate.read_text(), original)
        self.assertFalse(self.evidence.exists())

    def test_digit_shorthand_keeps_owner_attempt_and_version_identities_ascii_only(self):
        self.owner["runId"] = "a" * 31 + "١"
        self.write_receipt()
        context = helper.ci_context()
        with self.assertRaises(ValueError):
            helper.receipt_owner(self.receipt, context)
        with patch.dict(os.environ, {"GITHUB_RUN_ATTEMPT": "1٢"}):
            with self.assertRaises(ValueError):
                helper.ci_context()
        configuration = self.root / ".config"
        configuration.mkdir()
        tool = {"version": "9.9.٩", "rollForward": False, "commands": ["hexalith-module"]}
        (configuration / "dotnet-tools.json").write_text(
            json.dumps({"isRoot": True, "tools": {"hexalith.builds.module.cli": tool}})
        )
        with patch.object(helper.subprocess, "run") as process:
            with self.assertRaises(ValueError):
                helper.verify_tool(self.root)
            process.assert_not_called()

    def test_manifest_looking_like_an_option_is_bound_to_one_literal_manifest_value(self):
        manifest = "--mode=source.json"
        (self.root / manifest).write_text((self.root / "module.json").read_text())
        with self.fake_tool("success"), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"):
            self.assertEqual(helper.run_integration(self.root, manifest, ["real"], self.receipt, self.evidence, timeout=60), 0)
        self.assertEqual(
            json.loads((self.root / "actual-tool-args.json").read_text()),
            helper.TOOL[1:]
            + [
                "test",
                "--manifest=" + str(self.root / manifest),
                "--mode=package",
                "--ci-owner-receipt=" + str(self.receipt),
                "--output=json",
                "--profile=real",
            ],
        )
        # The tool runs in its own recorded session with an empty standard input.
        self.assertEqual(json.loads((self.root / "actual-tool-stdin.json").read_text()), "")
        record = json.loads(helper.process_record_path(self.receipt).read_text())
        self.assertEqual(record["pid"], record["session"])
        self.assertEqual(record["executionContext"], helper.ci_context())

    def test_preparation_binds_release_and_actual_package_mapping(self):
        workspace = self.root / "prepared"
        imports = workspace / "source-mapping"
        imports.mkdir(parents=True)
        digest = "d" * 64
        environment = {key: str(imports / filename) for key, filename in helper._prepare.IMPORTS.items()}
        environment.update(Configuration="Release", HEXALITH_SOURCE_MAPPING_HASH=digest)
        for path in environment.values():
            if path.startswith(str(imports)):
                Path(path).write_text("<Project />")
        metadata = imports / "mapping.json"
        metadata.write_text(json.dumps({"Mode": "package", "HasGit": True, "ContentHash": digest}))
        with patch.dict(os.environ, environment):
            helper.verify_preparation()
            metadata.write_text(json.dumps({"Mode": "source", "HasGit": True, "ContentHash": digest}))
            with self.assertRaises(ValueError):
                helper.verify_preparation()

    def test_passing_execution_requires_verified_cleanup(self):
        with self.fake_tool("success"), patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper, "finalize", return_value=1
        ):
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence, timeout=60), 1
            )

    def test_cleanup_verification_requires_exit_owner_and_success(self):
        result = json.loads(self.down_result().stdout)
        self.assertTrue(helper.cleanup_verified(self.down_result(), result, self.owner))
        for completed, changed in (
            (self.down_result(exit_code=3), result),
            (self.down_result(), dict(result, runId="f" * 32)),
            (self.down_result(), dict(result, outcome={"exitCode": "TopologyOrLifecycle"})),
            (self.down_result(), dict(result, outcome="Success")),
            (self.down_result(), dict(result, remainingResources=[])),
            (self.down_result(), ["not", "an", "object"]),
        ):
            with self.subTest(changed=changed):
                self.assertFalse(helper.cleanup_verified(completed, changed, self.owner))

    def test_receipt_with_a_foreign_state_directory_refuses(self):
        foreign = self.root / "foreign-state"
        foreign.mkdir()
        self.owner["stateDirectory"] = str(foreign)
        self.write_receipt()
        with self.assertRaises(ValueError):
            helper.receipt_owner(self.receipt, helper.ci_context())
        with patch.object(helper, "run_down") as run, self.assertRaises(ValueError):
            helper.finalize(self.root, self.receipt, self.evidence)
        run.assert_not_called()

    def test_down_timeout_malformed_or_non_json_output_is_unverified_and_still_retains(self):
        self.write_receipt()
        self.published_diagnostics()
        expired = subprocess.TimeoutExpired(["down"], 150, output="", stderr="token=abcdef stalled")
        cases = (
            ("timed-out", expired),
            ("non-json", subprocess.CompletedProcess([], 0, "not json", "")),
            ("malformed", subprocess.CompletedProcess([], 0, json.dumps({"runId": self.owner["runId"], "outcome": "Success",
                                                                         "remainingResources": []}), "")),
        )
        for number, (name, outcome) in enumerate(cases, start=1):
            with self.subTest(name=name), patch.object(helper, "verify_tool"), patch.object(
                helper, "run_down", side_effect=[outcome] if isinstance(outcome, Exception) else None,
                return_value=None if isinstance(outcome, Exception) else outcome,
            ):
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
                record = self.cleanup_record(number)
                self.assertFalse(record["verified"])
                self.assertTrue((self.evidence / "test-result.json").exists())
                if name == "timed-out":
                    self.assertEqual(record["status"], "timed-out")
                    self.assertNotIn("abcdef", record["stderr"]["excerpt"])
                else:
                    self.assertEqual(record["outputParsed"], name == "malformed")

    def test_malformed_published_result_is_a_classified_retention_refusal(self):
        self.write_receipt()
        directory = self.state / "diagnostics" / self.owner["runId"]
        directory.mkdir(parents=True)
        (directory / "test-result.json").write_text("[1, 2]")
        (directory / "test-result.json").chmod(0o600)
        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", return_value=self.down_result()):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
        self.assertTrue(self.cleanup_record(1)["verified"])

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_runner_cancellation_reaches_only_the_tool_so_withdrawal_precedes_every_composed_stop(self):
        step = self.start_step("muxer-apphost")
        try:
            self.wait_for(self.root / "actual-tool-ready")
            # The hosted runner signals only the step process; the composed AppHost shares the tool's group.
            step.send_signal(signal.SIGINT)
            output, errors = step.communicate(timeout=20)
        finally:
            self.stop_step(step)
        self.assertEqual(step.returncode, 130, output + errors)
        self.assertEqual((self.root / "actual-tool-signal").read_text(), str(signal.SIGINT))
        withdrawn = int((self.root / "actual-withdrawn").read_text())
        number, observed = (self.root / "actual-apphost-signal").read_text().split()
        self.assertEqual(int(number), signal.SIGTERM, "Only the tool's own stop may reach the AppHost.")
        self.assertGreaterEqual(int(observed), withdrawn, "No signal may reach a composed process before withdrawal.")
        finalizer = self.run_finalizer_step("cancelled")
        self.assertEqual(finalizer.returncode, 0, finalizer.stdout + finalizer.stderr)
        self.assertEqual((self.latest_cleanup()["status"], self.latest_cleanup()["verified"]), ("completed", True))

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_finalizer_leaves_an_owned_surviving_apphost_to_the_exact_down(self):
        step = self.start_step("owned-survivor")
        try:
            output, errors = step.communicate(timeout=30)
        finally:
            self.stop_step(step)
        self.assertEqual(step.returncode, 1, output + errors)
        [down] = self.downs()
        self.assertTrue(down["appHostAlive"], "The exact down must find the owned AppHost still alive.")
        number, observed = (self.root / "actual-apphost-signal").read_text().split()
        self.assertEqual(int(number), signal.SIGTERM)
        self.assertGreaterEqual(int(observed), down["startedAtNs"], "Nothing may signal the owned AppHost before the down.")
        self.assertFalse((self.root / "actual-apphost-alive").exists())

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_cancellation_escalates_on_a_tool_that_ignores_interrupt_and_terminate(self):
        for runner_signals in (1, 2):
            with self.subTest(runner_signals=runner_signals), self.fake_tool("ignore-all"), patch.object(
                helper, "CANCELLATION_ESCALATION_SECONDS", 0.6
            ), patch.object(helper, "CANCELLATION_FINAL_SECONDS", 1.2), tempfile.TemporaryFile() as output, \
                    tempfile.TemporaryFile() as errors:
                self.reset_job_records()
                session = helper.ToolSession()
                session.launch(helper.TOOL + ["test", "--ci-owner-receipt=" + str(self.receipt)], self.root, self.receipt,
                               helper.ci_context(), output, errors)
                self.wait_for(self.root / "actual-tool-ready")
                started = time.monotonic()
                for _ in range(runner_signals):
                    session.handle(signal.SIGINT, None)
                self.assertFalse(session.wait(30))
                self.assertEqual(session.child.returncode, -signal.SIGKILL)
                logged = [line.split() for line in (self.root / "actual-tool-signals").read_text().splitlines()]
                self.assertEqual([int(number) for number, _ in logged], [signal.SIGINT, signal.SIGTERM])
                terminated = float(logged[1][1]) - started
                if runner_signals == 2:
                    self.assertLess(terminated, 0.6, "A second runner signal escalates to SIGTERM at once.")
                else:
                    self.assertGreaterEqual(terminated, 0.6, "SIGTERM waits for the in-grace escalation time.")

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_a_reused_or_other_boot_session_is_never_signalled(self):
        survivor = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"], start_new_session=True)  # nosec B603
        try:
            self.wait_until(lambda: helper.process_identity(survivor.pid) is not None)
            exact = {"session": survivor.pid, "startTime": helper.process_identity(survivor.pid)["startTime"],
                     "bootId": helper.boot_identity()}
            self.assertIn(survivor.pid, helper.tool_processes(exact))
            for name, record in (("start-time", dict(exact, startTime=str(int(exact["startTime"]) + 1))),
                                 ("boot", dict(exact, bootId="another-boot"))):
                with self.subTest(name=name):
                    self.assertEqual(helper.session_processes(record), {})
                    self.assertEqual(helper.tool_processes(record), {})
                    self.assertEqual(helper.unowned_survivors(record, self.owner["runId"]), {})
                    self.assertTrue(helper.terminate_processes(lambda: helper.session_processes(record), survivor.pid,
                                                               wait=0.2))
                    if name == "start-time":
                        helper.signal_processes({survivor.pid: record["startTime"]}, survivor.pid, signal.SIGKILL)
                    time.sleep(0.1)
                    self.assertIsNone(survivor.poll(), "An unrelated session must never be signalled.")
        finally:
            survivor.kill()
            survivor.wait(timeout=10)

    def test_a_failed_cleanup_record_never_prevents_the_exact_down(self):
        self.write_receipt()
        original = helper.write_cleanup_record
        for failure in (OSError(errno.ENOSPC, "no space"), ValueError("evidence path refused")):
            calls = []

            def first_write_fails(*args, **kwargs):
                calls.append(args)
                if len(calls) == 1:
                    raise failure
                return original(*args, **kwargs)

            with self.subTest(failure=type(failure).__name__), patch.object(helper, "verify_tool"), patch.object(
                helper, "write_cleanup_record", side_effect=first_write_fails
            ), patch.object(helper, "run_down", return_value=self.down_result()) as down:
                self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1,
                                 "Unrecorded cleanup stays non-success.")
                down.assert_called_once()

    def test_stderr_is_redacted_before_the_excerpt_and_across_joined_keywords(self):
        straddling = "plain " * 1000 + "\ntoken=" + "v1 " * 3000
        record = helper.stderr_record(straddling + "\nDB_PASSWORD=hunter2\nAPI-TOKEN: abc\nplain line\n")
        self.assertNotIn("v1", record["excerpt"])
        self.assertNotIn("hunter2", record["excerpt"])
        self.assertNotIn("abc", record["excerpt"])
        self.assertIn("plain line", record["excerpt"])
        self.assertTrue(record["truncated"])

    def test_non_utf8_down_output_and_a_missing_tool_are_unverified_and_still_retain(self):
        self.write_receipt()
        self.published_diagnostics()
        with self.fake_tool("binary-down"), patch.object(helper, "verify_tool"):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
        record = self.cleanup_record(1)
        self.assertEqual((record["status"], record["outputParsed"], record["verified"]), ("completed", False, False))
        self.assertTrue((self.evidence / "test-result.json").exists())
        with patch.object(helper, "verify_tool"), patch.object(helper, "run_down", side_effect=FileNotFoundError("dotnet")):
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 1)
        record = self.cleanup_record(2)
        self.assertEqual((record["status"], record["reason"], record["verified"]), ("tool-unavailable", "FileNotFoundError", False))

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_a_timed_out_down_leaves_no_orphan_in_its_session(self):
        marker = self.root / "actual-down-grandchild"
        code = ("import subprocess, sys, time\n"
                "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])\n"
                "open(sys.argv[1], 'w').write(str(child.pid))\n"
                "time.sleep(60)\n")
        with self.assertRaises(subprocess.TimeoutExpired):
            helper.run_down([sys.executable, "-c", code, str(marker)], cwd=self.root, env=os.environ.copy(), timeout=2)
        grandchild = int(marker.read_text())
        self.wait_until(lambda: helper.process_identity(grandchild) is None)

    def test_timeout_derives_from_the_step_and_the_job_remaining_time(self):
        now = 2_000_000_000.0
        reserve = helper.STEP_RESERVE_SECONDS
        self.assertEqual(helper.derived_timeout(40, 360, int(now) - 10, now=now), 40 * 60 - reserve)
        started = int(now) - 8 * 60
        expected = int(started - helper.JOB_SETUP_ALLOWANCE_SECONDS + 45 * 60 - now - reserve - helper.FINALIZER_STEP_SECONDS)
        self.assertEqual(helper.derived_timeout(40, 45, started, now=now), expected)
        # The timeout path and the always() finalizer both still end before the job deadline.
        job_deadline = started - helper.JOB_SETUP_ALLOWANCE_SECONDS + 45 * 60
        self.assertLessEqual(now + expected + reserve + helper.FINALIZER_STEP_SECONDS, job_deadline)
        for started, minutes in ((int(now) - 40 * 60, 45), (int(now) + 60, 45), (int(now) - 46 * 60, 45)):
            with self.subTest(started=started), self.assertRaises(ValueError):
                helper.derived_timeout(40, minutes, started, now=now)
        for invalid in (0, -1, True, 1.5):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                helper.derived_timeout(invalid, 45, int(now), now=now)

    def test_an_exhausted_job_budget_refuses_before_any_allocation(self):
        self._real_helper_environment()
        with self.fake_tool("success"):
            base = [sys.executable, str(Path(helper.__file__).resolve()), "run", "--manifest", "module.json", "--profiles",
                    "real", "--receipt", str(self.receipt), "--evidence", str(self.evidence)]
            exhausted = ["--step-timeout-minutes", "40", "--job-timeout-minutes", "45", "--job-started-epoch",
                         str(int(time.time()) - 41 * 60)]
            for extra in (exhausted, exhausted + ["--timeout-seconds", "600"], exhausted[:4]):
                with self.subTest(extra=extra):
                    refused = subprocess.run(base + extra, cwd=self.root, env=os.environ.copy(), capture_output=True,  # nosec B603
                                             text=True, timeout=60, check=False)
                    self.assertEqual(refused.returncode, 1, refused.stdout + refused.stderr)
                    self.assertFalse((self.root / "actual-tool-args.json").exists())
                    self.assertFalse(self.receipt.exists() or helper.process_record_path(self.receipt).exists())
                    self.assertFalse((self.evidence / "invocation.json").exists())

    def test_the_v2_step_passes_its_derived_budget_into_the_invocation(self):
        step = self.start_step("success")
        output, errors = step.communicate(timeout=60)
        self.assertEqual(step.returncode, 0, output + errors)
        invocation = json.loads((self.evidence / "invocation.json").read_text())
        self.assertIsInstance(invocation["timeoutSeconds"], int)
        self.assertLessEqual(invocation["timeoutSeconds"], 40 * 60 - helper.STEP_RESERVE_SECONDS)
        self.assertLessEqual(invocation["timeoutSeconds"],
                             45 * 60 - 300 - helper.JOB_SETUP_ALLOWANCE_SECONDS - helper.STEP_RESERVE_SECONDS
                             - helper.FINALIZER_STEP_SECONDS)


if __name__ == "__main__":
    unittest.main()
