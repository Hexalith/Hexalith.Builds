"""Raw-message skip markers, independent of conventional-message parsing."""

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import commit_policy


class CommitPolicyTests(unittest.TestCase):
    def test_five_marker_forms_anywhere_and_horizontal_whitespace(self):
        for marker in ("skip ci", "ci skip", "no ci", "skip actions", "actions skip"):
            for space in (" ", "\t", "  \t"):
                value = marker.replace(" ", space).upper()
                for raw in (f"fix: subject [{value}]", f"fix: subject\n\nbody [{value}]\n",
                            f"Merge branch 'topic'\n\n[{value}]"):
                    with self.subTest(raw=raw):
                        self.assertFalse(commit_policy.allows(raw))

    def test_skip_checks_trailer_key_any_value(self):
        for value in ("true", "false", "", "anything"):
            self.assertFalse(commit_policy.allows(f"fix: subject\n\nSKIP-CHECKS \t: {value}\n"))

    def test_unrelated_text_is_allowed(self):
        for raw in ("fix: no skip", "fix: skip ci", "fix: [skip circuit]", "fix: [skip\nci]",
                    "fix: describe skip-checks: false inline", "fix: [no actions]"):
            self.assertTrue(commit_policy.allows(raw), raw)
