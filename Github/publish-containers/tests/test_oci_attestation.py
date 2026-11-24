import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess  # nosec B404 -- constructs GitHub CLI test responses.
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from oci_attestation import (  # noqa: E402
    BUILDS_WORKFLOW, OCI_INDEX_MEDIA_TYPE, PREFLIGHT_SCHEMA, expected_certificate, main, release_subjects,
    subject_row, verify_caller_publication, verify_subject,
)


INDEX = b'{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json"}'
DIGEST = hashlib.sha256(INDEX).hexdigest()
REGISTRY = "registry.example.test:5000"
IMAGE = f"{REGISTRY}/module"
OTHER_IMAGE = f"{REGISTRY}/module-api"
PROJECTS = "src/Module.csproj|module\nsrc/Module.Api.csproj|module-api"
REPOSITORY = "Hexalith/Hexalith.EventStore"
SOURCE_SHA = "a" * 40
BUILDS_SHA = "b" * 40
SOURCE_ENV = {
    "GITHUB_SERVER_URL": "https://github.com",
    "RUNNER_ENVIRONMENT": "github-hosted",
    "GITHUB_REPOSITORY": REPOSITORY,
    "GITHUB_REF": "refs/heads/main",
    "GITHUB_REF_PROTECTED": "true",
    "GITHUB_SHA": SOURCE_SHA,
    "RELEASE_SOURCE_SHA": SOURCE_SHA,
    "GITHUB_WORKFLOW_REF": f"{REPOSITORY}/.github/workflows/release.yml@refs/heads/main",
    "BUILDS_WORKFLOW_REF": f"{BUILDS_WORKFLOW}@{BUILDS_SHA}",
    "BUILDS_SHA": BUILDS_SHA,
}
CERTIFICATE = {
    "issuer": "https://token.actions.githubusercontent.com",
    "runnerEnvironment": "github-hosted",
    "sourceRepositoryURI": f"https://github.com/{REPOSITORY}",
    "sourceRepositoryDigest": SOURCE_SHA,
    "buildConfigURI": f"https://github.com/{REPOSITORY}/.github/workflows/release.yml@refs/heads/main",
    "sourceRepositoryRef": "refs/heads/main",
    "buildSignerURI": f"https://github.com/{BUILDS_WORKFLOW}@{BUILDS_SHA}",
    "buildSignerDigest": BUILDS_SHA,
}


def verification_result(certificate=CERTIFICATE, name=IMAGE, digest=DIGEST):
    return {"verificationResult": {
        "signature": {"certificate": certificate},
        "statement": {"subject": [{"name": name, "digest": {"sha256": digest}}]},
    }}


class OciAttestationTests(unittest.TestCase):
    def test_subject_uses_exact_index_bytes_and_tag_free_name_including_registry_port(self):
        image = f"{IMAGE}:1.2.3"
        evidence = {
            "result": "pass",
            "image": image,
            "media_type": OCI_INDEX_MEDIA_TYPE,
            "index_digest": f"sha256:{DIGEST}",
            "raw_index_sha256": DIGEST,
            "index_size": len(INDEX),
            "children": [{"digest": "sha256:" + "a" * 64}],
        }
        with tempfile.TemporaryDirectory() as directory, contextlib.chdir(directory):
            path = Path(directory)
            (path / "index.raw").write_bytes(INDEX)
            record = path / "oci-validation.json"
            record.write_text(json.dumps(evidence))
            self.assertEqual(subject_row(path, image), f"{DIGEST} *{IMAGE}")
            for field, value in {
                "index_digest": evidence["children"][0]["digest"],
                "raw_index_sha256": "a" * 64,
                "image": f"{IMAGE}:1.2.4",
                "media_type": "application/vnd.oci.image.manifest.v1+json",
                "result": "failure",
            }.items():
                with self.subTest(field=field):
                    record.write_text(json.dumps({**evidence, field: value}))
                    with self.assertRaisesRegex(ValueError, "does not bind"):
                        subject_row(path, image)
            record.write_text(json.dumps(evidence))
            (path / "index.raw").write_bytes(INDEX + b"\n")
            with self.assertRaisesRegex(ValueError, "does not bind"):
                subject_row(path, image)

    def test_subjects_require_every_declared_image_exactly_once(self):
        first = f"{DIGEST} *{IMAGE}"
        second = f"{'a' * 64} *{OTHER_IMAGE}"
        self.assertEqual(release_subjects(f"{first}\n{second}", PROJECTS, REGISTRY), {
            IMAGE: DIGEST, OTHER_IMAGE: "a" * 64,
        })
        for rows in ("", first, f"{first}\n{first}", f"{first}\n{second}:latest",
                     f"{first}\n{second.replace('a' * 64, 'A' * 64)}"):
            with self.subTest(rows=rows), self.assertRaises(ValueError):
                release_subjects(rows, PROJECTS, REGISTRY)

    def test_prepare_requires_subjects_to_match_the_retained_release_evidence(self):
        version = "1.2.3"
        with tempfile.TemporaryDirectory() as directory, contextlib.chdir(directory):
            root = Path(directory)
            evidence = root / "evidence" / version / "module"
            evidence.mkdir(parents=True)
            (evidence / "index.raw").write_bytes(INDEX)
            (evidence / "oci-validation.json").write_text(json.dumps({
                "result": "pass", "image": f"{IMAGE}:{version}", "media_type": OCI_INDEX_MEDIA_TYPE,
                "index_digest": f"sha256:{DIGEST}", "raw_index_sha256": DIGEST, "index_size": len(INDEX),
            }))
            checksums = root / "checksums.txt"
            args = ["oci_attestation", "prepare", str(checksums), str(root / "evidence"), version]
            env = {**SOURCE_ENV, "CONTAINER_PROJECTS": "src/Module.csproj|module", "REGISTRY": REGISTRY}
            with patch.object(sys, "argv", args), patch.dict(os.environ, env):
                err = io.StringIO()
                with patch.dict(os.environ, {"OCI_SUBJECTS": f"{'a' * 64} *{IMAGE}"}), contextlib.redirect_stderr(err):
                    self.assertEqual(main(), 1)
                self.assertIn("does not match the attested OCI subject", err.getvalue())
                self.assertFalse(checksums.exists())
                with patch.dict(os.environ, {"OCI_SUBJECTS": f"{DIGEST} *{IMAGE}"}):
                    self.assertEqual(main(), 0)
            self.assertEqual(checksums.read_text(), f"{DIGEST} *{IMAGE}\n")

    def test_only_protected_exact_source_and_full_signer_pin_are_accepted(self):
        self.assertEqual(expected_certificate(SOURCE_ENV), CERTIFICATE)
        for key, value in {
            "GITHUB_REF_PROTECTED": "false",
            "GITHUB_REF": "refs/heads/Main",
            "RUNNER_ENVIRONMENT": "self-hosted",
            "RELEASE_SOURCE_SHA": "c" * 40,
            "GITHUB_WORKFLOW_REF": "Other/Repo/.github/workflows/release.yml@refs/heads/main",
            "BUILDS_WORKFLOW_REF": f"{BUILDS_WORKFLOW}@refs/heads/main",
            "BUILDS_SHA": BUILDS_SHA[:8],
        }.items():
            with self.subTest(key=key), self.assertRaises(ValueError):
                expected_certificate({**SOURCE_ENV, key: value})

    def test_caller_publication_requires_the_same_run_and_frozen_identity(self):
        version = "1.2.3"
        env = {**SOURCE_ENV, "RELEASE_ENVIRONMENT": "production", "GITHUB_RUN_ID": "42",
               "GITHUB_RUN_ATTEMPT": "2", "GITHUB_EVENT_NAME": "workflow_dispatch"}
        run = {"id": "42", "attempt": "2", "event": "workflow_dispatch",
               "workflow_ref": env["GITHUB_WORKFLOW_REF"], "workflow_sha": SOURCE_SHA}
        builds = {"action_sha": BUILDS_SHA, "workflow_sha": BUILDS_SHA}
        identity = {"schema": PREFLIGHT_SCHEMA, "repository": REPOSITORY, "version": version,
                    "source_sha": SOURCE_SHA, "environment": "production", "container_repositories": [IMAGE],
                    "run": run, "builds": builds}
        with tempfile.TemporaryDirectory() as directory, contextlib.chdir(directory):
            path = Path(directory)
            preflight = path / version / "preflight"
            preflight.mkdir(parents=True)
            frozen = preflight / "publication-identity.json"
            receipt = preflight / "publication-preflight.container.json"
            candidates = (
                (identity, "", None),
                ({**identity, "run": {**run, "attempt": "1"}}, "run and attempt", None),
                ({**identity, "source_sha": "c" * 40}, "match the caller release", None),
                ({**identity, "environment": "staging"}, "match the caller release", None),
                ({**identity, "container_repositories": [OTHER_IMAGE]}, "match the caller release", None),
                ({**identity, "builds": {**builds, "action_sha": "c" * 40}}, "different Builds pin", None),
                (identity, "bind the frozen publication identity", "d" * 64),
            )
            for observed, error, sha in candidates:
                with self.subTest(error=error, identity=observed):
                    frozen.write_text(json.dumps(observed))
                    receipt.write_text(json.dumps({"schema": PREFLIGHT_SCHEMA, "result": "pass", "phase": "container",
                                                   "identity": observed,
                                                   "identity_sha256": sha or hashlib.sha256(frozen.read_bytes()).hexdigest()}))
                    if error:
                        with self.assertRaisesRegex(ValueError, error):
                            verify_caller_publication(path, version, {IMAGE: DIGEST}, env)
                    else:
                        verify_caller_publication(path, version, {IMAGE: DIGEST}, env)

    def verify(self, *entries, status=0):
        run = Mock(return_value=subprocess.CompletedProcess([], status, json.dumps(entries).encode(), b""))
        verify_subject(IMAGE, DIGEST, REPOSITORY, CERTIFICATE, Path("bundle.json"), Path("index.raw"), run)
        command = run.call_args.args[0]
        self.assertEqual(command[:4], ["gh", "attestation", "verify", "index.raw"])
        self.assertEqual(command[command.index("--bundle") + 1], "bundle.json")
        self.assertEqual(command[command.index("--repo") + 1], REPOSITORY)

    def test_verifies_digest_and_certificate_in_one_result(self):
        self.verify(verification_result())
        for field in CERTIFICATE:
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "No verified attestation"):
                self.verify(verification_result({**CERTIFICATE, field: "wrong"}))
        with self.assertRaisesRegex(ValueError, "No verified attestation"):
            self.verify(verification_result(name=OTHER_IMAGE))
        with self.assertRaisesRegex(ValueError, "No verified attestation"):
            self.verify(verification_result(digest="a" * 64), verification_result({**CERTIFICATE, "issuer": "wrong"}))

    def test_refuses_failed_or_malformed_verification(self):
        with self.assertRaisesRegex(ValueError, "verification failed"):
            self.verify(verification_result(), status=1)
        for entries in ([], [{}]):
            with self.subTest(entries=entries), self.assertRaisesRegex(ValueError, "Malformed"):
                self.verify(*entries)

    def test_refuses_missing_upload_id_or_bundle_before_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            checksums = path / "checksums.txt"
            checksums.write_text(f"{DIGEST} *{IMAGE}\n{'a' * 64} *{OTHER_IMAGE}\n")
            bundle = path / "bundle.json"
            bundle.write_text("{}")
            fixtures = ((bundle, ""), (path / "missing.json", "5"))
            with patch.dict(os.environ, {**SOURCE_ENV, "CONTAINER_PROJECTS": PROJECTS, "REGISTRY": REGISTRY}), \
                    patch("oci_attestation.verify_subject") as verify:
                for argument, attestation_id in fixtures:
                    err = io.StringIO()
                    argv = ["oci_attestation", "verify", str(checksums), str(path), "1.2.3", str(argument), attestation_id]
                    with patch.object(sys, "argv", argv), \
                            contextlib.redirect_stderr(err):
                        self.assertEqual(main(), 1)
                    self.assertIn("did not return an uploaded OCI attestation", err.getvalue())
                verify.assert_not_called()


if __name__ == "__main__":
    unittest.main()
