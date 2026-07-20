import hashlib
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
VALIDATOR_PATH = SCRIPT_DIRECTORY / "publication_preflight.py"
BUILDS_SHA = "c" * 40
SOURCE_SHA = "d" * 40
sys.path.insert(0, str(SCRIPT_DIRECTORY))


def load_validator():
    spec = importlib.util.spec_from_file_location("publication_preflight", VALIDATOR_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load publication preflight.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class PublicationPreflightTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.validator = load_validator()

    def create_contract(self, root):
        contract = root / "contract"
        contract.mkdir()
        for name in self.validator.REQUIRED_CONTRACT_FILES:
            (contract / name).write_text(f"fixture bytes for {name}\n", encoding="utf-8")
        return contract

    def runtime_environment(self):
        return {
            "GITHUB_REPOSITORY": "Hexalith/Hexalith.EventStore",
            "GITHUB_SHA": SOURCE_SHA,
            "GITHUB_WORKFLOW_SHA": SOURCE_SHA,
            "GITHUB_RUN_ID": "29713052827",
            "GITHUB_RUN_ATTEMPT": "1",
            "GITHUB_RUN_NUMBER": "2048",
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_WORKFLOW_REF": "Hexalith/Hexalith.EventStore/.github/workflows/release.yml@refs/heads/main",
            "GITHUB_ACTOR": "release-operator",
            "GITHUB_TRIGGERING_ACTOR": "release-operator",
            "HEXALITH_ZOT_USERNAME": "fixture-user",
            "HEXALITH_ZOT_API_KEY": "fixture-key",
        }

    def arguments(self, root, phase="verify"):
        return SimpleNamespace(
            repository="Hexalith/Hexalith.EventStore",
            version="3.78.0",
            source_sha=SOURCE_SHA,
            container_repository="registry.hexalith.com/eventstore",
            builds_execution_sha=BUILDS_SHA,
            environment_name="production",
            package_manifest=root / "release-packages.json",
            contract_directory=self.create_contract(root),
            evidence_directory=root / "evidence",
            phase=phase,
        )

    def write_manifest(self, path):
        path.write_text(
            json.dumps({"packages": [{"id": f"Package.{index}"} for index in range(14)]}),
            encoding="utf-8",
        )

    def test_exact_identity_records_repository_source_builds_run_environment_and_hashes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments)

            self.assertEqual(self.validator.PREFLIGHT_SCHEMA, identity["schema"])
            self.assertEqual(SOURCE_SHA, identity["source_sha"])
            self.assertEqual(BUILDS_SHA, identity["builds"]["workflow_sha"])
            self.assertEqual(BUILDS_SHA, identity["builds"]["action_sha"])
            self.assertEqual("29713052827", identity["run"]["id"])
            self.assertEqual("production", identity["environment"])
            expected_hash = hashlib.sha256(
                (arguments.contract_directory / "publication_preflight.py").read_bytes()
            ).hexdigest()
            self.assertEqual(
                expected_hash,
                identity["builds"]["files"]["publication_preflight.py"],
            )

    def test_runtime_repository_source_and_run_mismatches_fail_closed(self):
        mutations = {
            "repository-mismatch": {"GITHUB_REPOSITORY": "Other/Repository"},
            "source-mismatch": {"GITHUB_SHA": "e" * 40},
            "run-identity-invalid": {"GITHUB_RUN_ATTEMPT": "0"},
            "run-workflow-sha-invalid": {"GITHUB_WORKFLOW_SHA": "main"},
        }
        for scenario, updates in mutations.items():
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as temporary_directory:
                root = Path(temporary_directory)
                arguments = self.arguments(root)
                environment = self.runtime_environment()
                environment.update(updates)
                with (
                    mock.patch.dict(os.environ, environment, clear=True),
                    self.assertRaises(self.validator.PreflightError),
                ):
                    self.validator.build_publication_identity(arguments)

    def test_package_and_container_destinations_must_all_be_absent(self):
        packages = [f"Package.{index}" for index in range(14)]
        calls = []

        def absent_probe(kind, identity, version):
            calls.append((kind, identity, version))
            return 404

        evidence = self.validator.validate_destination_absence(
            packages,
            "3.78.0",
            "registry.hexalith.com/eventstore",
            absent_probe,
        )
        self.assertEqual("pass", evidence["result"])
        self.assertEqual(14, evidence["package_count"])
        self.assertEqual(15, len(calls))

        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.78.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 200 if identity == "Package.7" else 404,
            )
        self.assertEqual("version-collision", context.exception.code)

        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_container_absence(
                "3.78.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 503,
            )
        self.assertEqual("destination-probe-failure", context.exception.code)

    def test_case_insensitive_duplicate_or_non_exact_package_inventory_fails_closed(self):
        packages = [f"Package.{index}" for index in range(14)]
        packages[-1] = "package.0"
        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.78.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 404,
            )
        self.assertEqual("package-inventory-mismatch", context.exception.code)

    def test_registry_probe_requests_every_recognized_manifest_media_type(self):
        captured = []

        def status(request):
            captured.append(request)
            return 404

        with mock.patch.object(self.validator, "_http_status", side_effect=status):
            probe = self.validator.destination_probe("registry-user", "registry-key")
            self.assertEqual(404, probe("container", "registry.hexalith.com/eventstore", "3.78.0"))

        self.assertEqual(self.validator.MANIFEST_ACCEPT, captured[0].get_header("Accept"))

    def test_verify_publish_container_sequence_freezes_exact_identity(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments)
            destination = {"result": "pass"}

            self.validator._write_evidence(root / "evidence", "verify", identity, destination)
            self.validator._write_evidence(root / "evidence", "publish", identity, destination)
            self.validator._write_evidence(root / "evidence", "container", identity, destination)

            frozen = json.loads((root / "evidence" / "publication-identity.json").read_text(encoding="utf-8"))
            self.assertEqual(identity, frozen)
            for phase in ("verify", "publish", "container"):
                evidence = json.loads(
                    (root / "evidence" / f"publication-preflight.{phase}.json").read_text(encoding="utf-8")
                )
                self.assertEqual(phase, evidence["phase"])
                self.assertEqual("pass", evidence["result"])

            changed = dict(identity)
            changed["source_sha"] = "e" * 40
            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator._require_frozen_identity(root / "evidence", changed)
            self.assertEqual("publication-identity-changed", context.exception.code)

    def test_container_phase_requires_publish_recheck(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments)
            self.validator._write_evidence(root / "evidence", "verify", identity, {"result": "pass"})

            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator._write_evidence(
                    root / "evidence",
                    "container",
                    identity,
                    {"result": "pass"},
                )
            self.assertEqual("preflight-sequence-invalid", context.exception.code)

    def test_main_runs_all_three_fail_closed_destination_phases_without_comments(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            self.write_manifest(arguments.package_manifest)
            common = [
                "publication_preflight.py",
                "--repository",
                arguments.repository,
                "--version",
                arguments.version,
                "--source-sha",
                arguments.source_sha,
                "--container-repository",
                arguments.container_repository,
                "--builds-execution-sha",
                arguments.builds_execution_sha,
                "--environment-name",
                arguments.environment_name,
                "--package-manifest",
                str(arguments.package_manifest),
                "--contract-directory",
                str(arguments.contract_directory),
                "--evidence-directory",
                str(arguments.evidence_directory),
            ]
            previous_directory = Path.cwd()
            try:
                os.chdir(root)
                with (
                    mock.patch.dict(os.environ, self.runtime_environment(), clear=True),
                    mock.patch.object(
                        self.validator,
                        "destination_probe",
                        return_value=lambda kind, identity, version: 404,
                    ),
                ):
                    for phase in ("verify", "publish", "container"):
                        with mock.patch.object(sys, "argv", [*common, "--phase", phase]):
                            self.assertEqual(0, self.validator.main())
            finally:
                os.chdir(previous_directory)

            self.assertTrue((root / "evidence" / "publication-preflight.container.json").is_file())


if __name__ == "__main__":
    unittest.main()
