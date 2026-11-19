"""Exercise the shipped verifier and its FrontComposer Release bindings offline."""

import contextlib
import io
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from test_split_publication import (
    BASE, BUILDS, CANDIDATE, ROOT, canonical, descriptor, empty_graph, evaluator, handoff,
    policy_projection, provenance, quality_run, split_publication, zip_bytes,
)

import cryptographic_attestation


FIXTURES = Path(__file__).parent / "fixtures" / "sigstore"
SHIPPED_VERIFIER = ROOT / "sigstore-verifier" / "dist.cjs"


class CryptographicAttestationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bundles = json.loads((FIXTURES / "synthetic-bundles.json").read_bytes())
        verifier_dir = Path(cls.enterClassContext(tempfile.TemporaryDirectory()))
        cls.test_verifier = verifier_dir / "dist.cjs"
        shutil.copyfile(SHIPPED_VERIFIER, cls.test_verifier)
        shutil.copyfile(FIXTURES / "synthetic-trusted-root.json", verifier_dir / "trusted-root.json")

    def setUp(self):
        job = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.workspace = job / "workspace"
        self.workspace.mkdir()
        runner_temp = job / "runner-temp"
        runner_temp.mkdir()
        self.bundle_path = runner_temp / "attestation.json"
        self.workflow_provenance = provenance(same_repository=True)
        release = self.workflow_provenance["release"]
        evaluator = {"caller": release["caller"], "reusable": release["reusable"], "actions": []}
        evaluator["definition_digest"] = canonical.canonical_digest(evaluator)
        self.release_run = {
            "repository": descriptor.FRONTCOMPOSER_REPOSITORY,
            "workflow_path": descriptor.FRONTCOMPOSER_RELEASE_WORKFLOW,
            "run_id": 40,
            "run_attempt": 1,
        }
        slug = cryptographic_attestation.FRONTCOMPOSER_SLUG
        self.environment = {
            "GITHUB_SERVER_URL": "https://github.com",
            "GITHUB_REPOSITORY": slug,
            "GITHUB_WORKFLOW_REF": f"{slug}/{descriptor.FRONTCOMPOSER_RELEASE_WORKFLOW}@refs/heads/main",
            "GITHUB_REF": "refs/heads/main",
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_SHA": CANDIDATE,
            "GITHUB_RUN_ID": "40",
            "GITHUB_RUN_ATTEMPT": "1",
            "GITHUB_REPOSITORY_ID": "1",
            "GITHUB_REPOSITORY_OWNER_ID": "2",
            "RUNNER_TEMP": str(runner_temp),
            "SPLIT_ARCHIVE": "candidate.zip",
            "SPLIT_BUNDLE": str(self.bundle_path),
            "SPLIT_METADATA": "publication-state.json",
            "SPLIT_BUILDS_EXECUTION_SHA": BUILDS,
            "SPLIT_EXECUTION_MARKER": "",
        }
        self.members = {f"packages/Demo.{i}.1.2.3.nupkg": f"package {i}".encode() for i in range(8)}
        self.members["release-notes.md"] = b"Test release"
        self.document = {
            "schema": descriptor.DESCRIPTOR_SCHEMA,
            "release_run": self.release_run,
            "candidate": CANDIDATE,
            "ci_handoff_sha256": "1" * 64,
            "dependency_policy": policy_projection(),
            "evaluator": evaluator,
            "release_plan": {
                "version": "1.2.3", "tag": "v1.2.3", "release_name": "1.2.3", "release_notes_path": "release-notes.md",
            },
            "files": [
                {
                    "path": path,
                    "asset_name": Path(path).name if path.startswith("packages/") else None,
                    "role": "package" if path.startswith("packages/") else "release-metadata",
                    "sha256": canonical.sha256_bytes(payload),
                    "size": len(payload),
                }
                for path, payload in sorted(self.members.items())
            ],
        }

    def verify(self, bundle_name, *, archive_digest=None):
        bundle = canonical.canonical_bytes(self.bundles[bundle_name])
        self.bundle_path.write_bytes(bundle)
        archive = zip_bytes([*self.members.items(), ("publication-candidate.json", canonical.canonical_bytes(self.document))])
        (self.workspace / "candidate.zip").write_bytes(archive)
        (self.workspace / "publication-state.json").write_bytes(canonical.canonical_bytes({
            "workflow_provenance": self.workflow_provenance,
        }))
        output = io.StringIO()
        error = io.StringIO()
        environment = {**self.environment, "SPLIT_EXPECTED_SHA256": archive_digest or canonical.sha256_bytes(archive)}
        with (
            contextlib.chdir(self.workspace),
            contextlib.redirect_stdout(output),
            contextlib.redirect_stderr(error),
            mock.patch.dict(os.environ, environment),
            mock.patch.object(cryptographic_attestation, "_VERIFIER", self.test_verifier),
        ):
            status = split_publication.main(["verify-attestation"])
        self.assertFalse((self.workspace / "publication-started.json").exists())
        return status, output.getvalue(), error.getvalue()

    def test_shipped_trust_root_verifies_a_real_github_bundle(self):
        identity = cryptographic_attestation._verified_extensions((FIXTURES / "github-reusable-workflow.json").read_bytes())
        self.assertEqual("https://token.actions.githubusercontent.com", identity["1.3.6.1.4.1.57264.1.8"])
        self.assertEqual("09b495c3f12c7881b3cc17209a327792065c1a1d", identity["1.3.6.1.4.1.57264.1.10"])
        self.assertEqual(
            "https://github.com/malancas/attest-demo/actions/runs/9228858953/attempts/1",
            identity["1.3.6.1.4.1.57264.1.21"],
        )

    def test_shipped_trust_root_rejects_the_synthetic_certificate(self):
        with self.assertRaisesRegex(canonical.ContractError, "cryptographic verification failed"):
            cryptographic_attestation._verified_extensions(canonical.canonical_bytes(self.bundles["valid"]))

    def test_command_accepts_a_verified_statement_for_all_eight_archived_packages(self):
        status, output, error = self.verify("valid")
        self.assertEqual((0, ""), (status, error))
        self.assertEqual({"bundle_sha256": canonical.canonical_digest(self.bundles["valid"])}, json.loads(output))

    def test_command_rejects_an_invalid_signature_even_with_valid_log_evidence(self):
        invalid_bundle = canonical.canonical_bytes(self.bundles["invalid-signature"])
        result = subprocess.run(["node", str(self.test_verifier)], input=invalid_bundle, capture_output=True, check=False)
        self.assertEqual(1, result.returncode)
        self.assertIn(b"SIGNATURE_ERROR", result.stderr)
        status, output, error = self.verify("invalid-signature")
        self.assertEqual((1, ""), (status, output))
        self.assertIn("cryptographic verification failed", error)

    def test_command_rejects_mismatched_signed_evidence(self):
        cases = {
            "wrong-issuer": "1.3.6.1.4.1.57264.1.8",
            "wrong-signer": "build signer URI",
            "wrong-signer-sha": "1.3.6.1.4.1.57264.1.10",
            "wrong-candidate": "1.3.6.1.4.1.57264.1.13",
            "wrong-ref": "1.3.6.1.4.1.57264.1.14",
            "wrong-attempt": "1.3.6.1.4.1.57264.1.21",
            "wrong-package-digest": "authenticated package set",
        }
        for bundle, expected_diagnostic in cases.items():
            with self.subTest(bundle=bundle):
                status, output, error = self.verify(bundle)
                self.assertEqual((1, ""), (status, output))
                self.assertIn(expected_diagnostic, error)

    def test_command_rejects_an_archive_from_another_attempt(self):
        self.environment["GITHUB_RUN_ATTEMPT"] = "2"
        status, output, error = self.verify("valid")
        self.assertEqual((1, ""), (status, output))
        self.assertIn("current Release candidate and attempt", error)

    def test_command_rejects_wrong_upload_digest_or_changed_package_bytes(self):
        status, output, error = self.verify("valid", archive_digest="0" * 64)
        self.assertEqual((1, ""), (status, output))
        self.assertIn("authenticated upload digest", error)
        self.members["packages/Demo.7.1.2.3.nupkg"] = b"package X"
        status, output, error = self.verify("valid")
        self.assertEqual((1, ""), (status, output))
        self.assertIn("does not match its descriptor digest", error)

    def test_command_keeps_builds_execution_and_signer_shas_distinct(self):
        self.environment["SPLIT_BUILDS_EXECUTION_SHA"] = CANDIDATE
        status, output, error = self.verify("valid")
        self.assertEqual((1, ""), (status, output))
        self.assertIn("workflow provenance does not match", error)

    def test_manifest_sealing_rechecks_the_bundle(self):
        ci_evaluator = evaluator()
        ci_evaluator["caller"].update(workflow_path=".github/workflows/ci.yml", commit=CANDIDATE)
        ci_evaluator["reusable"]["workflow_path"] = ".github/workflows/domain-ci.yml"
        ci_evaluator["definition_digest"] = canonical.canonical_digest({
            key: ci_evaluator[key] for key in ("caller", "reusable", "actions")
        })
        ci_handoff = canonical.canonical_bytes({
            "schema": handoff.CI_HANDOFF_SCHEMA,
            "run": {
                **self.release_run, "workflow_path": ".github/workflows/ci.yml", "run_id": 9,
                "event": "push", "branch": "main", "candidate": CANDIDATE,
            },
            "revisions": {"base": BASE, "candidate": CANDIDATE, "merge_base": None},
            "evaluator": ci_evaluator,
            "dependency_policy": policy_projection(),
            "dependency_graph": empty_graph(),
        })
        handoff_path = self.workspace / "ci-handoff.json"
        handoff_path.write_bytes(ci_handoff)
        ci_digest = canonical.sha256_bytes(ci_handoff)
        self.document["ci_handoff_sha256"] = ci_digest
        self.workflow_provenance["ci"]["evidence_sha256"] = ci_digest
        self.workflow_provenance["definition_digest"] = canonical.canonical_digest({
            key: self.workflow_provenance[key] for key in ("ci", "release")
        })
        directory = self.workspace / "authenticated-candidate"
        for name, payload in self.members.items():
            path = directory / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        (directory / "publication-candidate.json").write_bytes(canonical.canonical_bytes(self.document))
        state = {
            "workflow_provenance": self.workflow_provenance,
            "quality_run": quality_run(),
            "live_verified": True,
            "sealed_at": "2025-01-01T12:00:00Z",
            "publish_gate": "false",
        }
        with (
            mock.patch.dict(os.environ, self.environment),
            mock.patch.object(cryptographic_attestation, "_VERIFIER", self.test_verifier),
        ):
            self.bundle_path.write_bytes(canonical.canonical_bytes(self.bundles["valid"]))
            result = split_publication.classify_authenticated(directory, handoff_path, self.bundle_path, state)
            self.assertEqual("attested", result["attestation_status"])
            self.assertFalse(result["publish_authorized"])
            self.bundle_path.write_bytes(canonical.canonical_bytes(self.bundles["invalid-signature"]))
            with self.assertRaisesRegex(canonical.ContractError, "cryptographic verification failed"):
                split_publication.classify_authenticated(directory, handoff_path, self.bundle_path, state)
