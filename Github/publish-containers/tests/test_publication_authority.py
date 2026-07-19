import hashlib
import importlib.util
import json
import os
import sys
import tempfile
import unittest
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
VALIDATOR_PATH = SCRIPT_DIRECTORY / "publication_authority.py"
BUILDS_SHA = "c" * 40
SOURCE_SHA = "d" * 40
sys.path.insert(0, str(SCRIPT_DIRECTORY))


def load_validator():
    spec = importlib.util.spec_from_file_location("publication_authority", VALIDATOR_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load publication authority validator.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class PublicationAuthorityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.validator = load_validator()

    def create_contract(self, root):
        files = {}
        for name in (
            "publish-containers.sh",
            "oci_registry_validator.py",
            "publication_authority.py",
            "smoke-container-platforms.sh",
            "smoke_container_platforms.py",
        ):
            path = root / name
            path.write_text(f"fixture bytes for {name}\n", encoding="utf-8")
            files[name] = path
        checked_at = datetime(2026, 7, 19, 13, 0, tzinfo=timezone.utc)
        authority = {
            "schema": "hexalith.release-publication-authority.v1",
            "decision": "authorized",
            "repository": "Hexalith/Hexalith.EventStore",
            "version": "3.77.0",
            "source_sha": SOURCE_SHA,
            "container_repository": "registry.hexalith.com/eventstore",
            "platforms": ["linux/amd64", "linux/arm64"],
            "builds": {
                "workflow_sha": BUILDS_SHA,
                "action_sha": BUILDS_SHA,
                "files": {name: sha256(path) for name, path in files.items()},
            },
            "owner": {"name": "release-owner", "role": "EventStore release owner"},
            "authorized_at": (checked_at - timedelta(minutes=5)).isoformat().replace("+00:00", "Z"),
            "expires_at": (checked_at + timedelta(minutes=30)).isoformat().replace("+00:00", "Z"),
            "durable_source": "https://github.com/Hexalith/Hexalith.EventStore/issues/120#issuecomment-1",
            "rationale": "Authorize the exact Story 3.12 corrective release identity.",
        }
        expected = {
            "repository": "Hexalith/Hexalith.EventStore",
            "version": "3.77.0",
            "source_sha": SOURCE_SHA,
            "container_repository": "registry.hexalith.com/eventstore",
            "platforms": ["linux/amd64", "linux/arm64"],
            "builds_execution_sha": BUILDS_SHA,
            "durable_source": "https://github.com/Hexalith/Hexalith.EventStore/issues/120#issuecomment-1",
            "owner_name": "release-owner",
            "files": files,
        }
        return authority, expected, checked_at

    def test_cross_origin_redirect_does_not_forward_authority_token(self):
        request = urllib.request.Request(
            "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/120",
            headers={"Authorization": "Bearer fixture-secret"},
        )

        redirected = self.validator.SafeRedirectHandler().redirect_request(
            request,
            None,
            302,
            "Found",
            {},
            "https://example.test/redirected-authority",
        )

        self.assertIsNotNone(redirected)
        self.assertIsNone(redirected.get_header("Authorization"))

    def test_github_authority_adapter_binds_comment_author_and_body(self):
        body = '{"schema":"hexalith.release-publication-authority.v1"}'
        response_document = {
            "body": body,
            "user": {"login": "release-owner"},
            "html_url": "https://github.com/Hexalith/Hexalith.EventStore/issues/120#issuecomment-1",
        }

        class FakeResponse:
            headers = {"ETag": '"fixture-etag"', "Last-Modified": "Sun, 19 Jul 2026 13:00:00 GMT"}

            def __enter__(self):
                return self

            def __exit__(self, exception_type, exception, traceback):
                return False

            def read(self, size):
                return json.dumps(response_document).encode("utf-8")

        api_url = "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/comments/1"
        with mock.patch.object(self.validator.URL_OPENER, "open", return_value=FakeResponse()) as opened:
            raw, metadata = self.validator._load_authority_url(
                api_url,
                "fixture-token",
                {"release-owner"},
            )

        request = opened.call_args.args[0]
        self.assertEqual("Bearer fixture-token", request.get_header("Authorization"))
        self.assertEqual(body.encode("utf-8"), raw)
        self.assertEqual("release-owner", metadata["login"])
        self.assertEqual(response_document["html_url"], metadata["html_url"])

    def test_exact_unexpired_release_owner_authority_passes_and_records_hashes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            authority, expected, checked_at = self.create_contract(root)
            raw = (json.dumps(authority, sort_keys=True) + "\n").encode("utf-8")

            evidence = self.validator.validate_authority_bytes(raw, expected, checked_at)

            self.assertEqual("pass", evidence["result"])
            self.assertEqual(hashlib.sha256(raw).hexdigest(), evidence["authority_sha256"])
            self.assertEqual(checked_at.isoformat().replace("+00:00", "Z"), evidence["checked_at"])
            self.assertEqual(BUILDS_SHA, evidence["builds_execution_sha"])

    def test_mismatch_expiry_wrong_role_and_changed_helper_fail_closed(self):
        mutations = {
            "repository-mismatch": lambda authority, expected, checked: authority.update(repository="Other/Repo"),
            "version-mismatch": lambda authority, expected, checked: authority.update(version="3.77.1"),
            "source-mismatch": lambda authority, expected, checked: authority.update(source_sha="e" * 40),
            "builds-identity-mismatch": lambda authority, expected, checked: authority["builds"].update(action_sha="e" * 40),
            "wrong-owner-role": lambda authority, expected, checked: authority["owner"].update(role="developer"),
            "durable-source-mismatch": lambda authority, expected, checked: authority.update(durable_source="https://example.invalid/authority"),
            "expired-authority": lambda authority, expected, checked: authority.update(expires_at=(checked - timedelta(seconds=1)).isoformat().replace("+00:00", "Z")),
            "approved-file-mismatch": lambda authority, expected, checked: expected["files"]["publish-containers.sh"].write_text("changed\n", encoding="utf-8"),
        }
        for expected_code, mutate in mutations.items():
            with self.subTest(expected_code=expected_code), tempfile.TemporaryDirectory() as temporary_directory:
                authority, expected, checked_at = self.create_contract(Path(temporary_directory))
                mutate(authority, expected, checked_at)
                raw = (json.dumps(authority, sort_keys=True) + "\n").encode("utf-8")
                with self.assertRaises(self.validator.AuthorityError) as context:
                    self.validator.validate_authority_bytes(raw, expected, checked_at)
                self.assertEqual(expected_code, context.exception.code)

    def test_package_and_container_destinations_must_all_be_absent(self):
        packages = [f"Package.{index}" for index in range(14)]
        calls = []

        def absent_probe(kind, identity, version):
            calls.append((kind, identity, version))
            return 404

        evidence = self.validator.validate_destination_absence(
            packages,
            "3.77.0",
            "registry.hexalith.com/eventstore",
            absent_probe,
        )
        self.assertEqual("pass", evidence["result"])
        self.assertEqual(15, len(calls))

        def collision_probe(kind, identity, version):
            return 200 if kind == "nuget" and identity == "Package.7" else 404

        with self.assertRaises(self.validator.AuthorityError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.77.0",
                "registry.hexalith.com/eventstore",
                collision_probe,
            )
        self.assertEqual("version-collision", context.exception.code)

        with self.assertRaises(self.validator.AuthorityError) as context:
            self.validator.validate_container_absence(
                "3.77.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 200,
            )
        self.assertEqual("version-collision", context.exception.code)

    def test_registry_probe_requests_every_recognized_manifest_media_type(self):
        captured = []

        def status(request):
            captured.append(request)
            return 404

        with mock.patch.object(self.validator, "_http_status", side_effect=status):
            probe = self.validator.destination_probe("registry-user", "registry-key")
            self.assertEqual(404, probe("container", "registry.hexalith.com/eventstore", "3.77.0"))

        self.assertEqual(self.validator.MANIFEST_ACCEPT, captured[0].get_header("Accept"))

    def test_frozen_authority_bytes_and_post_probe_expiry_fail_closed(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            authority, expected, checked_at = self.create_contract(root)
            raw = (json.dumps(authority, sort_keys=True) + "\n").encode("utf-8")
            evidence = self.validator.validate_authority_bytes(raw, expected, checked_at)
            metadata = {
                "api_url": "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/comments/1",
                "html_url": authority["durable_source"],
                "login": "release-owner",
                "role": "release_owner",
                "body_sha256": hashlib.sha256(raw).hexdigest(),
                "etag": '"fixture"',
                "last_modified": None,
                "retrieved_at": checked_at.isoformat(),
            }
            destination = {"result": "pass"}
            evidence_root = root / "evidence"
            self.validator._write_evidence(evidence_root, "verify", raw, metadata, evidence, destination)
            self.validator._write_evidence(evidence_root, "publish", raw, metadata, evidence, destination)
            with self.assertRaises(self.validator.AuthorityError) as context:
                self.validator._write_evidence(
                    evidence_root,
                    "container",
                    raw + b" ",
                    metadata,
                    evidence,
                    destination,
                )
            self.assertEqual("authority-bytes-changed", context.exception.code)

            authority["expires_at"] = (checked_at + timedelta(seconds=1)).isoformat().replace("+00:00", "Z")
            expiring_raw = (json.dumps(authority, sort_keys=True) + "\n").encode("utf-8")
            self.validator.validate_authority_bytes(expiring_raw, expected, checked_at)
            expired_checked_at = checked_at + timedelta(seconds=1)
            with self.assertRaises(self.validator.AuthorityError) as context:
                self.validator.validate_authority_bytes(
                    expiring_raw,
                    expected,
                    expired_checked_at,
                )
            self.assertEqual("expired-authority", context.exception.code)

    def test_main_revalidates_expiry_after_all_destination_probes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            contract_directory = root / "contract"
            contract_directory.mkdir()
            authority, expected, checked_at = self.create_contract(contract_directory)
            authority["expires_at"] = (checked_at + timedelta(seconds=1)).isoformat().replace(
                "+00:00",
                "Z",
            )
            raw = (json.dumps(authority, sort_keys=True) + "\n").encode("utf-8")
            metadata = {
                "api_url": "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/comments/1",
                "html_url": authority["durable_source"],
                "login": authority["owner"]["name"],
                "role": "release_owner",
                "body_sha256": hashlib.sha256(raw).hexdigest(),
                "etag": '"fixture"',
                "last_modified": None,
                "retrieved_at": checked_at.isoformat(),
            }
            allowlist = root / "release-owners.json"
            allowlist.write_text(
                json.dumps(
                    {
                        "schema": "hexalith.eventstore.github-approval-role-allowlist/v1",
                        "repository": expected["repository"],
                        "roles": {"release_owner": [authority["owner"]["name"]]},
                    }
                ),
                encoding="utf-8",
            )
            package_manifest = root / "release-packages.json"
            package_manifest.write_text(
                json.dumps({"packages": [{"id": f"Package.{index}"} for index in range(14)]}),
                encoding="utf-8",
            )
            probe_calls = []

            def absent_probe(kind, identity, version):
                probe_calls.append((kind, identity, version))
                return 404

            class SequencedDateTime(datetime):
                values = iter((checked_at, checked_at + timedelta(seconds=1)))

                @classmethod
                def now(cls, tz=None):
                    value = next(cls.values)
                    return value.astimezone(tz) if tz is not None else value.replace(tzinfo=None)

            arguments = [
                "publication_authority.py",
                "--authority-url",
                metadata["api_url"],
                "--repository",
                expected["repository"],
                "--version",
                expected["version"],
                "--source-sha",
                expected["source_sha"],
                "--container-repository",
                expected["container_repository"],
                "--builds-execution-sha",
                expected["builds_execution_sha"],
                "--package-manifest",
                str(package_manifest),
                "--role-allowlist",
                str(allowlist),
                "--contract-directory",
                str(contract_directory),
                "--evidence-directory",
                str(root / "evidence"),
                "--phase",
                "verify",
            ]
            previous_directory = Path.cwd()
            try:
                os.chdir(root)
                with (
                    mock.patch.object(self.validator, "datetime", SequencedDateTime),
                    mock.patch.object(self.validator, "_load_authority_url", return_value=(raw, metadata)),
                    mock.patch.object(self.validator, "destination_probe", return_value=absent_probe),
                    mock.patch.object(sys, "argv", arguments),
                ):
                    result = self.validator.main()
            finally:
                os.chdir(previous_directory)

            self.assertEqual(1, result)
            self.assertEqual(15, len(probe_calls))
            self.assertFalse((root / "evidence" / "publication-preflight.verify.json").exists())

    def test_destination_probe_errors_and_non_exact_inventory_fail_closed(self):
        packages = [f"Package.{index}" for index in range(14)]

        with self.assertRaises(self.validator.AuthorityError) as context:
            self.validator.validate_destination_absence(
                packages[:-1],
                "3.77.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 404,
            )
        self.assertEqual("package-inventory-mismatch", context.exception.code)

        with self.assertRaises(self.validator.AuthorityError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.77.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 503,
            )
        self.assertEqual("destination-probe-failure", context.exception.code)


if __name__ == "__main__":
    unittest.main()
