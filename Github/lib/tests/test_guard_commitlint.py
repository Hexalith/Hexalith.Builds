"""Real committed event histories; message/tool invocation uses an explicit seam."""

from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import expected_state as state
import guard_commitlint as policy
from test_expected_state import Repository


class RecordingTools:
    def __init__(self):
        self.calls = []

    def lint_message(self, message, identity):
        self.calls.append((message, identity))


class CommitRangeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repository = Repository(Path(temporary.name) / "git")
        self.base = self.commit("build: initialize fixture\n")
        self.first = self.commit("fix: first event change\n\ncomplete body\n\nRefs: #123\n")
        self.second = self.commit("feat: second event change\n")
        self.git = state.Git(self.repository.root)
        self.tools = RecordingTools()

    def commit(self, message):
        self.repository.command("commit", "--allow-empty", "-qm", message)
        return self.repository.command("rev-parse", "HEAD")

    def lint(self, event_name="push", event=None, **changes):
        policy.lint(self.git, self.tools, event_name=event_name,
                    event={"before": self.base, "after": self.second} if event is None else event,
                    frozen_head=self.repository.command("rev-parse", "HEAD"), **changes)

    def test_multi_commit_push_preserves_complete_messages(self):
        self.lint()
        self.assertEqual(["commit " + self.first, "commit " + self.second], [c[1] for c in self.tools.calls])
        self.assertEqual("fix: first event change\n\ncomplete body\n\nRefs: #123\n", self.tools.calls[0][0])

    def test_pr_uses_event_endpoints_and_title_instead_of_checkout_merge(self):
        self.repository.command("switch", "-qc", "mainline", self.base)
        other = self.commit("fix: target branch advance\n")
        self.repository.command("merge", "--no-ff", self.second, "-m", "Merge pull request #1")
        self.lint("pull_request", {"pull_request": {"base": {"sha": self.base},
            "head": {"sha": self.second}, "title": "feat: edited pull request title"}})
        self.assertEqual(["commit " + self.first, "commit " + self.second, "pull request title"],
                         [c[1] for c in self.tools.calls])
        self.assertFalse(any(other in c[1] or c[0].startswith("Merge") for c in self.tools.calls))

    def test_zero_before_lints_all_history_and_empty_range_passes(self):
        self.repository.command("switch", "-qc", "unrelated", self.base)
        unrelated = self.commit("chore: unrelated branch must remain excluded")
        self.lint(event={"before": policy.ZERO, "after": self.second})
        self.assertEqual(["commit " + sha for sha in (self.base, self.first, self.second)],
                         [call[1] for call in self.tools.calls])
        self.assertFalse(any(unrelated in call[1] for call in self.tools.calls))
        self.tools.calls.clear()
        self.lint(event={"before": self.second, "after": self.second})
        self.assertEqual([], self.tools.calls)

    def test_dispatch_exactly_frozen_head_and_explicit_range(self):
        policy.lint(self.git, self.tools, event_name="workflow_dispatch", event={}, frozen_head=self.first)
        self.assertEqual(["commit " + self.first], [c[1] for c in self.tools.calls])
        self.tools.calls.clear()
        self.lint("local", {}, commit_range=(self.first, self.second))
        self.assertEqual(["commit " + self.second], [c[1] for c in self.tools.calls])

    def test_invalid_or_missing_context_never_falls_back(self):
        cases = [("push", {}), ("push", {"before": "HEAD", "after": self.second}),
                 ("push", {"before": self.base, "after": policy.ZERO}),
                 ("push", {"before": "f" * 40, "after": self.second}),
                 ("pull_request", {}), ("pull_request", {"pull_request": {"base": {"sha": self.base},
                    "head": {"sha": self.second}}}), ("", {}), ("schedule", {})]
        for name, event in cases:
            with self.subTest(name=name, event=event), self.assertRaises(state.ExpectedStateError):
                self.lint(name, event)
        self.assertEqual([], self.tools.calls)

    def test_malformed_payloads_and_explicit_range_fail(self):
        with self.assertRaises(state.ExpectedStateError):
            policy.load_event(self.repository.root / "missing.json")
        for payload in ("[]", "broken", "null"):
            self.repository.write("event.json", payload)
            with self.assertRaises(state.ExpectedStateError):
                policy.load_event(self.repository.root / "event.json")
        for commit_range in ((self.base,), ("HEAD", self.second)):
            with self.assertRaises(state.ExpectedStateError):
                self.lint(commit_range=commit_range)

    def test_selected_merge_messages_are_not_ignored(self):
        self.repository.command("switch", "-qc", "side", self.base)
        side = self.commit("fix: side change\n")
        self.repository.command("switch", "main")
        self.repository.command("merge", "--no-ff", side, "-m", "Merge branch side [skip ci]")
        head = self.repository.command("rev-parse", "HEAD")
        self.lint(event={"before": self.second, "after": head})
        self.assertTrue(any(message.startswith("Merge branch side [skip ci]") for message, _ in self.tools.calls))
