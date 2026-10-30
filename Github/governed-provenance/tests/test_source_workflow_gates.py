"""Positive and refusal controls for exact-source workflow admission."""
import copy
import json
import subprocess
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import source_workflow_gates as gates

SOURCE = "a" * 40
WORKFLOW = "ci.yml"


def run(**changes):
    value = dict(id=7, run_number=3, run_attempt=1, head_sha=SOURCE, head_branch="main", event="push",
                 path=".github/workflows/ci.yml", status="completed", conclusion="success")
    value.update(changes)
    return value


class SourceWorkflowGateTests(unittest.TestCase):
    def test_cli_refuses_evidence_escape_before_api_requests(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory).resolve()
            for evidence in ("../outside.json", str(workspace.parent / "outside.json")):
                with self.subTest(evidence=evidence), mock.patch.object(gates.Path, "cwd", return_value=workspace), \
                        mock.patch.object(sys, "argv", [
                            "source_workflow_gates.py", "--repository", "owner/repository", "--source", SOURCE,
                            "--workflow", WORKFLOW, "--evidence", evidence,
                        ]), mock.patch.object(gates, "qualify") as qualify:
                    self.assertEqual(gates.main(), 1)
                    qualify.assert_not_called()

    def test_cli_accepts_absolute_evidence_inside_the_workspace(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory).resolve()
            evidence = workspace / "evidence.json"
            with mock.patch.object(gates.Path, "cwd", return_value=workspace), \
                    mock.patch.object(sys, "argv", [
                        "source_workflow_gates.py", "--repository", "owner/repository", "--source", SOURCE,
                        "--workflow", WORKFLOW, "--evidence", str(evidence),
                    ]), mock.patch.object(gates, "qualify") as qualify:
                self.assertEqual(gates.main(), 0)
                self.assertEqual(evidence, qualify.call_args.args[-1])

    def test_accepts_latest_exact_source_success(self):
        selected = gates.select_latest([{"workflow_runs": [run(id=6, run_number=2), run()]}], SOURCE, "main", WORKFLOW)
        self.assertEqual(gates.require_success(selected, SOURCE, "main", WORKFLOW)["id"], 7)

    def test_old_success_never_masks_new_failed_skipped_or_pending_attempt(self):
        for status, conclusion in (("completed", "failure"), ("completed", "skipped"), ("completed", "cancelled"),
                                   ("queued", None), ("in_progress", None), ("waiting", None)):
            with self.subTest(status=status, conclusion=conclusion):
                latest = run(run_attempt=2, status=status, conclusion=conclusion)
                selected = gates.select_latest([{"workflow_runs": [run(), latest]}], SOURCE, "main", WORKFLOW)
                self.assertEqual(selected, latest)
                with self.assertRaisesRegex(gates.GateError, "must succeed"):
                    gates.require_success(selected, SOURCE, "main", WORKFLOW)

    def test_new_run_failure_masks_old_run_success_across_pages(self):
        selected = gates.select_latest([{"workflow_runs": [run(id=8, run_number=4, conclusion="failure")]},
                                       {"workflow_runs": [run()]}], SOURCE, "main", WORKFLOW)
        with self.assertRaises(gates.GateError):
            gates.require_success(selected, SOURCE, "main", WORKFLOW)

    def test_missing_or_wrong_source_branch_event_are_refused(self):
        for runs in ([], [run(head_sha="b" * 40)], [run(head_branch="feature")], [run(event="pull_request")]):
            with self.subTest(runs=runs), self.assertRaisesRegex(gates.GateError, "no push run exists"):
                gates.select_latest([{"workflow_runs": runs}], SOURCE, "main", WORKFLOW)

    def test_malformed_and_ambiguous_runs_are_refused(self):
        for pages in (None, [], [{}], [{"workflow_runs": [None]}], [{"workflow_runs": [run(run_attempt=0)]}],
                      [{"workflow_runs": [run(), run(id=8)]}]):
            with self.subTest(pages=pages), self.assertRaises(gates.GateError):
                gates.select_latest(pages, SOURCE, "main", WORKFLOW)

    def test_fresh_attempt_and_identity_drift_are_refused(self):
        for changes in (dict(run_attempt=2), dict(id=8), dict(run_number=4), dict(head_sha="b" * 40),
                        dict(path=".github/workflows/other.yml"), dict(event="pull_request")):
            with self.subTest(changes=changes), self.assertRaises(gates.GateError):
                gates.require_success(run(**changes), SOURCE, "main", WORKFLOW, run())

    def test_each_required_gate_runs_and_retains_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "gates.json"
            replies = []
            for index, workflow in enumerate(("ci.yml", "story-guards.yml", "source-reference.yml"), 1):
                row = run(id=index, path=f".github/workflows/{workflow}")
                replies.extend(([{"workflow_runs": [row]}], row))
            with mock.patch.object(gates, "gh_json", side_effect=replies + replies) as api:
                report = gates.qualify("owner/repository", SOURCE, "main",
                                       ["ci.yml", "story-guards.yml", "source-reference.yml"], output)
            self.assertEqual(report["result"], "pass")
            self.assertEqual(len(report["gates"]), 3)
            self.assertNotIn("status=success", str(api.call_args_list))
            self.assertEqual(json.loads(output.read_text()), report)

    def test_refusal_retains_observed_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "gates.json"
            row = run(conclusion="skipped")
            with mock.patch.object(gates, "gh_json", side_effect=([{"workflow_runs": [row]}], row)):
                with self.assertRaises(gates.GateError):
                    gates.qualify("owner/repository", SOURCE, "main", [WORKFLOW], output)
            self.assertEqual(json.loads(output.read_text())["result"], "refused")
            self.assertIn("skipped", output.read_text())

    def test_refresh_refuses_interleaved_attempt_new_run_and_status_changes(self):
        for change in (dict(run_attempt=2), dict(id=8, run_number=4), dict(conclusion="failure"),
                       dict(conclusion="skipped"), dict(status="in_progress", conclusion=None)):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                first = run()
                second = run(id=9, path=".github/workflows/story-guards.yml")
                changed = run(**change)
                replies = [[{"workflow_runs": [first]}], first, [{"workflow_runs": [second]}], second,
                           [{"workflow_runs": [changed]}], changed]
                output = Path(directory) / "gates.json"
                with mock.patch.object(gates, "gh_json", side_effect=replies) as api:
                    with self.assertRaises(gates.GateError):
                        gates.qualify("owner/repository", SOURCE, "main", ["ci.yml", "story-guards.yml"], output)
                report = json.loads(output.read_text())
                self.assertEqual(report["result"], "refused")
                self.assertIn("refresh_pages", report["gates"][0])
                self.assertLessEqual(api.call_count, 6, "Drift must refuse without silently retrying.")

    def test_api_failures_retain_safe_request_context_without_cli_secrets(self):
        for code, classification in ((401, "authorization"), (403, "authorization"), (404, "not-found"),
                                     (429, "rate-limited"), (503, "server-error")):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / "gates.json"
                failure = subprocess.CompletedProcess([], 1, "", f"gh: HTTP {code}: request failed\nAuthorization: token super-secret")
                with mock.patch.object(gates.subprocess, "run", return_value=failure):
                    with self.assertRaises(gates.GateError) as rejected:
                        gates.qualify("owner/repository", SOURCE, "main", ["ci.yml"], output)
                report = json.loads(output.read_text())
                self.assertEqual(report["request_failure"], {
                    "endpoint": "repos/owner/repository/actions/workflows/ci.yml/runs", "method": "GET",
                    "exit_status": 1, "http_status": code, "classification": classification})
                self.assertIn(str(code), str(rejected.exception))
                self.assertNotIn("super-secret", output.read_text())
                self.assertNotIn("Authorization", output.read_text())


    def test_cli_and_invalid_json_failures_preserve_classification(self):
        scenarios = ((FileNotFoundError('private filesystem path'), 'cli-unavailable', None),
                     (subprocess.CompletedProcess([], 2, '', 'private transport error token=secret'), 'transport-or-cli-error', 2),
                     (subprocess.CompletedProcess([], 0, 'sensitive invalid payload', ''), 'invalid-json', 0))
        for result, classification, exit_status in scenarios:
            with self.subTest(classification=classification), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / 'gates.json'
                option = {'side_effect': result} if isinstance(result, Exception) else {'return_value': result}
                with mock.patch.object(gates.subprocess, 'run', **option), self.assertRaises(gates.GateError):
                    gates.qualify('owner/repository', SOURCE, 'main', ['ci.yml'], output)
                report = json.loads(output.read_text())
                self.assertEqual(report['request_failure']['classification'], classification)
                self.assertEqual(report['request_failure']['exit_status'], exit_status)
                self.assertNotIn('private', output.read_text())
                self.assertNotIn('sensitive', output.read_text())
                self.assertNotIn('secret', output.read_text())



if __name__ == "__main__":
    unittest.main()
