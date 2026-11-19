"""Contract tests for split-publication-v1.

These tests prove the archive, descriptor, manifest, fallback, publication marker,
and hostile-candidate boundaries without a live GitHub or NuGet credential.
"""

from __future__ import annotations

import io
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import archive_scan
import attestation
import canonical
import descriptor
import graph
import handoff
import manifest
import publication
import split_publication


CANDIDATE = "a" * 40
BASE = "b" * 40
BUILDS = "c" * 40
OTHER = "d" * 40
POLICY_SHA = "e" * 64
BLOB = "f" * 64
BUILDS_SIGNER = "https://github.com/Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml"
FRONTCOMPOSER_SIGNER = "https://github.com/Hexalith/Hexalith.FrontComposer/.github/workflows/release-publish.yml"


def canonical_json(value):
    return canonical.parse_json(canonical.canonical_bytes(value), "test", max_bytes=1024 * 1024)


def evaluator(definition_actions=None):
    document = {
        "caller": {
            "repository": "github.com/hexalith/hexalith.frontcomposer",
            "workflow_path": ".github/workflows/release.yml",
            "blob_sha256": BLOB,
        },
        "reusable": {
            "repository": "github.com/hexalith/hexalith.builds",
            "workflow_path": ".github/workflows/domain-release.yml",
            "commit": BUILDS,
            "blob_sha256": POLICY_SHA,
        },
        "actions": definition_actions or [],
    }
    document["definition_digest"] = canonical.canonical_digest(document)
    return document


def empty_graph():
    material = {
        "schema": graph.GRAPH_SCHEMA,
        "root": {"repository": "github.com/hexalith/hexalith.frontcomposer", "commit": CANDIDATE},
        "edge_count": 0,
        "edges": [],
    }
    return {**material, "graph_digest": canonical.canonical_digest(material)}


def policy_projection():
    return {
        "schema": "hexalith.dependency-graph-policy.v1",
        "repository": "github.com/hexalith/hexalith.frontcomposer",
        "path": "eng/dependency-graph-policy.json",
        "commit": BASE,
        "sha256": POLICY_SHA,
    }


def quality_run():
    return {
        "repository": "github.com/hexalith/hexalith.frontcomposer",
        "workflow_path": ".github/workflows/quality.yml",
        "run_id": 7,
        "run_attempt": 1,
        "event": "push",
        "branch": "main",
        "head_sha": CANDIDATE,
        "conclusion": "success",
    }


def provenance(*, same_repository=False):
    document = {
        "ci": {
            "run": {
                "repository": "github.com/hexalith/hexalith.frontcomposer",
                "workflow_path": ".github/workflows/ci.yml",
                "run_id": 9,
                "run_attempt": 1,
                "event": "push",
                "branch": "main",
                "head_sha": CANDIDATE,
            },
            "evidence_sha256": "1" * 64,
        },
        "release": {
            "caller": {
                "repository": "github.com/hexalith/hexalith.frontcomposer",
                "workflow_path": ".github/workflows/release.yml",
                "commit": CANDIDATE,
                "blob_sha256": BLOB,
            },
            "reusable": {
                "repository": "github.com/hexalith/hexalith.builds",
                "workflow_path": ".github/workflows/domain-release.yml",
                "commit": BUILDS,
                "blob_sha256": POLICY_SHA,
            },
            "builds_execution_sha": BUILDS,
        },
    }
    if same_repository:
        document["release"]["reusable"].update(
            repository="github.com/hexalith/hexalith.frontcomposer",
            workflow_path=".github/workflows/release-publish.yml",
            commit=CANDIDATE,
        )
    document["definition_digest"] = canonical.canonical_digest({"ci": document["ci"], "release": document["release"]})
    return document


def attestation_expectations():
    return {
        "packages": [("Demo.1.2.3.nupkg", "4" * 64)],
        "repository_slug": "Hexalith/Hexalith.FrontComposer",
        "caller_workflow": ".github/workflows/release.yml",
        "repository_id": 1,
        "owner_id": 2,
        "candidate": CANDIDATE,
        "builds_sha": BUILDS,
        "run_id": 40,
        "run_attempt": 1,
    }


def attestation_statement(builder_id):
    return {
        "_type": attestation.STATEMENT_TYPE,
        "subject": [{"name": "Demo.1.2.3.nupkg", "digest": {"sha256": "4" * 64}}],
        "predicateType": attestation.PREDICATE_TYPE,
        "predicate": {
            "buildDefinition": {
                "buildType": "https://actions.github.io/buildtypes/workflow/v1",
                "externalParameters": {
                    "workflow": {
                        "repository": "https://github.com/Hexalith/Hexalith.FrontComposer",
                        "ref": "refs/heads/main",
                        "path": ".github/workflows/release.yml",
                    }
                },
                "internalParameters": {
                    "github": {
                        "event_name": "workflow_dispatch",
                        "repository_id": 1,
                        "repository_owner_id": 2,
                        "runner_environment": "github-hosted",
                    }
                },
                "resolvedDependencies": [
                    {
                        "uri": "git+https://github.com/Hexalith/Hexalith.FrontComposer@refs/heads/main",
                        "digest": {"gitCommit": CANDIDATE},
                    }
                ],
            },
            "runDetails": {
                "builder": {"id": builder_id},
                "metadata": {"invocationId": "https://github.com/Hexalith/Hexalith.FrontComposer/actions/runs/40/attempts/1"},
            },
        },
    }


def certificate_extensions(signer_sha):
    return {
        "1.3.6.1.4.1.57264.1.8": "https://token.actions.githubusercontent.com",
        "1.3.6.1.4.1.57264.1.10": signer_sha,
        "1.3.6.1.4.1.57264.1.11": "github-hosted",
        "1.3.6.1.4.1.57264.1.12": "https://github.com/Hexalith/Hexalith.FrontComposer",
        "1.3.6.1.4.1.57264.1.13": CANDIDATE,
        "1.3.6.1.4.1.57264.1.14": "refs/heads/main",
        "1.3.6.1.4.1.57264.1.15": "1",
        "1.3.6.1.4.1.57264.1.17": "2",
        "1.3.6.1.4.1.57264.1.21": "https://github.com/Hexalith/Hexalith.FrontComposer/actions/runs/40/attempts/1",
    }


def zip_bytes(members, *, mode=None):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, payload in members:
            info = zipfile.ZipInfo(name)
            info.compress_type = zipfile.ZIP_DEFLATED
            if mode is not None:
                info.external_attr = mode << 16
            archive.writestr(info, payload)
    return buffer.getvalue()


class CanonicalTests(unittest.TestCase):
    def test_python_canonical_form_covers_the_required_escape_classes(self):
        value = {
            "quote": "a\"b",
            "slash": "a\\b",
            "controls": "\b\t\n\f\r",
            "del": "\u007f",
            "bmp": "é",
            "supplementary": "😀",
            "solidus": "a/b",
        }
        encoded = canonical.canonical_bytes(value)
        self.assertIn(b'\\"', encoded)
        self.assertIn(b"\\\\", encoded)
        self.assertIn(b"\\b\\t\\n\\f\\r", encoded)
        self.assertIn(b"\\u007f", encoded)
        self.assertIn(b"\\u00e9", encoded)
        self.assertIn(b"\\ud83d\\ude00", encoded)
        self.assertIn(b"a/b", encoded)
        self.assertFalse(encoded.endswith(b"\n"))

    def test_duplicate_members_and_non_integers_fail_closed(self):
        with self.assertRaises(canonical.ContractError):
            canonical.parse_json(b'{"a":1,"a":2}', "duplicate", max_bytes=100)
        with self.assertRaises(canonical.ContractError):
            canonical.expect_safe_integer(True, "flag")
        with self.assertRaises(canonical.ContractError):
            canonical.expect_version("01.0.0", "version")
        with self.assertRaises(canonical.ContractError):
            canonical.expect_version("1.0.0+build", "version")
        self.assertEqual("1.2.3-rc.1", canonical.expect_version("1.2.3-rc.1", "version"))


class ArchiveTests(unittest.TestCase):
    def test_regular_members_round_trip_as_data(self):
        payload = zip_bytes([("packages/demo.1.2.3.nupkg", b"package-bytes"), ("publication-candidate.json", b"{}")])
        members = archive_scan.scan_archive(payload)
        self.assertEqual([b"package-bytes", b"{}"], [member.payload for member in members])

    def test_hostile_members_are_rejected_before_use(self):
        attacks = [
            zip_bytes([("../escape.txt", b"x")]),
            zip_bytes([("link.sh", b"#!/bin/sh\n")], mode=0o120777),
            zip_bytes([("notes/../../outside.txt", b"x")]),
            zip_bytes([(f"{'/'.join(f's{index}' for index in range(9))}/file.txt", b"x")]),
        ]
        for payload in attacks:
            with self.subTest(payload=payload[:80]):
                with self.assertRaises(canonical.ContractError):
                    archive_scan.scan_archive(payload)

    def test_compression_ratio_and_duplicate_names_fail_closed(self):
        with self.assertRaises(canonical.ContractError):
            archive_scan.scan_archive(zip_bytes([("bomb.txt", b"\0" * 2_000_000)]))
        duplicate = io.BytesIO()
        with zipfile.ZipFile(duplicate, "w") as archive:
            archive.writestr("same.txt", b"one")
            archive.writestr("same.txt", b"two")
        with self.assertRaises(canonical.ContractError):
            archive_scan.scan_archive(duplicate.getvalue())

    def test_materialized_script_is_not_executed(self):
        script = b"#!/bin/sh\ntouch executed-by-candidate\n"
        payload = zip_bytes([("hostile.sh", script), ("publication-candidate.json", b"{}")])
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "archive"
            previous = os.getcwd()
            os.chdir(temporary)
            try:
                members = archive_scan.materialize_archive(payload, destination)
                self.assertFalse((Path(temporary) / "executed-by-candidate").exists())
                self.assertEqual(script, next(member.payload for member in members if member.name == "hostile.sh"))
                self.assertTrue((destination / "hostile.sh").is_file())
                self.assertFalse((destination / "hostile.sh").stat().st_mode & stat.S_IXUSR)
            finally:
                os.chdir(previous)


    def test_archive_requires_no_execution_marker(self):
        release_evaluator = evaluator()
        release_evaluator["caller"]["commit"] = CANDIDATE
        release_evaluator["reusable"].update(
            repository="github.com/hexalith/hexalith.frontcomposer",
            workflow_path=".github/workflows/release-publish.yml",
            commit=CANDIDATE,
        )
        release_evaluator["definition_digest"] = canonical.canonical_digest({
            key: release_evaluator[key] for key in ("caller", "reusable", "actions")
        })
        document = {
            "schema": descriptor.DESCRIPTOR_SCHEMA,
            "release_run": {
                "repository": "github.com/hexalith/hexalith.frontcomposer",
                "workflow_path": ".github/workflows/release.yml",
                "run_id": 10,
                "run_attempt": 1,
            },
            "candidate": CANDIDATE,
            "ci_handoff_sha256": BLOB,
            "dependency_policy": policy_projection(),
            "evaluator": release_evaluator,
            "release_plan": None,
            "files": [],
        }
        payload = zip_bytes([("publication-candidate.json", canonical.canonical_bytes(document))])
        with mock.patch.dict(os.environ, clear=True):
            self.assertIn("publication-candidate.json", split_publication.authenticate_directory(payload))
        with tempfile.TemporaryDirectory() as temporary:
            marker = Path(temporary) / "marker"
            marker.touch()
            with mock.patch.dict(os.environ, {"SPLIT_EXECUTION_MARKER": str(marker)}):
                with self.assertRaisesRegex(canonical.ContractError, "before archive authentication"):
                    split_publication.authenticate_directory(payload)
        wrong_source = canonical_json(document)
        wrong_evaluator = wrong_source["evaluator"]
        wrong_evaluator["reusable"]["commit"] = OTHER
        wrong_evaluator["definition_digest"] = canonical.canonical_digest({
            key: wrong_evaluator[key] for key in ("caller", "reusable", "actions")
        })
        with self.assertRaisesRegex(canonical.ContractError, "publisher to the candidate"):
            descriptor.validate_descriptor(wrong_source)


class CiHandoffTests(unittest.TestCase):
    def test_handoff_requires_the_authenticated_ci_evaluator(self):
        ci_evaluator = evaluator()
        ci_evaluator["caller"].update(workflow_path=".github/workflows/ci.yml", commit=CANDIDATE)
        ci_evaluator["reusable"]["workflow_path"] = ".github/workflows/domain-ci.yml"
        run = {
            "repository": "github.com/hexalith/hexalith.frontcomposer",
            "workflow_path": ".github/workflows/ci.yml",
            "run_id": 9,
            "run_attempt": 1,
            "event": "push",
            "branch": "main",
            "candidate": CANDIDATE,
        }
        document = {
            "schema": handoff.CI_HANDOFF_SCHEMA,
            "run": run,
            "revisions": {"base": BASE, "candidate": CANDIDATE, "merge_base": None},
            "evaluator": ci_evaluator,
            "dependency_policy": policy_projection(),
            "dependency_graph": empty_graph(),
        }
        cases = [
            (".github/workflows/domain-ci.yml", CANDIDATE, None),
            (".github/workflows/domain-release.yml", CANDIDATE, "select the Builds CI workflow"),
            (".github/workflows/domain-ci.yml", OTHER, "caller commit does not match"),
        ]
        for workflow, revision, error in cases:
            with self.subTest(workflow=workflow, revision=revision):
                ci_evaluator["caller"]["commit"] = revision
                ci_evaluator["reusable"]["workflow_path"] = workflow
                ci_evaluator["definition_digest"] = canonical.canonical_digest({
                    key: ci_evaluator[key] for key in ("caller", "reusable", "actions")
                })
                raw = canonical.canonical_bytes(document)
                if error:
                    with self.assertRaisesRegex(canonical.ContractError, error):
                        handoff.validate_ci_handoff(document, raw)
                else:
                    verified = handoff.validate_ci_handoff(document, raw)
                    self.assertEqual(ci_evaluator, verified["evaluator"])
                    self.assertEqual(canonical.sha256_bytes(raw), verified["evidence_sha256"])


class ManifestAndFallbackTests(unittest.TestCase):
    def test_manifest_v4_seals_only_its_closed_body(self):
        for same_repository in (False, True):
            with self.subTest(same_repository=same_repository):
                workflow_provenance = provenance(same_repository=same_repository)
                sealed = manifest.seal_manifest(
                    dependency_graph=empty_graph(),
                    dependency_policy=policy_projection(),
                    quality_run=quality_run(),
                    attestation={
                        "status": "attested",
                        "bundle": {"path": "provenance-attestation.json", "sha256": "2" * 64},
                        "fallback": None,
                    },
                    workflow_provenance=workflow_provenance,
                    candidate=CANDIDATE,
                    sealed_at="2026-10-30T18:00:00Z",
                )
                self.assertEqual(manifest.MANIFEST_SCHEMA, sealed["schema"])
                self.assertEqual(workflow_provenance, sealed["workflow_provenance"])
                self.assertEqual(sealed, manifest.verify_manifest(sealed, CANDIDATE))
                tampered = json.loads(canonical.canonical_bytes(sealed))
                tampered["quality_run"]["run_id"] = 8
                with self.assertRaises(canonical.ContractError):
                    manifest.verify_manifest(tampered, CANDIDATE)

                if same_repository:
                    tampered = json.loads(canonical.canonical_bytes(sealed))
                    tampered_provenance = tampered["workflow_provenance"]
                    tampered_provenance["release"]["builds_execution_sha"] = OTHER
                    with self.assertRaisesRegex(canonical.ContractError, "definition_digest"):
                        manifest.verify_manifest(tampered, CANDIDATE)
                    tampered_provenance["definition_digest"] = canonical.canonical_digest(
                        {"ci": tampered_provenance["ci"], "release": tampered_provenance["release"]}
                    )
                    with self.assertRaisesRegex(canonical.ContractError, "manifest seal"):
                        manifest.verify_manifest(tampered, CANDIDATE)

    def test_same_repository_provenance_rejects_mismatched_coordinates(self):
        mismatches = [
            ("ci", "run", "repository", "github.com/hexalith/hexalith.tenants"),
            ("ci", "run", "head_sha", OTHER),
            ("release", "caller", "repository", "github.com/hexalith/hexalith.tenants"),
            ("release", "caller", "workflow_path", ".github/workflows/another-release.yml"),
            ("release", "caller", "commit", OTHER),
            ("release", "reusable", "repository", "github.com/hexalith/hexalith.tenants"),
            ("release", "reusable", "workflow_path", ".github/workflows/another-publisher.yml"),
            ("release", "reusable", "commit", BUILDS),
        ]
        for section, coordinate, field, replacement in mismatches:
            with self.subTest(coordinate=coordinate, field=field):
                document = provenance(same_repository=True)
                document[section][coordinate][field] = replacement
                document["definition_digest"] = canonical.canonical_digest({"ci": document["ci"], "release": document["release"]})
                with self.assertRaises(canonical.ContractError):
                    manifest.validate_workflow_provenance(document, CANDIDATE)

    def test_same_repository_provenance_still_requires_an_exact_builds_execution_sha(self):
        document = provenance(same_repository=True)
        document["release"]["builds_execution_sha"] = "refs/heads/main"
        document["definition_digest"] = canonical.canonical_digest({"ci": document["ci"], "release": document["release"]})
        with self.assertRaisesRegex(canonical.ContractError, "builds execution sha"):
            manifest.validate_workflow_provenance(document, CANDIDATE)

    def test_legacy_provenance_requires_the_exact_builds_execution_sha_as_the_reusable_commit(self):
        document = provenance()
        document["release"]["reusable"]["commit"] = CANDIDATE
        document["definition_digest"] = canonical.canonical_digest({"ci": document["ci"], "release": document["release"]})
        with self.assertRaisesRegex(canonical.ContractError, "selected Builds execution SHA"):
            manifest.validate_workflow_provenance(document, CANDIDATE)

    def test_attempt_one_approval_does_not_authorize_attempt_two(self):
        request = {
            "schema": attestation.FALLBACK_REQUEST_SCHEMA,
            "candidate": CANDIDATE,
            "ci_run": {
                "repository": "github.com/hexalith/hexalith.frontcomposer",
                "workflow_path": ".github/workflows/ci.yml",
                "run_id": 9,
                "run_attempt": 1,
            },
            "reason": "github-attestations-unavailable",
            "requested_by": "release-owner",
            "created_at": "2026-10-30T18:00:00Z",
            "expires_at": "2026-10-30T20:00:00Z",
            "fingerprints_sha256": "3" * 64,
            "authority": {"policy_commit": BASE, "policy_sha256": POLICY_SHA},
        }
        expected = {
            "candidate": CANDIDATE,
            "ci_run": request["ci_run"],
            "actor": "release-owner",
            "authority": request["authority"],
            "fingerprints_sha256": request["fingerprints_sha256"],
        }
        validated = attestation.validate_fallback_request(request, expected)
        request_sha = canonical.sha256_bytes(canonical.canonical_bytes(validated))
        approval = {
            "state": "approved",
            "comment": f"hexalith-attestation-fallback-v1:{request_sha}:40:1",
            "environments": [{"id": 5, "name": "production", "can_admins_bypass": False}],
            "user": {"login": "release-owner", "id": 11},
        }
        common = {
            "attestation_capability": "unsupported",
            "repository": "github.com/hexalith/hexalith.frontcomposer",
            "caller_workflow": ".github/workflows/release.yml",
            "environment_id": 5,
            "release_owner_logins": ["release-owner"],
            "observed_at": "2026-10-30T19:00:00Z",
        }
        authorized = attestation.authorize_fallback(validated, [approval], {**common, "run_id": 40, "run_attempt": 1})
        self.assertEqual(attestation.FALLBACK_AUTHORIZATION_SCHEMA, authorized["schema"])
        with self.assertRaises(canonical.ContractError):
            attestation.authorize_fallback(validated, [approval], {**common, "run_id": 40, "run_attempt": 2})
        with self.assertRaises(canonical.ContractError):
            attestation.authorize_fallback(
                validated,
                [approval],
                {**common, "run_id": 40, "run_attempt": 1, "attestation_capability": "supported"},
            )


class AttestationTests(unittest.TestCase):
    def test_legacy_signer_matches_the_exact_builds_revision(self):
        expected = attestation_expectations()
        statement = attestation_statement(f"{BUILDS_SIGNER}@{BUILDS}")
        extensions = certificate_extensions(BUILDS)
        attestation.validate_statement(statement, expected)
        attestation.certificate_identity(extensions, expected)
        attestation.validate_statement(statement, expected, workflow_provenance=provenance())
        for ref in (CANDIDATE, "refs/heads/main"):
            with self.subTest(ref=ref):
                with self.assertRaisesRegex(canonical.ContractError, "builder id"):
                    attestation.validate_statement(attestation_statement(f"{BUILDS_SIGNER}@{ref}"), expected)
        with self.assertRaisesRegex(canonical.ContractError, r"\.1\.10"):
            attestation.certificate_identity(certificate_extensions(CANDIDATE), expected)

    def test_provenance_selects_the_frontcomposer_signer_at_the_candidate(self):
        expected = attestation_expectations()
        signer_id = f"{FRONTCOMPOSER_SIGNER}@{CANDIDATE}"
        extensions = {**certificate_extensions(CANDIDATE), "1.3.6.1.4.1.57264.1.9": signer_id}
        workflow_provenance = manifest.validate_workflow_provenance(provenance(same_repository=True), CANDIDATE)
        statement = attestation_statement(signer_id)
        attestation.validate_statement(
            statement, expected, workflow_provenance=workflow_provenance, verified_certificate_extensions=extensions
        )
        with self.assertRaisesRegex(canonical.ContractError, "cryptographically verified signer certificate"):
            attestation.validate_statement(statement, expected, workflow_provenance=workflow_provenance)
        attestation.certificate_identity(extensions, expected, workflow_provenance=workflow_provenance)
        with self.assertRaisesRegex(canonical.ContractError, "authenticated release"):
            attestation.validate_statement(
                attestation_statement(signer_id), {**expected, "builds_sha": OTHER}, workflow_provenance=workflow_provenance
            )
        with self.assertRaisesRegex(canonical.ContractError, "builder id"):
            attestation.validate_statement(attestation_statement(signer_id), expected)

    def test_same_repository_builder_rejects_a_different_signer_or_ref(self):
        builder_ids = (
            f"{BUILDS_SIGNER}@{BUILDS}",
            f"{FRONTCOMPOSER_SIGNER}@{BUILDS}",
            f"{FRONTCOMPOSER_SIGNER}@refs/heads/feature",
            f"{FRONTCOMPOSER_SIGNER}@refs/tags/main",
            f"{FRONTCOMPOSER_SIGNER.replace('release-publish.yml', 'release.yml')}@{CANDIDATE}",
            f"{FRONTCOMPOSER_SIGNER.replace('Hexalith.FrontComposer', 'Hexalith.Tenants')}@{CANDIDATE}",
        )
        for builder_id in builder_ids:
            with self.subTest(builder_id=builder_id):
                with self.assertRaisesRegex(canonical.ContractError, "builder id"):
                    attestation.validate_statement(
                        attestation_statement(builder_id),
                        attestation_expectations(),
                        workflow_provenance=provenance(same_repository=True),
                    )

    def test_same_repository_builder_rejects_a_different_source_commit(self):
        statement = attestation_statement(f"{FRONTCOMPOSER_SIGNER}@{CANDIDATE}")
        statement["predicate"]["buildDefinition"]["resolvedDependencies"][0]["digest"]["gitCommit"] = OTHER
        with self.assertRaisesRegex(canonical.ContractError, "resolved dependency"):
            attestation.validate_statement(
                statement, attestation_expectations(), workflow_provenance=provenance(same_repository=True)
            )

    def test_same_repository_main_ref_requires_verified_certificate_identity(self):
        main_id = f"{FRONTCOMPOSER_SIGNER}@refs/heads/main"
        statement = attestation_statement(main_id)
        expected = attestation_expectations()
        workflow_provenance = provenance(same_repository=True)
        with self.assertRaisesRegex(canonical.ContractError, "cryptographically verified signer certificate"):
            attestation.validate_statement(statement, expected, workflow_provenance=workflow_provenance)

        extensions = {**certificate_extensions(CANDIDATE), "1.3.6.1.4.1.57264.1.9": main_id}
        attestation.validate_statement(
            statement, expected, workflow_provenance=workflow_provenance, verified_certificate_extensions=extensions
        )
        mismatches = {
            "1.3.6.1.4.1.57264.1.8": "https://different-issuer.example",
            "1.3.6.1.4.1.57264.1.9": f"{BUILDS_SIGNER}@refs/heads/main",
            "1.3.6.1.4.1.57264.1.10": BUILDS,
            "1.3.6.1.4.1.57264.1.12": "https://github.com/Hexalith/Hexalith.Tenants",
            "1.3.6.1.4.1.57264.1.13": OTHER,
            "1.3.6.1.4.1.57264.1.14": "refs/heads/feature",
        }
        for oid, replacement in mismatches.items():
            with self.subTest(oid=oid):
                with self.assertRaisesRegex(canonical.ContractError, "attestation certificate"):
                    attestation.validate_statement(
                        statement,
                        expected,
                        workflow_provenance=workflow_provenance,
                        verified_certificate_extensions={**extensions, oid: replacement},
                    )

    def test_statement_rejects_an_extra_subject(self):
        statement = attestation_statement(f"{BUILDS_SIGNER}@{BUILDS}")
        statement["subject"].append({"name": "extra.nupkg", "digest": {"sha256": "5" * 64}})
        with self.assertRaisesRegex(canonical.ContractError, "authenticated package set"):
            attestation.validate_statement(statement, attestation_expectations())


class PublicationBoundaryTests(unittest.TestCase):
    def test_builder_authorization_claim_cannot_publish(self):
        result = publication.classify(
            {
                "publish_gate": "true",
                "release_plan": {"version": "1.2.3"},
                "attestation_verified": True,
                "manifest_verified": True,
                "live_verified": True,
                "bindings_verified": True,
                "archive_verified": True,
                "builder_authorizes": True,
            }
        )
        self.assertFalse(result["publish_authorized"])
        self.assertEqual("classification-blocked", result["prepublication_denial"])

    def test_non_true_gate_never_authorizes(self):
        for value in (None, "", "TRUE", " true", "false"):
            result = publication.classify({"publish_gate": value, "release_plan": {"version": "1.2.3"}})
            self.assertFalse(result["publish_authorized"])

    def test_marker_precedes_the_first_mutation_and_exact_bytes_are_required(self):
        package = zip_bytes([("lib/demo.dll", b"exact")])
        signed = zip_bytes([("lib/demo.dll", b"exact"), (".signature.p7s", b"repository-signature")])
        calls = []
        started = {"value": False}

        class FakeTransport(publication.Transport):
            def request(self, method, url, headers, body):
                calls.append((method, url, started["value"], "X-NuGet-ApiKey" in headers))
                if method in {"POST", "PUT", "PATCH"} and not started["value"]:
                    raise AssertionError("mutation preceded the publication marker")
                if url.endswith("/index.json"):
                    body = json.dumps(
                        {
                            "resources": [
                                {"@type": "PackagePublish/2.0.0", "@id": "https://www.nuget.org/api/v2/package"},
                                {"@type": "PackageBaseAddress/3.0.0", "@id": "https://api.nuget.org/v3-flatcontainer/"},
                            ]
                        }
                    ).encode()
                    return publication.Response(200, {}, body)
                if method == "GET" and started["value"] and url.endswith("/git/ref/tags/v1.2.3"):
                    return publication.Response(200, {}, json.dumps({"object": {"sha": CANDIDATE}}).encode())
                if method == "GET" and ("/git/ref/tags/" in url or "/releases/tags/" in url or "v3-flatcontainer" in url):
                    if started["value"] and url.endswith(".nupkg"):
                        return publication.Response(200, {}, signed)
                    return publication.Response(404, {}, b"")
                if method == "POST" and url.endswith("/releases"):
                    return publication.Response(201, {}, json.dumps({"id": 70, "upload_url": "https://uploads.github.com/repos/Hexalith/Hexalith.FrontComposer/releases/70/assets{?name,label}"}).encode())
                if method == "POST" and "uploads.github.com" in url:
                    return publication.Response(201, {}, b"{}")
                if method == "PUT":
                    if headers.get("X-NuGet-ApiKey") != "secret-nuget-key":
                        raise AssertionError("NuGet publication did not receive the environment credential")
                    return publication.Response(201, {}, b"")
                if method == "PATCH":
                    return publication.Response(200, {}, b"{}")
                if method == "GET" and url.endswith("/git/ref/tags/v1.2.3"):
                    return publication.Response(200, {}, json.dumps({"object": {"sha": CANDIDATE}}).encode())
                raise AssertionError((method, url))

        def mark():
            started["value"] = True

        result = publication.execute_publication(
            transport=FakeTransport(),
            token="github-token",
            nuget_api_key="secret-nuget-key",
            repository_slug="Hexalith/Hexalith.FrontComposer",
            candidate=CANDIDATE,
            plan={"version": "1.2.3", "tag": "v1.2.3"},
            notes="Release 1.2.3\n",
            packages=[("Demo", "Demo.1.2.3.nupkg", package)],
            assets=[("Demo.1.2.3.nupkg", package)],
            mark_started=mark,
        )
        self.assertEqual(70, result["github_release_id"])
        self.assertTrue(started["value"])
        self.assertNotIn("secret-nuget-key", json.dumps(calls))

    def test_duplicate_destination_names_are_rejected(self):
        with self.assertRaises(canonical.ContractError):
            handoff.durable_asset_names(4, 1, ["publication-candidate.json"], "provenance-attestation.json")

    def test_artifact_redirect_drops_credentials(self):
        location, headers = publication.redirected_artifact_request("https://objects.githubusercontent.com/archive.zip")
        self.assertEqual({}, headers)
        self.assertTrue(location.startswith("https://"))
        with self.assertRaises(canonical.ContractError):
            publication.redirected_artifact_request("https://user:pass@example.com/archive.zip")
        with self.assertRaises(canonical.ContractError):
            publication.redirected_artifact_request("http://objects.githubusercontent.com/archive.zip")


class GraphTests(unittest.TestCase):
    def test_live_reconstruction_matches_the_sealed_graph_and_catalog_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            builds = self.commit_repository(root / "builds", {"Props/Directory.Packages.props": b"catalog-v1\n"})
            child = self.commit_repository(root / "child", {"README.md": b"child\n"})
            modules = "\n".join(
                [
                    '[submodule "references/Hexalith.Builds"]',
                    "path = references/Hexalith.Builds",
                    "url = https://github.com/Hexalith/Hexalith.Builds.git",
                    '[submodule "references/Hexalith.Commons"]',
                    "path = references/Hexalith.Commons",
                    "url = git@github.com:Hexalith/Hexalith.Commons.git",
                ]
            ).encode()
            front = self.commit_repository(
                root / "front",
                {".gitmodules": modules, "README.md": b"front\n"},
                gitlinks={
                    "references/Hexalith.Builds": builds,
                    "references/Hexalith.Commons": child,
                },
            )
            reader = graph.GitDirectoryReader(
                {
                    "github.com/hexalith/hexalith.frontcomposer": root / "front",
                    "github.com/hexalith/hexalith.builds": root / "builds",
                    "github.com/hexalith/hexalith.commons": root / "child",
                }
            )
            trusted = set(reader.repositories)
            reconstructed = graph.reconstruct_graph(reader, "github.com/hexalith/hexalith.frontcomposer", front, trusted)
            self.assertEqual(reconstructed, graph.validate_graph(reconstructed))
            builds_edge = next(edge for edge in reconstructed["edges"] if edge["repository"].endswith("hexalith.builds"))
            self.assertEqual(canonical.sha256_bytes(b"catalog-v1\n"), builds_edge["catalog_sha256"])
            self.assertIsNone(builds_edge["catalog_contract_version"])
            tampered = json.loads(canonical.canonical_bytes(reconstructed))
            tampered["edges"][0]["commit"] = OTHER
            tampered["graph_digest"] = canonical.canonical_digest({key: tampered[key] for key in ("schema", "root", "edge_count", "edges")})
            self.assertNotEqual(reconstructed, graph.validate_graph(tampered))

    def commit_repository(self, directory: Path, files: dict[str, bytes], gitlinks: dict[str, str] | None = None) -> str:
        directory.mkdir()
        subprocess.run(["git", "init", str(directory)], check=True, capture_output=True)
        for name, payload in files.items():
            path = directory / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
            subprocess.run(["git", "-C", str(directory), "add", name], check=True, capture_output=True)
        if gitlinks:
            for path, commit in gitlinks.items():
                subprocess.run(
                    ["git", "-C", str(directory), "update-index", "--add", "--cacheinfo", f"160000,{commit},{path}"],
                    check=True,
                    capture_output=True,
                )
        subprocess.run(
            ["git", "-C", str(directory), "-c", "user.email=test@example.com", "-c", "user.name=test", "commit", "-m", "fixture"],
            check=True,
            capture_output=True,
        )
        return subprocess.run(["git", "-C", str(directory), "rev-parse", "HEAD"], check=True, capture_output=True, text=True).stdout.strip()


class WorkspaceInputTests(unittest.TestCase):
    def test_relative_and_absolute_inputs_stay_inside_workspace(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory).resolve()
            for value in ("nested/output.json", str(workspace / "nested/output.json")):
                with self.subTest(value=value), mock.patch.object(split_publication.Path, "cwd", return_value=workspace), \
                        mock.patch.dict(os.environ, {"SPLIT_OUTPUT": value}):
                    self.assertEqual(workspace / "nested/output.json", split_publication._workspace_path("SPLIT_OUTPUT"))

    def test_traversal_and_symlinks_cannot_select_external_files(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory).resolve()
            workspace = base / "workspace"
            workspace.mkdir()
            protected = base / "protected.json"
            protected.write_text("private input", encoding="utf-8")
            link = workspace / "linked.json"
            link.symlink_to(protected)
            for value in ("../protected.json", str(protected), str(link)):
                with self.subTest(value=value), mock.patch.object(split_publication.Path, "cwd", return_value=workspace), \
                        mock.patch.dict(os.environ, {"SPLIT_METADATA": value}), \
                        mock.patch.object(split_publication, "parse_json") as parse, \
                        mock.patch.object(sys, "stderr", io.StringIO()):
                    self.assertEqual(1, split_publication.main(["prepare-candidate"]))
                    parse.assert_not_called()
                    self.assertEqual("private input", protected.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
