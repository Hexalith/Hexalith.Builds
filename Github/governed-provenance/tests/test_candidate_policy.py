"""Offline authenticated-transport fixtures; no live gate qualification claims."""
import base64
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("candidate_policy", ROOT / "candidate_policy.py")
POLICY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(POLICY)


class CandidatePolicyTests(unittest.TestCase):
    def setUp(self):
        self.env = {
            "GITHUB_REPOSITORY": "Hexalith/Orders", "GITHUB_RUN_ID": "101",
            "GITHUB_RUN_ATTEMPT": "1", "GH_TOKEN": "read-only-fixture",
            "GITHUB_EVENT_NAME": "pull_request", "GITHUB_SHA": "a" * 40,
            "GITHUB_REF": "refs/pull/7/merge",
            "ACTIONS_ID_TOKEN_REQUEST_URL": "https://oidc.invalid/?request=fixture",
            "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "fixture-oidc-request",
        }
        self.event = "pull_request"
        self.state = "open"
        self.parents = ["c" * 40, "b" * 40]
        self.events = []
        self.calls = []
        self.run_head = None

    def get(self, url, token):
        self.calls.append((url, token))
        if url.startswith("https://oidc.invalid/"):
            self.assertEqual(token, "fixture-oidc-request")
            return {"value": "fixture-signed-token"}
        self.assertEqual(token, "read-only-fixture")
        if url.endswith("/approvals"):
            return [{"state": "approved", "user": {"id": 1}, "environments": [{"name": "candidate-policy"}]}]
        if "/environments/" in url:
            return {"protection_rules": [{"type": "required_reviewers", "reviewers": [{"id": 1}]}]}
        if "/actions/runs/" in url:
            return {"id": int(self.env["GITHUB_RUN_ID"]), "repository": {"full_name": "Hexalith/Orders"},
                    "run_attempt": int(self.env["GITHUB_RUN_ATTEMPT"]), "event": self.event,
                    "head_sha": self.run_head or ("a" * 40 if self.event == "workflow_dispatch" else "b" * 40), "created_at": "2026-10-10T00:00:00Z", "pull_requests": [{"number": 7, "head": {"sha": "b" * 40}, "base": {"sha": "c" * 40}}]}
        if "/pulls/" in url:
            return {"state": self.state, "merged": False, "created_at": "2026-10-01T00:00:00Z",
                    "merge_commit_sha": "a" * 40, "base": {"sha": "c" * 40, "ref": "main"}, "head": {"sha": "b" * 40}}
        if "/events?" in url:
            return self.events
        if "/git/commits/" in url:
            return {"sha": "a" * 40, "parents": [{"sha": p} for p in self.parents]}
        if "/git/ref/" in url:
            return {"object": {"sha": "c" * 40}}
        self.fail(url)

    def issue(self):
        result = POLICY.issue("orders", self.env, self.get)
        raw = base64.b64decode(result["payload"])
        import hashlib
        self.assertIn("audience=hexalith-candidate-v1%3A" + hashlib.sha256(raw).hexdigest(), self.calls[-1][0])
        self.assertEqual(result["token"], "fixture-signed-token")
        return json.loads(raw)

    def test_pr_exact_merge_and_epoch(self):
        self.events = [{"event": "reopened", "id": 9001, "created_at": "2026-10-09T00:00:00Z"}]
        value = self.issue()
        self.assertEqual(value["targetCommit"], "a" * 40)
        self.assertEqual(value["pr"]["headCommit"], "b" * 40)
        self.assertEqual(value["pr"]["baseCommit"], "c" * 40)
        self.assertEqual(value["epoch"], "9001")
        self.assertEqual(value["policyId"], POLICY.POLICY_ID)

    def test_pr_run_head_and_merge_execution_context_are_distinct(self):
        value = self.issue()
        self.assertNotEqual(value["targetCommit"], value["pr"]["headCommit"])
        self.assertNotEqual(value["pr"]["headCommit"], value["pr"]["baseCommit"])
        self.assertEqual(self.get("https://api.github.com/repos/Hexalith/Orders/actions/runs/101", "read-only-fixture")["head_sha"], value["pr"]["headCommit"])
        self.assertEqual(self.env["GITHUB_SHA"], value["targetCommit"])

    def test_wrong_github_execution_context_or_api_head_refuses(self):
        for field, replacement, message in [
            ("GITHUB_SHA", "b" * 40, "pr-binding-mismatch"),
            ("GITHUB_REF", "refs/pull/7/head", "pr-binding-mismatch"),
            ("GITHUB_EVENT_NAME", "workflow_dispatch", "github-event-binding-mismatch"),
        ]:
            with self.subTest(field=field):
                original = self.env[field]
                self.env[field] = replacement
                with self.assertRaisesRegex(ValueError, message):
                    self.issue()
                self.env[field] = original
        self.run_head = "a" * 40
        with self.assertRaisesRegex(ValueError, "pr-binding-mismatch"):
            self.issue()

    def test_retry_same_request_distinct_run_and_attempt(self):
        original = self.issue()
        self.env["GITHUB_RUN_ATTEMPT"] = "2"
        retry = self.issue()
        self.assertEqual(original["gateRunId"], retry["gateRunId"])
        self.assertNotEqual(original["runId"], retry["runId"])
        self.assertNotEqual(original["attemptId"], retry["attemptId"])
        self.assertEqual(retry["attemptSequence"], 2)

    def test_release_dispatch_independent_exact_revision(self):
        self.event = "workflow_dispatch"
        self.env["GITHUB_EVENT_NAME"] = "workflow_dispatch"
        self.env["GITHUB_REF"] = "refs/heads/main"
        first = self.issue()
        self.env["GITHUB_RUN_ID"] = "102"
        second = self.issue()
        self.assertNotEqual(first["gateRunId"], second["gateRunId"])
        self.assertEqual(first["targetCommit"], second["targetCommit"])
        self.assertNotIn("pr", second)
        self.assertEqual(second["kind"], "release")

    def test_closed_and_wrong_parent_requests_refuse_before_oidc(self):
        for state, parents, message in [("closed", self.parents, "request-not-pending"),
                                        ("open", list(reversed(self.parents)), "pr-binding-mismatch")]:
            self.state, self.parents = state, parents
            self.calls.clear()
            with self.assertRaisesRegex(ValueError, message):
                self.issue()
            self.assertFalse(any("oidc.invalid" in url for url, _ in self.calls))

    def test_unrecognized_event_refuses(self):
        self.event = "push"
        self.env["GITHUB_EVENT_NAME"] = "push"
        with self.assertRaisesRegex(ValueError, "authenticated-request-required"):
            self.issue()

    def test_workflow_requires_protected_approval_and_disposable_host(self):
        text = (ROOT.parents[1] / ".github/workflows/candidate-policy.yml").read_text()
        self.assertIn("environment: candidate-policy", text)
        self.assertIn("runs-on: ubuntu-latest", text)
        self.assertIn("job_workflow_sha", text)
        self.assertIn("id-token: write", text)
        self.assertNotIn("packages: write", text)
        self.assertNotIn("contents: write", text)

    def test_only_approved_consumer_gets_nonce_proof_capability_and_exact_output_policy(self):
        text = (ROOT.parents[1] / ".github/workflows/domain-ci.yml").read_text()
        consumer = text.split("  aspire-tests:", 1)[1].split("  performance-tests:", 1)[0]
        build = text.split("  build-and-test:", 1)[1].split("  aspire-tests:", 1)[0]
        self.assertIn("id-token: write", consumer)
        self.assertNotIn("id-token: write", build)
        self.assertNotIn("id-token: write", text.split("  performance-tests:", 1)[1])
        self.assertNotIn("id-token: write", text.split("jobs:", 1)[0])
        self.assertIn("candidate_workspace.py", consumer)
        self.assertIn("CANDIDATE_BUILDS_WORKFLOW_SHA: ${{ job.workflow_sha }}", consumer)



if __name__ == "__main__":
    unittest.main()
