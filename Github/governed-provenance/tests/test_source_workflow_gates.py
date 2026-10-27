"""Positive and refusal controls for exact-source workflow admission."""
import copy
import json
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
            with mock.patch.object(gates, "gh_json", side_effect=replies) as api:
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


if __name__ == "__main__":
    unittest.main()
