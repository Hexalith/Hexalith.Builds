import hashlib
import importlib.util
import io
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
AUTHORITY_COMMENT_ID = 123456
AUTHORITY_URL = (
    "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/comments/123456"
)
# The inventory size a fixture module declares. It is deliberately a fixture value,
# not a shared invariant: every module declares its own count.
FIXTURE_PACKAGE_COUNT = 14
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
            authority_url=AUTHORITY_URL,
            authority_owner="github:release-owner",
            package_manifest=root / "release-packages.json",
            expected_package_count=FIXTURE_PACKAGE_COUNT,
            contract_directory=self.create_contract(root),
            evidence_directory=root / "evidence",
            phase=phase,
        )
        self.write_manifest(arguments.package_manifest)
        return arguments

    def write_manifest(self, path, count=FIXTURE_PACKAGE_COUNT):
        path.write_text(
            json.dumps({"packages": [{"id": f"Package.{index}"} for index in range(count)]}),
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

    def authority_record(self, identity, *, owner="release-owner", expires_at="2099-01-01T00:00:00Z"):
        return {
            "id": AUTHORITY_COMMENT_ID,
            "issue_url": "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/42",
            "html_url": "https://github.com/Hexalith/Hexalith.EventStore/issues/42#issuecomment-123456",
            "user": {"login": owner},
            "author_association": "OWNER",
            "created_at": "2026-08-20T10:00:00Z",
            "updated_at": "2026-08-20T10:00:00Z",
            "body": json.dumps(
                {
                    "schema": self.validator.AUTHORITY_SCHEMA,
                    "role": "release-owner",
                    "identity_sha256": hashlib.sha256(
                        self.validator._canonical_bytes(identity)
                    ).hexdigest(),
                    "expires_at": expires_at,
                    "nonce": "story-3-14-authority-0001",
                }
            ),
        }

    def authority_summary(self):
        return {
            "url": AUTHORITY_URL,
            "comment_id": AUTHORITY_COMMENT_ID,
            "issue_url": "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/42",
            "owner": "github:release-owner",
            "created_at": "2026-08-20T10:00:00Z",
            "expires_at": "2099-01-01T00:00:00Z",
            "nonce": "story-3-14-authority-0001",
            "identity_sha256": "1" * 64,
            "record_sha256": "2" * 64,
        }

    def test_github_authority_binds_identity_owner_expiry_and_one_use_consumption(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments, self.source_proof())
            record = self.authority_record(identity)
            with mock.patch.object(self.validator, "_github_json", return_value=record):
                authority = self.validator.validate_publication_authority(
                    arguments,
                    identity,
                    "fixture-token",
                    now=self.validator.datetime(2026, 8, 20, 11, tzinfo=self.validator.timezone.utc),
                )
            self.assertEqual("github:release-owner", authority["owner"])
            self.assertEqual(str(identity["run"]["id"]), identity["run"]["id"])

            receipt = {"id": 789, "body": json.dumps(self.validator._consumption_body(authority, identity))}
            with mock.patch.object(self.validator, "_github_json_array", return_value=[]):
                self.assertIsNone(
                    self.validator.require_authority_state(authority, identity, "verify", "fixture-token")
                )
            with mock.patch.object(self.validator, "_github_json_array", return_value=[receipt]):
                self.assertEqual(
                    receipt,
                    self.validator.require_authority_state(authority, identity, "container", "fixture-token"),
                )
                with self.assertRaises(self.validator.PreflightError) as context:
                    self.validator.require_authority_state(authority, identity, "verify", "fixture-token")
                self.assertEqual("authority-replayed", context.exception.code)

    def test_github_authority_rejects_expired_wrong_owner_and_identity_mismatch(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments, self.source_proof())
            now = self.validator.datetime(2026, 8, 20, 11, tzinfo=self.validator.timezone.utc)
            scenarios = (
                (self.authority_record(identity, expires_at="2026-08-20T10:30:00Z"), "authority-expired"),
                (self.authority_record(identity, owner="intruder"), "authority-wrong-role"),
            )
            changed = self.authority_record(identity)
            changed_body = json.loads(changed["body"])
            changed_body["identity_sha256"] = "0" * 64
            changed["body"] = json.dumps(changed_body)
            scenarios += ((changed, "authority-mismatch"),)
            for record, code in scenarios:
                with self.subTest(code=code), mock.patch.object(self.validator, "_github_json", return_value=record):
                    with self.assertRaises(self.validator.PreflightError) as context:
                        self.validator.validate_publication_authority(
                            arguments, identity, "fixture-token", now=now
                        )
                    self.assertEqual(code, context.exception.code)

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
            self.assertEqual(FIXTURE_PACKAGE_COUNT, len(identity["packages"]["normalized_ids"]))
            self.assertEqual(
                [f"package.{index}" for index in range(FIXTURE_PACKAGE_COUNT)],
                identity["packages"]["normalized_ids"],
            )
            expected_hash = hashlib.sha256(
                (arguments.contract_directory / "publication_preflight.py").read_bytes()
            ).hexdigest()
            self.assertEqual(
                expected_hash,
                identity["builds"]["files"]["publication_preflight.py"],
            )

    def test_multi_container_identity_is_a_canonical_order_independent_set(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            arguments.container_repositories = [
                "REGISTRY.HEXALITH.COM/parties-ui",
                "registry.hexalith.com/parties",
                "registry.hexalith.com/parties-mcp",
            ]
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(arguments, self.source_proof())

                reordered = SimpleNamespace(**vars(arguments))
                reordered.container_repositories = list(reversed(arguments.container_repositories))
                reordered_identity = self.validator.build_publication_identity(reordered, self.source_proof())

            expected = [
                "registry.hexalith.com/parties",
                "registry.hexalith.com/parties-mcp",
                "registry.hexalith.com/parties-ui",
            ]
            self.assertEqual(expected, identity["container_repositories"])
            self.assertNotIn("container_repository", identity)
            self.assertEqual(identity, reordered_identity)

    def test_container_identity_rejects_empty_duplicate_or_malformed_sets(self):
        scenarios = (
            [],
            ["registry.hexalith.com/parties", "REGISTRY.HEXALITH.COM/parties"],
            ["registry.hexalith.com/parties", "registry.hexalith.com/../escape"],
        )
        for repositories in scenarios:
            with self.subTest(repositories=repositories), self.assertRaises(
                self.validator.PreflightError
            ) as context:
                self.validator._canonical_container_repositories(repositories)
            self.assertEqual("container-repository-invalid", context.exception.code)

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
        packages = [f"Package.{index}" for index in range(FIXTURE_PACKAGE_COUNT)]
        calls = []

        def absent_probe(kind, identity, version):
            calls.append((kind, identity, version))
            return 404

        evidence = self.validator.validate_destination_absence(
            packages,
            "3.78.0",
            "registry.hexalith.com/eventstore",
            absent_probe,
            FIXTURE_PACKAGE_COUNT,
        )
        self.assertEqual("pass", evidence["result"])
        self.assertEqual(FIXTURE_PACKAGE_COUNT, evidence["package_count"])
        self.assertEqual(FIXTURE_PACKAGE_COUNT + 1, len(calls))

        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.78.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 200 if identity == "Package.7" else 404,
                FIXTURE_PACKAGE_COUNT,
            )
        self.assertEqual("version-collision", context.exception.code)

        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_container_absence(
                "3.78.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 503,
            )
        self.assertEqual("destination-probe-failure", context.exception.code)

    def test_package_and_multi_container_destinations_are_checked_as_one_set(self):
        packages = [f"Package.{index}" for index in range(FIXTURE_PACKAGE_COUNT)]
        repositories = [
            "registry.hexalith.com/parties-ui",
            "registry.hexalith.com/parties",
            "registry.hexalith.com/parties-mcp",
        ]
        calls = []

        def absent_probe(kind, identity, version):
            calls.append((kind, identity, version))
            return 404

        evidence = self.validator.validate_destination_absence(
            packages,
            "3.78.0",
            repositories,
            absent_probe,
            FIXTURE_PACKAGE_COUNT,
        )

        expected = sorted(repositories)
        self.assertEqual(3, evidence["container_count"])
        self.assertEqual(expected, evidence["container_repositories"])
        self.assertEqual(
            [("container", repository, "3.78.0") for repository in expected],
            calls[-3:],
        )

        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.78.0",
                repositories,
                lambda kind, identity, version: (
                    200 if identity == "registry.hexalith.com/parties-ui" else 404
                ),
                FIXTURE_PACKAGE_COUNT,
            )
        self.assertEqual("version-collision", context.exception.code)

    def test_case_insensitive_duplicate_or_non_exact_package_inventory_fails_closed(self):
        packages = [f"Package.{index}" for index in range(FIXTURE_PACKAGE_COUNT)]
        packages[-1] = "package.0"
        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.78.0",
                "registry.hexalith.com/eventstore",
                lambda kind, identity, version: 404,
                FIXTURE_PACKAGE_COUNT,
            )
        self.assertEqual("package-inventory-mismatch", context.exception.code)

    def test_declared_package_count_gates_the_inventory_for_any_module_size(self):
        for declared, actual in ((5, 5), (14, 14), (1, 1)):
            with self.subTest(declared=declared):
                packages = [f"Package.{index}" for index in range(actual)]
                evidence = self.validator.validate_destination_absence(
                    packages,
                    "3.78.0",
                    "registry.hexalith.com/tenants",
                    lambda kind, identity, version: 404,
                    declared,
                )
                self.assertEqual(declared, evidence["package_count"])

        for declared, actual in ((5, 4), (5, 6), (14, 5)):
            with self.subTest(declared=declared, actual=actual):
                packages = [f"Package.{index}" for index in range(actual)]
                with self.assertRaises(self.validator.PreflightError) as context:
                    self.validator.validate_destination_absence(
                        packages,
                        "3.78.0",
                        "registry.hexalith.com/tenants",
                        lambda kind, identity, version: 404,
                        declared,
                    )
                self.assertEqual("package-inventory-mismatch", context.exception.code)

    def test_duplicate_ids_fail_closed_at_a_small_module_inventory(self):
        packages = [f"Package.{index}" for index in range(5)]
        packages[-1] = "package.0"
        with self.assertRaises(self.validator.PreflightError) as context:
            self.validator.validate_destination_absence(
                packages,
                "3.78.0",
                "registry.hexalith.com/tenants",
                lambda kind, identity, version: 404,
                5,
            )
        self.assertEqual("package-inventory-mismatch", context.exception.code)

        with tempfile.TemporaryDirectory() as temporary_directory:
            manifest = Path(temporary_directory) / "release-packages.json"
            manifest.write_text(
                json.dumps({"packages": [{"id": package_id} for package_id in packages]}),
                encoding="utf-8",
            )
            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator._load_package_identity(manifest, 5)
            self.assertEqual("package-inventory-mismatch", context.exception.code)

    def test_manifest_identity_honours_the_declared_count_and_rejects_drift(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            manifest = root / "release-packages.json"
            self.write_manifest(manifest, count=5)

            identity = self.validator._load_package_identity(manifest, 5)
            self.assertEqual(5, len(identity["ids"]))

            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator._load_package_identity(manifest, 6)
            self.assertEqual("package-inventory-mismatch", context.exception.code)

    def test_expected_package_count_argument_is_required_and_must_be_positive(self):
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
                "--authority-url",
                arguments.authority_url,
                "--authority-owner",
                arguments.authority_owner,
                "--package-manifest",
                str(arguments.package_manifest),
                "--contract-directory",
                str(arguments.contract_directory),
                "--evidence-directory",
                str(arguments.evidence_directory),
                "--phase",
                "verify",
            ]
            previous_directory = Path.cwd()
            try:
                # Every path argument must resolve below the workspace, otherwise these
                # invocations would be rejected for path confinement instead of the count.
                os.chdir(root)

                with mock.patch.object(sys, "argv", [*common, "--expected-package-count", "5"]):
                    parsed = self.validator._parse_arguments()
                    self.assertEqual(5, parsed.expected_package_count)
                    self.assertEqual([arguments.container_repository], parsed.container_repositories)

                # Omitted entirely, then values a module must never be able to declare.
                invocations = [common]
                invocations.extend(
                    [*common, "--expected-package-count", value]
                    for value in ("0", "-1", "05", "5.0", "", "five")
                )
                for argv in invocations:
                    with (
                        self.subTest(count=argv[-1] if argv is not common else "<omitted>"),
                        mock.patch.object(sys, "argv", argv),
                        mock.patch("sys.stderr", new=io.StringIO()),
                        self.assertRaises(SystemExit) as context,
                    ):
                        self.validator._parse_arguments()
                    self.assertNotEqual(0, context.exception.code)
            finally:
                os.chdir(previous_directory)

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

        packages = [f"Package.{index}" for index in range(FIXTURE_PACKAGE_COUNT)]
        for status in (201, 204, 301, 302, 401, 403, 429, 500, 503):
            with self.subTest(status=status), self.assertRaises(self.validator.PreflightError) as context:
                self.validator.validate_destination_absence(
                    packages,
                    "3.78.0",
                    "registry.hexalith.com/eventstore",
                    lambda kind, identity, version, response_status=status: response_status,
                    FIXTURE_PACKAGE_COUNT,
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

    def test_multi_container_sequence_rejects_set_drift_before_publish(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            verify = self.arguments(root)
            verify.container_repositories = [
                "registry.hexalith.com/parties",
                "registry.hexalith.com/parties-mcp",
                "registry.hexalith.com/parties-ui",
            ]
            with mock.patch.dict(os.environ, self.runtime_environment(), clear=True):
                identity = self.validator.build_publication_identity(verify, self.source_proof())
                changed = SimpleNamespace(**vars(verify))
                changed.container_repositories = verify.container_repositories[:-1]
                changed_identity = self.validator.build_publication_identity(changed, self.source_proof())

            self.validator._write_evidence(root / "evidence", "verify", identity, {"result": "pass"})
            with self.assertRaises(self.validator.PreflightError) as context:
                self.validator._write_evidence(
                    root / "evidence",
                    "publish",
                    changed_identity,
                    {"result": "pass"},
                )
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

    def test_main_runs_all_three_fail_closed_destination_and_authority_phases(self):
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
                "--authority-url",
                arguments.authority_url,
                "--authority-owner",
                arguments.authority_owner,
                "--package-manifest",
                str(arguments.package_manifest),
                "--expected-package-count",
                str(FIXTURE_PACKAGE_COUNT),
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
                    mock.patch.object(
                        self.validator,
                        "validate_publication_authority",
                        return_value=self.authority_summary(),
                    ),
                    mock.patch.object(self.validator, "require_authority_state", return_value=None),
                    mock.patch.object(
                        self.validator,
                        "consume_publication_authority",
                        return_value={"id": 789, "result": "consumed"},
                    ),
                ):
                    for phase in ("verify", "publish", "container"):
                        with mock.patch.object(sys, "argv", [*common, "--phase", phase]):
                            self.assertEqual(0, self.validator.main())
            finally:
                os.chdir(previous_directory)

            self.assertTrue((root / "evidence" / "publication-preflight.container.json").is_file())
            self.assertEqual(6, source_proof.call_count)

    def test_main_freezes_and_revalidates_three_container_repositories(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            arguments = self.arguments(root)
            repositories = [
                "registry.hexalith.com/parties-ui",
                "registry.hexalith.com/parties",
                "registry.hexalith.com/parties-mcp",
            ]
            common = [
                "publication_preflight.py",
                "--repository",
                arguments.repository,
                "--version",
                arguments.version,
                "--source-sha",
                arguments.source_sha,
                "--builds-execution-sha",
                arguments.builds_execution_sha,
                "--environment-name",
                arguments.environment_name,
                "--authority-url",
                arguments.authority_url,
                "--authority-owner",
                arguments.authority_owner,
                "--package-manifest",
                str(arguments.package_manifest),
                "--expected-package-count",
                str(FIXTURE_PACKAGE_COUNT),
                "--contract-directory",
                str(arguments.contract_directory),
                "--evidence-directory",
                str(arguments.evidence_directory),
            ]
            for repository in repositories:
                common.extend(("--container-repository", repository))

            probed = []

            def absent_probe(kind, identity, version):
                probed.append((kind, identity, version))
                return 404

            previous_directory = Path.cwd()
            try:
                os.chdir(root)
                with (
                    mock.patch.dict(os.environ, self.runtime_environment(), clear=True),
                    mock.patch.object(self.validator, "destination_probe", return_value=absent_probe),
                    mock.patch.object(
                        self.validator,
                        "prove_current_green_source",
                        return_value=self.source_proof(),
                    ),
                    mock.patch.object(
                        self.validator,
                        "validate_publication_authority",
                        return_value=self.authority_summary(),
                    ),
                    mock.patch.object(self.validator, "require_authority_state", return_value=None),
                    mock.patch.object(
                        self.validator,
                        "consume_publication_authority",
                        return_value={"id": 789, "result": "consumed"},
                    ),
                ):
                    for phase in ("verify", "publish", "container"):
                        with mock.patch.object(sys, "argv", [*common, "--phase", phase]):
                            self.assertEqual(0, self.validator.main())
            finally:
                os.chdir(previous_directory)

            frozen = json.loads(
                (root / "evidence" / "publication-identity.json").read_text(encoding="utf-8")
            )
            self.assertEqual(sorted(repositories), frozen["container_repositories"])
            self.assertEqual(9, sum(1 for kind, _, _ in probed if kind == "container"))

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
                mock.patch.object(
                    self.validator,
                    "validate_publication_authority",
                    return_value=self.authority_summary(),
                ),
                mock.patch.object(self.validator, "require_authority_state", return_value=None),
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
                    mock.patch.object(
                        self.validator,
                        "validate_publication_authority",
                        return_value=self.authority_summary(),
                    ),
                    mock.patch.object(self.validator, "require_authority_state", return_value=None),
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
