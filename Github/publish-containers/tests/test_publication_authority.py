import hashlib
import importlib.util
import json
import tempfile
import unittest
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
VALIDATOR_PATH = SCRIPT_DIRECTORY / "publication_authority.py"
BUILDS_SHA = "c" * 40
SOURCE_SHA = "d" * 40


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
