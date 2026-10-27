"""Recovery execution identity must remain tied to source approval and immutable bytes."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import execution_identity as identity
import governed_provenance as provenance
import test_governed_provenance as fixtures


class ExecutionIdentityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.builds = self.root / "builds"
        self.source = self.root / "source"
        self.builds.mkdir()
        self.source.mkdir()
        fixtures.CheckoutBuilder(self.builds).write("Github/governed-provenance/helper.py", "# approved\n")
        fixtures.CheckoutBuilder(self.builds).write("Github/publish-containers/helper.py", "# approved\n")
        self.builds_sha = fixtures.StandaloneIdentityTests.commit(self.builds)
        builder = fixtures.CheckoutBuilder(self.source)
        builder.write(".github/workflows/release.yml", f"env:\n  HEXALITH_BUILDS_EXECUTION_SHA: {self.builds_sha}\n")
        builder.write(".github/workflows/recover-partial-release.yml", "jobs:\n  recover:\n    steps:\n      - run: echo fixture\n")
        self.source_sha = fixtures.StandaloneIdentityTests.commit(self.source)
        arguments = fixtures.Arguments(self.builds, workflow_mode="standalone", stage="release", workflow_root=str(self.source),
                              builds_execution_sha=self.builds_sha, workflow_path=".github/workflows/recover-partial-release.yml",
                              workflow_repository="fixture/recovery", workflow_sha=self.source_sha,
                              workflow_ref="fixture/recovery/.github/workflows/recover-partial-release.yml@refs/heads/main",
                              workflow_file_path=".github/workflows/recover-partial-release.yml", caller_repository="fixture/recovery",
                              caller_workflow_ref="fixture/recovery/.github/workflows/recover-partial-release.yml@refs/heads/main",
                              candidate=self.source_sha, expected_evaluator_digest="")
        self.provenance_path = self.source / "provenance.json"
        self.provenance_path.write_bytes(provenance.canonical_bytes(provenance.build_provenance(arguments)))
        environment = mock.patch.dict(os.environ, {"GITHUB_REPOSITORY": "fixture/recovery"})
        environment.start()
        self.addCleanup(environment.stop)

    def capture(self, **changes):
        arguments = dict(workspace=self.source, builds_root=self.builds, source=self.source_sha,
                         declared=self.builds_sha, provenance=self.provenance_path)
        arguments.update(changes)
        return identity.identity(**arguments)

    def test_valid_identity_binds_source_execution_and_provenance(self):
        result = self.capture()
        self.assertEqual(result["source"], self.source_sha)
        self.assertEqual(result["builds_execution_sha"], self.builds_sha)
        self.assertEqual(len(result["helpers"]), 2)
        self.assertEqual(len(result["provenance_sha256"]), 64)

    def test_different_execution_revision_is_refused(self):
        with self.assertRaisesRegex(ValueError, "source release workflow declaration"):
            self.capture(declared="a" * 40)

    def test_modified_or_extra_shared_helper_is_refused(self):
        helper = self.builds / "Github/publish-containers/helper.py"
        original = helper.read_bytes()
        helper.write_bytes(original + b"# drift\n")
        with self.assertRaises(provenance.ProvenanceError):
            self.capture()
        helper.write_bytes(original)
        (helper.parent / "unapproved.py").write_text("# extra\n")
        with self.assertRaisesRegex(ValueError, "unapproved shared helper"):
            self.capture()

    def test_source_workflow_and_provenance_drift_are_refused(self):
        workflow = self.source / ".github/workflows/release.yml"
        original = workflow.read_bytes()
        workflow.write_bytes(original + b"# drift\n")
        with self.assertRaisesRegex(ValueError, "workflow bytes changed"):
            self.capture()
        workflow.write_bytes(original)
        document = json.loads(self.provenance_path.read_text())
        document["closure"]["closure_digest"] = "b" * 64
        self.provenance_path.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, "closure changed"):
            self.capture()

    def test_source_declaration_requires_one_consistent_literal(self):
        for declaration in ("env: {}", "  HEXALITH_BUILDS_EXECUTION_SHA: main\n",
                            "  HEXALITH_BUILDS_EXECUTION_SHA: ${{ env.SHA }}\n",
                            f"  HEXALITH_BUILDS_EXECUTION_SHA: {self.builds_sha}\n  HEXALITH_BUILDS_EXECUTION_SHA: {'b' * 40}\n"):
            with self.subTest(declaration=declaration), self.assertRaises(ValueError):
                identity.approved_builds_sha(declaration)


if __name__ == "__main__":
    unittest.main()
