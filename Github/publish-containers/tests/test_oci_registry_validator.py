import hashlib
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
REPOSITORY_ROOT = SCRIPT_DIRECTORY.parents[1]
FIXTURE_ROOT = REPOSITORY_ROOT / "test" / "fixtures" / "publish-containers"
VALIDATOR_PATH = SCRIPT_DIRECTORY / "oci_registry_validator.py"


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
        config = {"architecture": architecture, "os": os_name}
        if mutation == "child-config-mismatch" and architecture == "amd64":
            config["architecture"] = "arm64"
        config_body = compact_json(config)
        config_digest = digest(config_body)
        config_name = write_body(root, f"{architecture}-config.json", config_body)
        objects[config_digest] = {"content_type": config_media_type, "body": config_name}

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
            "content_type": top_media_type,
            "docker_content_digest": tag_digest,
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

    def test_historical_single_manifest_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            capture_root = Path(temporary_directory)
            build_capture(capture_root, "single-manifest")
            with self.assertRaises(self.validator.ValidationError) as context:
                self.validator.validate_capture(capture_root)
            self.assertEqual("wrong-index-media-type", context.exception.code)

    @unittest.skipUnless(
        os.environ.get("HEXALITH_VALIDATE_OCI_FULL_MATRIX") == "true",
        "Task 4 full OCI validation matrix is not active yet.",
    )
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


if __name__ == "__main__":
    unittest.main()
