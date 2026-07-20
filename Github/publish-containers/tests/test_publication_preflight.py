import hashlib
import importlib.util
import json
import os
import sys
import tempfile
import unittest
import urllib.error
import urllib.parse
import urllib.request
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
            "GITHUB_REF": "refs/heads/main",
            "GITHUB_WORKFLOW_SHA": SOURCE_SHA,
            "GITHUB_RUN_ID": "29713052827",
            "GITHUB_RUN_ATTEMPT": "1",
            "GITHUB_RUN_NUMBER": "2048",
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_WORKFLOW_REF": "Hexalith/Hexalith.EventStore/.github/workflows/release.yml@refs/heads/main",
            "GITHUB_ACTOR": "release-operator",
            "GITHUB_TRIGGERING_ACTOR": "release-operator",
            "GITHUB_TOKEN": "fixture-github-token",
            "HEXALITH_ZOT_USERNAME": "fixture-user",
            "HEXALITH_ZOT_API_KEY": "fixture-key",
        }

    def arguments(self, root, phase="verify"):
        arguments = SimpleNamespace(
            repository="Hexalith/Hexalith.EventStore",
            version="3.78.0",
            source_sha=SOURCE_SHA,
            source_branch="main",
            source_ci_workflow="ci.yml",
            container_repository="registry.hexalith.com/eventstore",
            builds_execution_sha=BUILDS_SHA,
            environment_name="production",
            package_manifest=root / "release-packages.json",
            contract_directory=self.create_contract(root),
            evidence_directory=root / "evidence",
            phase=phase,
        )
        self.write_manifest(arguments.package_manifest)
        return arguments

    def write_manifest(self, path):
        path.write_text(
            json.dumps({"packages": [{"id": f"Package.{index}"} for index in range(14)]}),
            encoding="utf-8",
        )

    def source_proof(self):
        return {
            "branch": "main",
            "ref": "refs/heads/main",
            "live_sha": SOURCE_SHA,
            "ci_workflow": "ci.yml",
            "ci_run": {
                "id": 29728255746,
                "head_sha": SOURCE_SHA,
                "head_branch": "main",
                "event": "push",
                "status": "completed",
                "conclusion": "success",
            },
        }

    def test_exact_identity_records_repository_source_builds_run_environment_and_hashes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments, self.source_proof())

            self.assertEqual(self.validator.PREFLIGHT_SCHEMA, identity["schema"])
            self.assertEqual(SOURCE_SHA, identity["source_sha"])
            self.assertEqual(BUILDS_SHA, identity["builds"]["workflow_sha"])
            self.assertEqual(BUILDS_SHA, identity["builds"]["action_sha"])
            self.assertEqual("29713052827", identity["run"]["id"])
            self.assertEqual("production", identity["environment"])
            self.assertEqual("main", identity["source"]["branch"])
            self.assertEqual(29728255746, identity["source"]["ci_run"]["id"])
            self.assertEqual(14, len(identity["packages"]["normalized_ids"]))
            self.assertEqual(
                [f"package.{index}" for index in range(14)],
                identity["packages"]["normalized_ids"],
            )
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
                with mock.patch.dict(os.environ, environment, clear=True):
                    with self.assertRaises(self.validator.PreflightError):
                        self.validator.build_publication_identity(arguments, self.source_proof())

    def test_source_proof_queries_exact_main_and_successful_push_ci(self):
        successful_run = self.source_proof()["ci_run"]
        captured = []
        api_token = f"fixture-{self.id()}"

        def github_json(url, token):
            captured.append((url, token))
            if "/git/ref/heads/main" in url:
                return {"object": {"sha": SOURCE_SHA}}
            return {"workflow_runs": [successful_run]}

        with mock.patch.object(self.validator, "_github_json", side_effect=github_json):
            proof = self.validator.prove_current_green_source(
                "Hexalith/Hexalith.EventStore",
                SOURCE_SHA,
                "main",
                "ci.yml",
                api_token,
            )

        self.assertEqual(self.source_proof(), proof)
        self.assertEqual(
            "https://api.github.com/repos/Hexalith/Hexalith.EventStore/git/ref/heads/main",
            captured[0][0],
        )
        parsed = urllib.parse.urlparse(captured[1][0])
        self.assertEqual(
            "/repos/Hexalith/Hexalith.EventStore/actions/workflows/ci.yml/runs",
            parsed.path,
        )
        self.assertEqual(
            {
                "branch": ["main"],
                "event": ["push"],
                "head_sha": [SOURCE_SHA],
                "status": ["success"],
                "per_page": ["100"],
            },
            urllib.parse.parse_qs(parsed.query),
        )
        self.assertTrue(all(token == api_token for _, token in captured))

    def test_source_proof_rejects_stale_main(self):
        with mock.patch.object(
            self.validator,
            "_github_json",
            return_value={"object": {"sha": "e" * 40}},
        ):
            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator.prove_current_green_source(
                    "Hexalith/Hexalith.EventStore",
                    SOURCE_SHA,
                    "main",
                    "ci.yml",
                    f"fixture-{self.id()}",
                )
        self.assertEqual("source-no-longer-current", context.exception.code)

    def test_source_proof_rejects_missing_successful_push_ci(self):
        with mock.patch.object(
            self.validator,
            "_github_json",
            side_effect=[{"object": {"sha": SOURCE_SHA}}, {"workflow_runs": []}],
        ):
            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator.prove_current_green_source(
                    "Hexalith/Hexalith.EventStore",
                    SOURCE_SHA,
                    "main",
                    "ci.yml",
                    f"fixture-{self.id()}",
                )
        self.assertEqual("source-ci-not-successful", context.exception.code)

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

    def test_destination_probe_sends_exact_read_only_nuget_and_oci_requests(self):
        captured = []

        class FakeResponse:
            status = 404

            def __enter__(self):
                return self

            def __exit__(self, exception_type, exception, traceback):
                return False

            def read(self, size):
                self.read_size = size
                return b""

        def open_request(request, timeout):
            captured.append((request, timeout))
            return FakeResponse()

        with mock.patch.object(self.validator.URL_OPENER, "open", side_effect=open_request):
            probe = self.validator.destination_probe("registry-user", "registry-key")
            self.assertEqual(404, probe("nuget", "Hexalith.EventStore.Contracts", "3.78.0"))
            self.assertEqual(404, probe("container", "registry.hexalith.com/eventstore", "3.78.0"))

        nuget = captured[0][0]
        container = captured[1][0]
        self.assertEqual(
            "https://api.nuget.org/v3-flatcontainer/hexalith.eventstore.contracts/3.78.0/"
            "hexalith.eventstore.contracts.3.78.0.nupkg",
            nuget.full_url,
        )
        self.assertEqual("HEAD", nuget.get_method())
        self.assertIsNone(nuget.get_header("Authorization"))
        self.assertEqual(
            "https://registry.hexalith.com/v2/eventstore/manifests/3.78.0",
            container.full_url,
        )
        self.assertEqual("HEAD", container.get_method())
        self.assertEqual(self.validator.MANIFEST_ACCEPT, container.get_header("Accept"))
        self.assertEqual(
            "Basic cmVnaXN0cnktdXNlcjpyZWdpc3RyeS1rZXk=",
            container.get_header("Authorization"),
        )
        self.assertTrue(all(timeout == 30 for _, timeout in captured))

    def test_destination_redirects_and_non_authoritative_statuses_fail_closed(self):
        request = urllib.request.Request(
            "https://registry.hexalith.com/v2/eventstore/manifests/3.78.0",
            headers={"Authorization": "Basic fixture-secret"},
            method="HEAD",
        )
        for target in (
            "https://registry.hexalith.com/v2/eventstore/manifests/other",
            "https://storage.example.test/manifest",
            "http://registry.hexalith.com/v2/eventstore/manifests/other",  # NOSONAR -- downgrade fixture.
        ):
            with self.subTest(target=target):
                handler = self.validator.FailClosedRedirectHandler()
                with self.assertRaises(urllib.error.HTTPError):
                    handler.redirect_request(request, None, 302, "Found", {}, target)

        packages = [f"Package.{index}" for index in range(14)]
        for status in (201, 204, 301, 302, 401, 403, 429, 500, 503):
            with self.subTest(status=status), self.assertRaises(self.validator.PreflightError) as context:
                self.validator.validate_destination_absence(
                    packages,
                    "3.78.0",
                    "registry.hexalith.com/eventstore",
                    lambda kind, identity, version, response_status=status: response_status,
                )
            self.assertEqual("destination-probe-failure", context.exception.code)

    def test_verify_publish_container_sequence_freezes_exact_identity(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments, self.source_proof())
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
                identity = self.validator.build_publication_identity(arguments, self.source_proof())
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
                    mock.patch.object(
                        self.validator,
                        "prove_current_green_source",
                        return_value=self.source_proof(),
                    ) as source_proof,
                ):
                    for phase in ("verify", "publish", "container"):
                        with mock.patch.object(sys, "argv", [*common, "--phase", phase]):
                            self.assertEqual(0, self.validator.main())
            finally:
                os.chdir(previous_directory)

            self.assertTrue((root / "evidence" / "publication-preflight.container.json").is_file())
            self.assertEqual(6, source_proof.call_count)

    def test_source_race_during_probe_blocks_phase_evidence(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)

            calls = 0

            def advancing_source(*unused):
                nonlocal calls
                del unused
                calls += 1
                if calls == 1:
                    return self.source_proof()
                raise self.validator.PreflightError(
                    "source-no-longer-current",
                    "The release source is no longer the current main tip.",
                )

            with (
                mock.patch.dict(os.environ, self.runtime_environment(), clear=True),
                mock.patch.object(
                    self.validator,
                    "prove_current_green_source",
                    side_effect=advancing_source,
                ),
                mock.patch.object(
                    self.validator,
                    "destination_probe",
                    return_value=lambda kind, identity, version: 404,
                ),
                self.assertRaises(self.validator.PreflightError) as context,
            ):
                self.validator._validate_publication(arguments)

            self.assertEqual("source-no-longer-current", context.exception.code)
            self.assertFalse((root / "evidence" / "publication-preflight.verify.json").exists())

    def test_manifest_order_case_and_content_mutation_fail_frozen_identity(self):
        mutations = {
            "order": lambda manifest: manifest["packages"].reverse(),
            "case": lambda manifest: manifest["packages"][0].update({"id": "package.0"}),
            "content": lambda manifest: manifest.update({"contract": "changed"}),
        }
        for scenario, mutate in mutations.items():
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as temporary_directory:
                root = Path(temporary_directory)
                verify = self.arguments(root)
                with (
                    mock.patch.dict(os.environ, self.runtime_environment(), clear=True),
                    mock.patch.object(
                        self.validator,
                        "prove_current_green_source",
                        return_value=self.source_proof(),
                    ),
                    mock.patch.object(
                        self.validator,
                        "destination_probe",
                        return_value=lambda kind, identity, version: 404,
                    ),
                ):
                    self.validator._validate_publication(verify)
                    manifest = json.loads(verify.package_manifest.read_text(encoding="utf-8"))
                    mutate(manifest)
                    verify.package_manifest.write_text(  # NOSONAR -- validated temporary fixture path.
                        json.dumps(manifest),
                        encoding="utf-8",
                    )
                    publish = SimpleNamespace(**vars(verify))
                    publish.phase = "publish"
                    with self.assertRaises(self.validator.PreflightError) as context:
                        self.validator._validate_publication(publish)
                self.assertEqual("publication-identity-changed", context.exception.code)


if __name__ == "__main__":
    unittest.main()
