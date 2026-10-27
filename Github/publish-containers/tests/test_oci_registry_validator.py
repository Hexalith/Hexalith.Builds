import argparse
import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from unittest import mock


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
REPOSITORY_ROOT = SCRIPT_DIRECTORY.parents[1]
FIXTURE_ROOT = REPOSITORY_ROOT / "test" / "fixtures" / "publish-containers"
VALIDATOR_PATH = SCRIPT_DIRECTORY / "oci_registry_validator.py"
SOURCE_SHA = "d" * 40
RELEASE_VERSION = "3.76.1"
REPOSITORY = "Hexalith/Hexalith.EventStore"
CREATED = "2024-02-29T23:59:59Z"


def compact_json(value):
    return json.dumps(value, separators=(",", ":"), sort_keys=True).encode("utf-8")


def digest(value):
    return f"sha256:{hashlib.sha256(value).hexdigest()}"


def load_validator():
    spec = importlib.util.spec_from_file_location("oci_registry_validator", VALIDATOR_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load OCI registry validator.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_body(root, name, body):
    path = root / name
    path.write_bytes(body)
    return name


def build_capture(root, mutation):
    config_media_type = "application/vnd.oci.image.config.v1+json"
    child_media_type = "application/vnd.oci.image.manifest.v1+json"
    index_media_type = "application/vnd.oci.image.index.v1+json"
    platforms = [("linux", "amd64"), ("linux", "arm64")]
    objects = {}
    descriptors = []

    for os_name, architecture in platforms:
        labels = {
            "org.opencontainers.image.source": f"https://github.com/{REPOSITORY}",
            "org.opencontainers.image.url": f"https://github.com/{REPOSITORY}/releases/tag/v{RELEASE_VERSION}",
            "org.opencontainers.image.documentation": (
                f"https://github.com/{REPOSITORY}/blob/{SOURCE_SHA}/README.md"
            ),
            "org.opencontainers.image.revision": SOURCE_SHA,
            "org.opencontainers.image.version": RELEASE_VERSION,
            "org.opencontainers.image.created": CREATED,
            "org.opencontainers.artifact.created": CREATED,
        }
        if mutation == "truncated-url-label" and architecture == "amd64":
            labels["org.opencontainers.image.source"] = "https"
        if mutation == "missing-revision-label" and architecture == "arm64":
            labels.pop("org.opencontainers.image.revision")
        if mutation == "missing-created-label" and architecture == "amd64":
            labels.pop("org.opencontainers.image.created")
        if mutation == "missing-artifact-created-label" and architecture == "arm64":
            labels.pop("org.opencontainers.artifact.created")
        if mutation == "truncated-created-label" and architecture == "amd64":
            labels["org.opencontainers.image.created"] = "2024-02-29T23"
        if mutation == "different-child-created-label" and architecture == "arm64":
            labels["org.opencontainers.artifact.created"] = "2024-03-01T00:00:00Z"
        if mutation == "invalid-created-date" and architecture == "amd64":
            labels["org.opencontainers.image.created"] = labels["org.opencontainers.artifact.created"] = "2023-02-29T00:00:00Z"
        if mutation == "different-child-created-pair" and architecture == "arm64":
            labels["org.opencontainers.image.created"] = labels["org.opencontainers.artifact.created"] = "2024-03-01T00:00:00Z"
        if mutation == "consistent-wrong-publisher-created":
            labels["org.opencontainers.image.created"] = labels["org.opencontainers.artifact.created"] = "2024-03-01T00:00:00Z"
        config = {"architecture": architecture, "os": os_name, "config": {"Labels": labels}}
        if mutation == "child-config-mismatch" and architecture == "amd64":
            config["architecture"] = "arm64"
        config_body = compact_json(config)
        config_digest = digest(config_body)
        config_name = write_body(root, f"{architecture}-config.json", config_body)
        objects[config_digest] = {"content_type": "application/octet-stream", "body": config_name}

        config_size = len(config_body) + (1 if mutation == "config-size-mismatch" and architecture == "amd64" else 0)
        child = {
            "schemaVersion": 2,
            "mediaType": child_media_type,
            "config": {"mediaType": config_media_type, "size": config_size, "digest": config_digest},
            "layers": [],
        }
        child_body = compact_json(child)
        child_digest = digest(child_body)
        child_name = write_body(root, f"{architecture}-manifest.json", child_body)
        objects[child_digest] = {"content_type": child_media_type, "body": child_name}

        descriptor = {
            "mediaType": child_media_type,
            "size": len(child_body),
            "digest": child_digest,
            "platform": {"os": os_name, "architecture": architecture},
        }
        if mutation == "descriptor-size-mismatch" and architecture == "amd64":
            descriptor["size"] += 1
        if mutation == "duplicate-platform" and architecture == "arm64":
            descriptor["platform"]["architecture"] = "amd64"
        if mutation == "unknown-platform" and architecture == "arm64":
            descriptor["platform"] = {"os": "unknown", "architecture": "unknown"}
        if mutation == "blank-platform" and architecture == "arm64":
            descriptor["platform"]["architecture"] = " "
        if mutation == "variant-platform" and architecture == "arm64":
            descriptor["platform"]["variant"] = "v8"
        descriptors.append(descriptor)

    if mutation == "missing-platform":
        descriptors.pop()
    if mutation == "extra-platform":
        extra = dict(descriptors[0])
        extra["platform"] = {"os": "linux", "architecture": "s390x"}
        descriptors.append(extra)
    if mutation == "malformed-descriptor":
        descriptors[0]["digest"] = "not-a-sha256"

    top_media_type = (
        "application/vnd.docker.distribution.manifest.list.v2+json"
        if mutation == "wrong-top-level-media-type"
        else index_media_type
    )
    index = {"schemaVersion": 2, "mediaType": top_media_type, "manifests": descriptors}
    index_body = compact_json(index)
    index_digest = digest(index_body)
    index_name = write_body(root, "index.json", index_body)
    objects[index_digest] = {"content_type": top_media_type, "body": index_name}

    if mutation == "unresolved-child":
        objects.pop(descriptors[0]["digest"])
    if mutation == "child-content-type-mismatch":
        objects[descriptors[0]["digest"]]["content_type"] = "application/octet-stream"
    if mutation == "raw-digest-mismatch":
        original_digest = descriptors[0]["digest"]
        false_digest = "sha256:" + ("0" * 64)
        descriptors[0]["digest"] = false_digest
        objects[false_digest] = objects.pop(original_digest)
        index_body = compact_json(index)
        index_digest = digest(index_body)
        write_body(root, "index.json", index_body)
        objects[index_digest] = {"content_type": top_media_type, "body": index_name}

    tag_body_name = index_name
    tag_digest = index_digest
    if mutation == "tag-digest-body-disagreement":
        altered_body = index_body + b"\n"
        tag_body_name = write_body(root, "tag-index.json", altered_body)

    if mutation == "single-manifest":
        historical_body = (FIXTURE_ROOT / "v3.75.0-single-manifest.json").read_bytes()
        tag_body_name = write_body(root, "historical-manifest.json", historical_body)
        tag_digest = digest(historical_body)
        objects = {
            tag_digest: {
                "content_type": "application/vnd.docker.distribution.manifest.v2+json",
                "body": tag_body_name,
            }
        }
        top_media_type = "application/vnd.docker.distribution.manifest.v2+json"

    capture = {
        "tag": {
            "content_type": (
                "application/octet-stream" if mutation == "index-content-type-mismatch" else top_media_type
            ),
            "docker_content_digest": None if mutation == "missing-registry-digest" else tag_digest,
            "body": tag_body_name,
        },
        "objects": objects,
    }
    (root / "responses.json").write_text(json.dumps(capture, indent=2) + "\n", encoding="utf-8")


class OciRegistryValidatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.validator = load_validator()

    def test_historical_evidence_is_exact_and_non_authorizing(self):
        evidence = json.loads((FIXTURE_ROOT / "v3.75.0-evidence.json").read_text(encoding="utf-8"))
        self.assertEqual("v3.75.0", evidence["release"])
        self.assertEqual(14, len(evidence["packages"]))
        self.assertEqual(14, len({item["id"] for item in evidence["packages"]}))
        self.assertEqual("failed-non-authorizing", evidence["container"]["result"])
        self.assertEqual("linux/arm64", evidence["container"]["missing_platform"])
        self.assertEqual(
            "sha256:1cb7b6ed3db986e9896cc36f360d14017f3bf3521eed8e33267c3dffd05ca253",
            evidence["container"]["digest"],
        )

    def test_cross_origin_redirect_does_not_forward_registry_authorization(self):
        request = urllib.request.Request(
            "https://registry.example.test/v2/eventstore/blobs/sha256:abc",
            headers={"Authorization": "Basic fixture-secret"},
        )

        redirected = self.validator.SafeRedirectHandler().redirect_request(
            request,
            None,
            302,
            "Found",
            {},
            "https://storage.example.test/signed-blob",
        )

        self.assertIsNotNone(redirected)
        self.assertIsNone(redirected.get_header("Authorization"))

        redirect_request = self.validator.SafeRedirectHandler().redirect_request
        with self.assertRaises(urllib.error.HTTPError) as context:
            redirect_request(
                request,
                None,
                302,
                "Found",
                {},
                "http://storage.example.test/signed-blob",
            )
        context.exception.close()

    def test_registry_http_adapter_preserves_bytes_headers_and_immutable_references(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            capture_root = Path(temporary_directory)
            build_capture(capture_root, "none")
            capture = json.loads((capture_root / "responses.json").read_text(encoding="utf-8"))
            requested = []

            class FakeResponse:
                def __init__(self, response):
                    self.headers = {
                        "Content-Type": response.get("content_type", ""),
                        "Docker-Content-Digest": response.get("docker_content_digest"),
                    }
                    self._body = (capture_root / response["body"]).read_bytes()

                def __enter__(self):
                    return self

                def __exit__(self, exception_type, exception, traceback):
                    return False

                def read(self):
                    return self._body

            def open_request(request, timeout):
                del timeout
                requested.append(request)
                reference = urllib.parse.unquote(request.full_url.rsplit("/", 1)[1])
                response = capture["tag"] if reference == "3.76.1" else capture["objects"][reference]
                return FakeResponse(response)

            with mock.patch.object(self.validator.URL_OPENER, "open", side_effect=open_request):
                evidence = self.validator.validate_registry(
                    "registry.example.test/eventstore:3.76.1",
                    "registry-user",
                    "registry-key",
                )

            expected_index = (capture_root / "index.json").read_bytes()
            self.assertEqual(expected_index, evidence["index_bytes"])
            self.assertEqual(
                ["linux/amd64", "linux/arm64"],
                [child["platform"] for child in evidence["children"]],
            )
            manifest_requests = [request for request in requested if "/manifests/" in request.full_url]
            blob_requests = [request for request in requested if "/blobs/" in request.full_url]
            self.assertEqual(4, len(manifest_requests))
            self.assertEqual(2, len(blob_requests))
            self.assertTrue(
                all(request.get_header("Accept") == self.validator.MANIFEST_ACCEPT for request in manifest_requests)
            )
            self.assertTrue(
                all(request.get_header("Accept") == "application/octet-stream" for request in blob_requests)
            )
            self.assertTrue(
                any(f"/manifests/{evidence['index_digest']}" in request.full_url for request in manifest_requests)
            )
            for child in evidence["children"]:
                self.assertTrue(
                    any(f"/manifests/{child['digest']}" in request.full_url for request in manifest_requests)
                )
                self.assertTrue(
                    any(f"/blobs/{child['config_digest']}" in request.full_url for request in blob_requests)
                )

    def test_cli_inputs_reject_path_escape_and_option_shaped_image(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            with mock.patch.object(Path, "cwd", return_value=root):
                evidence = self.validator.workspace_output_directory(str(root / "evidence"))
                self.assertEqual(root / "evidence", evidence)
                escaped_path = str(root.parent / "escape")
                with self.assertRaises(argparse.ArgumentTypeError):
                    self.validator.workspace_output_directory(escaped_path)

        with self.assertRaises(self.validator.ValidationError) as context:
            self.validator.validated_image_reference("--config=/tmp/host/eventstore:3.78.0")
        self.assertEqual("invalid-image-reference", context.exception.code)

    def test_historical_single_manifest_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            capture_root = Path(temporary_directory)
            build_capture(capture_root, "single-manifest")
            with self.assertRaises(self.validator.ValidationError) as context:
                self.validator.validate_capture(capture_root)
            self.assertEqual("wrong-index-media-type", context.exception.code)

    def test_fixture_matrix(self):
        matrix = json.loads((FIXTURE_ROOT / "validation-cases.json").read_text(encoding="utf-8"))
        for case in matrix["cases"]:
            with self.subTest(case=case["name"]), tempfile.TemporaryDirectory() as temporary_directory:
                capture_root = Path(temporary_directory)
                build_capture(capture_root, case["mutation"])
                if case["expected"] == "pass":
                    evidence = self.validator.validate_capture(capture_root)
                    self.assertEqual(["linux/amd64", "linux/arm64"], evidence["platforms"])
                    continue

                with self.assertRaises(self.validator.ValidationError) as context:
                    self.validator.validate_capture(capture_root)
                self.assertEqual(case["expected"], context.exception.code)

    def test_pass_evidence_preserves_exact_index_bytes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            capture_root = root / "capture"
            evidence_root = root / "evidence"
            capture_root.mkdir()
            build_capture(capture_root, "none")

            evidence = self.validator.validate_capture(capture_root)
            expected_index = evidence["index_bytes"]
            document = self.validator.write_evidence(
                evidence_root,
                "registry.example.test/eventstore:3.76.1",
                evidence,
            )

            self.assertEqual(expected_index, (evidence_root / "index.raw").read_bytes())
            self.assertEqual(
                hashlib.sha256(expected_index).hexdigest(),
                document["raw_index_sha256"],
            )
            self.assertEqual(
                ["linux/amd64", "linux/arm64"],
                document["platforms"],
            )
            for child in document["children"]:
                manifest_path = evidence_root / child["manifest_raw_file"]
                config_path = evidence_root / child["config_raw_file"]
                self.assertTrue(manifest_path.is_file())
                self.assertTrue(config_path.is_file())
                self.assertEqual(
                    child["manifest_raw_sha256"],
                    hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
                )
                self.assertEqual(
                    child["config_raw_sha256"],
                    hashlib.sha256(config_path.read_bytes()).hexdigest(),
                )

    def test_exact_provenance_labels_are_required_in_both_raw_child_configs(self):
        expected = self.validator.expected_provenance_labels(REPOSITORY, SOURCE_SHA, RELEASE_VERSION, CREATED)
        for mutation, expected_result in (
            ("none", "pass"),
            ("truncated-url-label", "provenance-label-mismatch"),
            ("missing-revision-label", "provenance-label-mismatch"),
            ("missing-created-label", "invalid-release-identity"),
            ("missing-artifact-created-label", "provenance-label-mismatch"),
            ("truncated-created-label", "invalid-release-identity"),
            ("different-child-created-label", "provenance-label-mismatch"),
            ("consistent-wrong-publisher-created", "provenance-label-mismatch"),
        ):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary_directory:
                capture_root = Path(temporary_directory)
                build_capture(capture_root, mutation)
                if expected_result == "pass":
                    evidence = self.validator.validate_capture(capture_root, expected)
                    self.assertTrue(all(child["provenance_labels"] == expected for child in evidence["children"]))
                else:
                    with self.assertRaises(self.validator.ValidationError) as context:
                        self.validator.validate_capture(capture_root, expected)
                    self.assertEqual(expected_result, context.exception.code)

    def test_legacy_three_argument_reader_still_requires_calendar_and_equal_created_labels(self):
        expected = self.validator.expected_provenance_labels(REPOSITORY, SOURCE_SHA, RELEASE_VERSION)
        for mutation in ["none", "missing-created-label", "missing-artifact-created-label", "truncated-created-label", "invalid-created-date", "different-child-created-pair"]:
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                build_capture(root, mutation)
                if mutation == "none":
                    evidence = self.validator.validate_capture(root, expected)
                    self.assertTrue(all(child["provenance_labels"]["org.opencontainers.image.created"] == CREATED for child in evidence["children"]))
                else:
                    with self.assertRaises(self.validator.ValidationError):
                        self.validator.validate_capture(root, expected)

    def test_observation_cli_keeps_created_label_refusals(self):
        for mutation in ("none", "missing-created-label", "missing-artifact-created-label",
                         "truncated-created-label", "invalid-created-date", "different-child-created-pair"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                build_capture(root, mutation)
                arguments = ["validator", "--image", "registry.hexalith.com/tenants:3.76.1",
                             "--repository", REPOSITORY, "--source-sha", SOURCE_SHA,
                             "--version", RELEASE_VERSION, "--observe-created", "--evidence-directory", ".hexalith/fixture"]
                with mock.patch.object(sys, "argv", arguments), mock.patch.object(
                    self.validator, "validate_registry", side_effect=lambda image, user, key, expected: self.validator.validate_capture(root, expected)
                ), mock.patch.object(self.validator, "write_evidence", return_value={
                    "index_digest": "sha256:" + "a" * 64, "platforms": ["linux/amd64", "linux/arm64"]
                }):
                    self.assertEqual(self.validator.main(), 0 if mutation == "none" else 1)

    def test_cli_cannot_replace_an_explicit_expected_instant_with_observation(self):
        arguments = ["validator", "--image", "registry.hexalith.com/tenants:3.76.1",
                     "--repository", REPOSITORY, "--source-sha", SOURCE_SHA, "--version", RELEASE_VERSION,
                     "--created", CREATED, "--observe-created", "--evidence-directory", ".hexalith/fixture"]
        with mock.patch.object(sys, "argv", arguments), self.assertRaises(SystemExit) as rejected:
            self.validator.main()
        self.assertEqual(rejected.exception.code, 2)

    def test_created_identity_rejects_impossible_or_noncanonical_instants(self):
        for created in ["2023-02-29T23:59:59Z", "1900-02-29T00:00:00Z", "2024-04-31T00:00:00Z",
                        "0000-01-01T00:00:00Z", "2024-01-01T24:00:00Z", "2024-01-01T00:00:00+00:00", ""]:
            with self.subTest(created=created), self.assertRaises(self.validator.ValidationError):
                self.validator.expected_provenance_labels(REPOSITORY, SOURCE_SHA, RELEASE_VERSION, created)
        for created in [CREATED, "2000-02-29T00:00:00Z", "2026-04-30T12:34:56Z"]:
            self.assertEqual(self.validator.expected_provenance_labels(REPOSITORY, SOURCE_SHA, RELEASE_VERSION, created)
                             ["org.opencontainers.image.created"], created)


if __name__ == "__main__":
    unittest.main()
