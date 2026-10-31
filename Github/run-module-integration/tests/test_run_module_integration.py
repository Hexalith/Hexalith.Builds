"""Executable controls for exact ownership, blocking integration and finalization."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess  # nosec B404 -- only a fixed interpreter and this test's temporary fixture are executed.
import signal
import sys
import time
import tempfile
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

    def test_no_receipt_means_no_allocation_and_does_not_invoke_tool(self):
        with patch.object(helper.subprocess, "run") as run:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence), 0)
            run.assert_not_called()
        self.assertEqual(json.loads((self.evidence / "cleanup.json").read_text())["status"], "not-allocated")

    def test_foreign_repository_run_attempt_or_job_cannot_cleanup(self):
        for key, value in (
            ("repository", "other/r"),
            ("workflowRunId", "99"),
            ("workflowAttempt", 3),
            ("jobId", "another"),
        ):
            with self.subTest(key=key), patch.object(helper.subprocess, "run") as run:
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
        with self.assertRaises(ValueError), patch.object(helper.subprocess, "run") as run:
            helper.finalize(self.root, self.receipt, self.evidence)
        run.assert_not_called()

    def test_cleanup_uses_exact_id_is_retryable_and_retains_only_safe_results(self):
        self.write_receipt()
        directory = self.state / "diagnostics" / self.owner["runId"]
        directory.mkdir(parents=True)
        (directory / "test-result.json").write_text('{"classification":"baseline-only"}')
        (directory / "native-endpoint-credentials.json").write_text("private-key-sentinel")
        with patch.object(helper, "verify_tool"), patch.object(
            helper.subprocess, "run", return_value=self.down_result()
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
            helper.subprocess, "run", return_value=self.down_result()
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
        result = json.loads((self.evidence / "cleanup.json").read_text())
        self.assertEqual(result["runId"], self.owner["runId"])

    def test_constructor_time_cancellation_is_forwarded_after_child_assignment(self):
        forwarded = []

        class Child:
            returncode = 130

            def poll(child):
                return None

            def send_signal(child, signum):
                forwarded.append(signum)

            def communicate(child, timeout):
                self.write_receipt()
                return '{"outcome":{"exitCode":"Cancelled"}}', None

        def constructing(*args, **kwargs):
            helper.signal.raise_signal(helper.signal.SIGTERM)
            return Child()

        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen", side_effect=constructing
        ), patch.object(helper, "finalize", return_value=0) as cleanup:
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence), 130
            )
            self.assertEqual(forwarded, [helper.signal.SIGINT])
            self.assertEqual(cleanup.call_args.kwargs["job_status"], "cancelled")

    @unittest.skipUnless(os.name == "posix", "The CI helper targets disposable Linux runners.")
    def test_real_helper_process_forwards_cancellation_and_finalizes_exact_receipt(self):
        process = self._start_real_helper()
        child_pid = None
        try:
            child_pid = self._wait_real_child(process)
            process.send_signal(signal.SIGTERM)
            output, errors = process.communicate(timeout=10)
            self._assert_real_cancellation(process, output, errors)
        finally:
            self._stop_real_children(process, child_pid)

    def _real_helper_environment(self):
        configuration = self.root / ".config"
        configuration.mkdir()
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
        imports.mkdir(parents=True)
        environment = os.environ.copy()
        for key, filename in helper._prepare.IMPORTS.items():
            path = imports / filename
            path.write_text("<Project />")
            environment[key] = str(path)
        digest = "d" * 64
        (imports / "mapping.json").write_text(json.dumps({"Mode": "package", "HasGit": True, "ContentHash": digest}))
        environment.update(
            Configuration="Release", HEXALITH_SOURCE_MAPPING_HASH=digest, FAKE_CI_OWNER=json.dumps(self.owner)
        )
        return environment

    def _fake_dotnet_directory(self):
        tools = self.root / "tools"
        tools.mkdir()
        executable = tools / "dotnet"
        executable.write_text(
            "#!"
            + sys.executable
            + "\n"
            + r"""
import json, os, pathlib, signal, sys, time
root = pathlib.Path.cwd()
args = sys.argv[1:]
if '--version' in args:
    print('9.9.9')
    sys.exit(0)
owner = json.loads(os.environ['FAKE_CI_OWNER'])
if 'down' in args:
    assert '--run-id=' + owner['runId'] in args
    (root / 'actual-finalizer.json').write_text(json.dumps({'runId': owner['runId'], 'jobStatus': os.environ['HEXALITH_CI_JOB_STATUS']}))
    resources = {key: [] for key in ('containerIds', 'processIds', 'workspaceIds', 'claimIds', 'discoveryFailures')}
    resources['unverified'] = False
    print(json.dumps({'runId': owner['runId'], 'outcome': {'exitCode': 'Success'}, 'remainingResources': resources}))
    sys.exit(0)
assert 'test' in args
def interrupt(signum, frame):
    (root / 'actual-child-interrupted').write_text(str(signum))
    print(json.dumps({'outcome': {'exitCode': 'Cancelled'}}), flush=True)
    sys.exit(130)
signal.signal(signal.SIGINT, interrupt)
pathlib.Path(next(arg.split('=', 1)[1] for arg in args if arg.startswith('--ci-owner-receipt='))).write_text(json.dumps(owner))
(root / 'actual-child-ready').write_text(str(os.getpid()))
while True:
    time.sleep(0.01)
"""
        )
        executable.chmod(0o700)
        return tools

    def _start_real_helper(self):
        environment = self._real_helper_environment()
        tools = self._fake_dotnet_directory()
        environment["PATH"] = str(tools) + os.pathsep + environment["PATH"]
        command = [
            sys.executable,
            str(Path(helper.__file__).resolve()),
            "run",
            "--manifest",
            "module.json",
            "--profiles",
            "real",
            "--receipt",
            str(self.receipt),
            "--evidence",
            str(self.evidence),
        ]
        return subprocess.Popen(
            command,  # nosec B603 # nosemgrep -- fixed interpreter/helper; all arguments belong to this temporary fixture.
            cwd=self.root,
            env=environment,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

    def _wait_real_child(self, process):
        deadline = time.monotonic() + 10
        ready = self.root / "actual-child-ready"
        while not ready.exists() and process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertTrue(ready.exists(), "The real tool child must reach its interruptible boundary.")
        return int(ready.read_text())

    def _assert_real_cancellation(self, process, output, errors):
        self.assertEqual(process.returncode, 130, output + errors)
        self.assertEqual((self.root / "actual-child-interrupted").read_text(), str(signal.SIGINT))
        actual = json.loads((self.root / "actual-finalizer.json").read_text())
        self.assertEqual(actual, {"runId": self.owner["runId"], "jobStatus": "cancelled"})
        self.assertEqual(
            json.loads((self.evidence / "execution.json").read_text())["outcome"]["exitCode"], "Cancelled"
        )
        self.assertEqual(
            json.loads((self.evidence / "cleanup.json").read_text())["remainingResources"]["processIds"], []
        )

    def _stop_real_children(self, process, child_pid):
        if process.poll() is None:
            process.kill()
        process.communicate(timeout=10)
        if child_pid is not None and not (self.root / "actual-child-interrupted").exists():
            try:
                os.kill(child_pid, signal.SIGKILL)
            except ProcessLookupError:
                pass

    def test_actual_job_cancellation_reaches_exact_down_without_rewriting_first_result(self):
        self.write_receipt()
        with patch.object(helper, "verify_tool"), patch.object(
            helper.subprocess, "run", return_value=self.down_result()
        ) as run:
            self.assertEqual(helper.finalize(self.root, self.receipt, self.evidence, job_status="cancelled"), 0)
            self.assertEqual(run.call_args.kwargs["env"]["HEXALITH_CI_JOB_STATUS"], "cancelled")

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
                helper.subprocess, "run", return_value=self.down_result(remaining)
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
        class Child:
            returncode = 17

            def communicate(child, timeout):
                self.write_receipt()
                return json.dumps({"outcome": {"exitCode": "ProductOrTest"}}), None

        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen", return_value=Child()
        ), patch.object(helper, "finalize", return_value=1) as cleanup:
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence), 17
            )
            cleanup.assert_called_once()

    def test_invalid_child_output_still_finalizes(self):
        class Child:
            returncode = 1

            def communicate(child, timeout):
                self.write_receipt()
                return "interrupted", None

        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen", return_value=Child()
        ), patch.object(helper, "finalize", return_value=0) as cleanup:
            with self.assertRaises(ValueError):
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence)
            cleanup.assert_called_once()

    def test_cancellation_during_execution_runs_finalizer_and_preserves_cancel(self):
        class Child:
            returncode = 130

            def poll(child):
                return None

            def send_signal(child, signum):
                self.assertEqual(signum, helper.signal.SIGINT)

            def communicate(child, timeout):
                self.write_receipt()
                helper.signal.raise_signal(helper.signal.SIGTERM)
                return '{"outcome":{"exitCode":"Cancelled"}}', None

        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen", return_value=Child()
        ), patch.object(helper, "finalize", return_value=0) as cleanup:
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence), 130
            )
            cleanup.assert_called_once()

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
                helper.write_evidence(destination, "cleanup.json", {})
        self.assertFalse((self.root / "foreign").exists())
        self.assertFalse((self.root / "TestResults").exists())

    def test_evidence_rejects_a_symlinked_ancestor_and_an_unlisted_filename(self):
        outside = self.root / "outside"
        outside.mkdir()
        (self.root / "TestResults").symlink_to(outside, target_is_directory=True)
        with self.assertRaises(ValueError):
            helper.write_evidence(self.evidence, "cleanup.json", {})
        with self.assertRaises(ValueError):
            helper.write_evidence(self.evidence, "../sentinel.json", {})
        self.assertEqual(list(outside.iterdir()), [])

    def test_evidence_refuses_file_links_without_truncating_the_external_file(self):
        self.evidence.mkdir(parents=True)
        sentinel = self.root / "sentinel.json"
        sentinel.write_text("original")
        target = self.evidence / "cleanup.json"
        for hard_link in (False, True):
            with self.subTest(hard_link=hard_link):
                if hard_link:
                    os.link(sentinel, target)
                else:
                    target.symlink_to(sentinel)
                with self.assertRaises((OSError, ValueError)):
                    helper.write_evidence(self.evidence, "cleanup.json", {})
                self.assertEqual(sentinel.read_text(), "original")
                target.unlink()

    def test_receipt_must_stay_under_actual_runner_temp_without_link_ancestors(self):
        foreign = self.root / "foreign"
        foreign.mkdir()
        link = self.runner_temp / "link"
        link.symlink_to(foreign, target_is_directory=True)
        for receipt in (foreign / "owner.json", link / "owner.json", self.runner_temp / ".." / "owner.json"):
            with self.subTest(receipt=receipt), patch.object(helper.subprocess, "run") as tool:
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
        with patch.object(helper.subprocess, "run") as process:
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

        class Child:
            returncode = 0

            def communicate(child, timeout):
                self.write_receipt()
                return '{"outcome":{"exitCode":"Success"}}', None

        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"):
            with patch.object(helper.subprocess, "Popen", return_value=Child()) as start:
                with patch.object(helper, "finalize", return_value=0):
                    self.assertEqual(
                        helper.run_integration(self.root, manifest, ["real"], self.receipt, self.evidence), 0
                    )
        arguments = start.call_args.args[0]
        self.assertEqual(
            arguments,
            helper.TOOL
            + [
                "test",
                "--manifest=" + str(self.root / manifest),
                "--mode=package",
                "--ci-owner-receipt=" + str(self.receipt),
                "--output=json",
                "--profile=real",
            ],
        )

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
        class Child:
            returncode = 0

            def communicate(child, timeout):
                self.write_receipt()
                return '{"outcome":{"exitCode":"Success"}}', None

        with patch.object(helper, "verify_tool"), patch.object(helper, "verify_preparation"), patch.object(
            helper.subprocess, "Popen", return_value=Child()
        ), patch.object(helper, "finalize", return_value=1):
            self.assertEqual(
                helper.run_integration(self.root, "module.json", ["real"], self.receipt, self.evidence), 1
            )


if __name__ == "__main__":
    unittest.main()
