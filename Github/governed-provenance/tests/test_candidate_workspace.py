"""Exact ephemeral output policy keeps candidate source inputs visible to Git."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("candidate_workspace", ROOT / "candidate_workspace.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class CandidateWorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.gitrun(self.root, "init", "--quiet")
        (self.root / "source.cs").write_text("candidate")
        self.gitrun(self.root, "add", "source.cs")
        self.gitrun(self.root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "test: create candidate")
        self.output = self.root / MODULE.OUTPUT
        self.output.mkdir(parents=True)
        self.gitrun(self.output, "init", "--quiet")
        self.gitrun(self.output, "remote", "add", "origin", "https://github.com/Hexalith/Hexalith.Builds.git")
        (self.output / "helper.py").write_text("workflow source")
        self.gitrun(self.output, "add", "helper.py")
        self.gitrun(self.output, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "test: create preparation")
        self.commit = self.gitrun(self.output, "rev-parse", "HEAD").strip()

    @staticmethod
    def gitrun(root, *arguments):
        return subprocess.run(["git", "-C", str(root), *arguments], check=True, capture_output=True, text=True).stdout

    def test_supplied_checkout_layout_is_clean_but_other_source_edits_remain_visible(self):
        self.assertIn(".hexalith/", self.gitrun(self.root, "status", "--porcelain"))
        MODULE.register(self.root, self.commit)
        self.assertEqual(self.gitrun(self.root, "status", "--porcelain"), "")
        (self.root / ".hexalith/other-source.cs").write_text("not workflow output")
        self.assertIn(".hexalith/", self.gitrun(self.root, "status", "--porcelain"))
        (self.root / "source.cs").write_text("changed candidate")
        self.assertIn("source.cs", self.gitrun(self.root, "status", "--porcelain"))
        self.assertFalse((self.root / ".gitignore").exists())

    def test_tracked_overlap_refuses(self):
        (self.root / ".hexalith/tracked.cs").write_text("source")
        self.gitrun(self.root, "add", ".hexalith/tracked.cs")
        self.gitrun(self.root, "update-index", "--add", "--cacheinfo", "100644," + self.gitrun(self.root, "hash-object", "-w", "source.cs").strip() + ",.hexalith/builds-execution/source.cs")
        with self.assertRaisesRegex(ValueError, "overlaps-tracked-inputs"):
            MODULE.register(self.root, self.commit)

    def test_wrong_preparation_revision_refuses(self):
        with self.assertRaisesRegex(ValueError, "checkout-mismatch"):
            MODULE.register(self.root, "0" * 40)
