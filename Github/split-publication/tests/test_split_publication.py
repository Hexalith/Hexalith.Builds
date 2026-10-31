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


def provenance():
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
    document["definition_digest"] = canonical.canonical_digest({"ci": document["ci"], "release": document["release"]})
    return document


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


class ManifestAndFallbackTests(unittest.TestCase):
    def test_manifest_v4_seals_only_its_closed_body(self):
        sealed = manifest.seal_manifest(
            dependency_graph=empty_graph(),
            dependency_policy=policy_projection(),
            quality_run=quality_run(),
            attestation={"status": "attested", "bundle": {"path": "provenance-attestation.json", "sha256": "2" * 64}, "fallback": None},
            workflow_provenance=provenance(),
            candidate=CANDIDATE,
            sealed_at="2026-10-30T18:00:00Z",
        )
        self.assertEqual(manifest.MANIFEST_SCHEMA, sealed["schema"])
        verified = manifest.verify_manifest(sealed, CANDIDATE)
        self.assertEqual(sealed["seal"]["hash"], verified["seal"]["hash"])
        tampered = json.loads(canonical.canonical_bytes(sealed))
        tampered["quality_run"]["run_id"] = 8
        with self.assertRaises(canonical.ContractError):
            manifest.verify_manifest(tampered, CANDIDATE)

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

    def test_statement_rejects_an_extra_subject(self):
        statement = {
            "_type": attestation.STATEMENT_TYPE,
            "subject": [
                {"name": "Demo.1.2.3.nupkg", "digest": {"sha256": "4" * 64}},
                {"name": "extra.nupkg", "digest": {"sha256": "5" * 64}},
            ],
            "predicateType": attestation.PREDICATE_TYPE,
            "predicate": {},
        }
        with self.assertRaises(canonical.ContractError):
            attestation.validate_statement(
                statement,
                {
                    "packages": [("Demo.1.2.3.nupkg", "4" * 64)],
                    "repository_slug": "Hexalith/Hexalith.FrontComposer",
                    "caller_workflow": ".github/workflows/release.yml",
                    "repository_id": 1,
                    "owner_id": 2,
                    "candidate": CANDIDATE,
                    "builds_sha": BUILDS,
                    "run_id": 40,
                    "run_attempt": 1,
                },
            )


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


class HostileWorkflowTests(unittest.TestCase):
    def test_split_jobs_keep_candidate_code_away_from_publication_authority(self):
        workflow = Path(__file__).resolve().parents[3] / ".github" / "workflows" / "domain-release.yml"
        text = workflow.read_text(encoding="utf-8")
        self.assertIn("release-mode:\n", text)
        self.assertIn("default: ''", text)
        builder = job(text, "build-publication-candidate")
        publisher = job(text, "publish-publication-candidate")
        legacy = job(text, "release")
        governed = job(text, "governed-release")
        self.assertIn("inputs.release-mode == '' && !inputs.governed-release", legacy)
        self.assertIn("inputs.release-mode == '' && inputs.governed-release", governed)
        self.assertNotIn("environment:", builder)
        self.assertNotIn("secrets.", builder)
        self.assertIn("actions: read", builder)
        self.assertIn("contents: read", builder)
        self.assertNotIn("id-token:", builder)
        self.assertNotIn("attestations:", builder)
        self.assertNotIn("contents: write", builder)
        self.assertIn("submodules: false", builder)
        self.assertIn("persist-credentials: false", builder)
        self.assertIn('GITHUB_TOKEN: ""', builder)
        self.assertIn("environment: ${{ inputs.environment-name }}", publisher)
        self.assertNotIn("secrets: inherit", publisher)
        self.assertNotIn("candidate-command", publisher)
        self.assertNotIn("semantic-release", publisher)
        self.assertNotIn("dotnet ", publisher)
        self.assertNotIn("npm ", publisher)
        self.assertIn("actions/attest@f7c74d28b9d84cb8768d0b8ca14a4bac6ef463e6", publisher)
        self.assertIn("repository: Hexalith/Hexalith.Builds", publisher)
        self.assertNotIn("submodules: true", builder)
        self.assertNotIn("--recursive", (ROOT.parent / "initialize-build" / "action.yml").read_text(encoding="utf-8"))
        for line in publisher.splitlines():
            if "uses:" in line and "actions/checkout@" in line:
                self.assertIn("Hexalith/Hexalith.Builds", publisher)
        uses = [line.strip() for line in publisher.splitlines() if line.strip().startswith("- uses:") or line.strip().startswith("uses:")]
        for value in uses:
            reference = value.split(":", 1)[1].strip()
            self.assertTrue(
                reference.startswith("./.hexalith/builds-execution/") or "@" in reference and reference.rsplit("@", 1)[1].split()[0].islower(),
                reference,
            )
            self.assertNotIn("@main", reference)
            self.assertNotRegex(reference, r"@v[0-9]")


def job(workflow: str, name: str) -> str:
    start = workflow.index(f"\n  {name}:\n")
    remainder = workflow[start + 1:]
    import re

    match = re.search(r"\n  [a-z][a-z0-9-]*:\n", remainder)
    return remainder if match is None else remainder[: match.start()]


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
