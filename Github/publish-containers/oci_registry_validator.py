#!/usr/bin/env python3
"""Fail-closed validation for the shared two-platform OCI publisher."""

import argparse
import base64
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


OCI_INDEX_MEDIA_TYPE = "application/vnd.oci.image.index.v1+json"
OCI_MANIFEST_MEDIA_TYPE = "application/vnd.oci.image.manifest.v1+json"
DOCKER_MANIFEST_MEDIA_TYPE = "application/vnd.docker.distribution.manifest.v2+json"
OCI_CONFIG_MEDIA_TYPE = "application/vnd.oci.image.config.v1+json"
DOCKER_CONFIG_MEDIA_TYPE = "application/vnd.docker.container.image.v1+json"
CHILD_MANIFEST_MEDIA_TYPES = {OCI_MANIFEST_MEDIA_TYPE, DOCKER_MANIFEST_MEDIA_TYPE}
CONFIG_MEDIA_TYPES = {OCI_CONFIG_MEDIA_TYPE, DOCKER_CONFIG_MEDIA_TYPE}
REQUIRED_PLATFORMS = ("linux/amd64", "linux/arm64")
MANIFEST_ACCEPT = ", ".join(
    (
        OCI_INDEX_MEDIA_TYPE,
        OCI_MANIFEST_MEDIA_TYPE,
        DOCKER_MANIFEST_MEDIA_TYPE,
        "application/vnd.docker.distribution.manifest.list.v2+json",
    )
)
SHA256_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
REGISTRY_PATTERN = re.compile(r"^[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[1-9]\d{0,4})?$", re.ASCII)
REPOSITORY_PATTERN = re.compile(r"^[a-z0-9]+(?:[._/-][a-z0-9]+)*$")
TAG_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", re.ASCII)
REGISTRY_OBJECT_UNRESOLVED = "Registry object could not be resolved."


def _origin(url):
    parsed = urllib.parse.urlsplit(url)
    port = parsed.port
    if port is None:
        port = 443 if parsed.scheme.lower() == "https" else 80
    return parsed.scheme.lower(), (parsed.hostname or "").lower(), port


class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):  # noqa: D203
    """Follow redirects without forwarding credentials to another origin."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        if urllib.parse.urlsplit(request.full_url).scheme.lower() == "https" and urllib.parse.urlsplit(
            new_url
        ).scheme.lower() != "https":
            raise urllib.error.HTTPError(
                new_url,
                code,
                "HTTPS downgrade redirect rejected.",
                headers,
                file_pointer,
            )
        redirected = super().redirect_request(
            request,
            file_pointer,
            code,
            message,
            headers,
            new_url,
        )
        if redirected is not None and _origin(request.full_url) != _origin(new_url):
            redirected.remove_header("Authorization")
        return redirected


URL_OPENER = urllib.request.build_opener(SafeRedirectHandler())


class ValidationError(Exception):

    """A deterministic, support-safe OCI validation failure."""

    def __init__(self, code, message):
        """Initialize a categorized validation failure."""
        super().__init__(message)
        self.code = code


def _fail(code, message):
    raise ValidationError(code, message)


def _media_type(value):
    if not isinstance(value, str):
        return ""
    return value.split(";", 1)[0].strip()


def _sha256_digest(body):
    return f"sha256:{hashlib.sha256(body).hexdigest()}"


def _parse_json(body, code, subject):
    try:
        value = json.loads(body)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValidationError(code, f"{subject} is not valid JSON.") from error
    if not isinstance(value, dict):
        _fail(code, f"{subject} must be a JSON object.")
    return value


def _workspace_path(value, *, must_exist, expected_kind):
    if not isinstance(value, str) or not value or "\0" in value:
        raise argparse.ArgumentTypeError("Workspace path must be a nonblank string.")
    try:
        workspace = Path.cwd().resolve(strict=True)
        candidate = Path(value)
        if not candidate.is_absolute():
            candidate = workspace / candidate
        resolved = candidate.resolve(strict=must_exist)
    except OSError as error:
        raise argparse.ArgumentTypeError("Workspace path could not be resolved.") from error
    if resolved == workspace or not resolved.is_relative_to(workspace):
        raise argparse.ArgumentTypeError("Path must resolve below the current workspace.")
    if expected_kind == "file" and not resolved.is_file():
        raise argparse.ArgumentTypeError("Workspace file does not exist.")
    if expected_kind == "directory" and not resolved.is_dir():
        raise argparse.ArgumentTypeError("Workspace directory does not exist.")
    return resolved


def workspace_input_file(value):
    """Resolve an existing CLI file below the current workspace."""
    return _workspace_path(value, must_exist=True, expected_kind="file")


def workspace_input_directory(value):
    """Resolve an existing CLI directory below the current workspace."""
    return _workspace_path(value, must_exist=True, expected_kind="directory")


def workspace_output_directory(value):
    """Resolve a CLI output directory below the current workspace."""
    return _workspace_path(value, must_exist=False, expected_kind="output")


def workspace_make_directory(path):
    """Create a directory after its CLI path has been workspace-confined."""
    path.mkdir(parents=True, exist_ok=True)  # NOSONAR -- canonicalized below cwd.


def workspace_path_exists(path):
    """Check a path after the owning CLI path has been workspace-confined."""
    return path.exists()  # NOSONAR -- canonicalized below cwd.


def workspace_read_bytes(path):
    """Read bytes after the owning CLI path has been workspace-confined."""
    return path.read_bytes()  # NOSONAR -- canonicalized below cwd.


def workspace_read_text(path):
    """Read text after the owning CLI path has been workspace-confined."""
    return path.read_text(encoding="utf-8")  # NOSONAR -- canonicalized below cwd.


def workspace_write_bytes(path, value):
    """Write bytes after the owning CLI path has been workspace-confined."""
    path.write_bytes(value)  # NOSONAR -- canonicalized below cwd.


def workspace_write_text(path, value):
    """Write text after the owning CLI path has been workspace-confined."""
    path.write_text(value, encoding="utf-8")  # NOSONAR -- canonicalized below cwd.


def _read_body(capture_root, response, unresolved_code):
    if not isinstance(response, dict):
        _fail(unresolved_code, REGISTRY_OBJECT_UNRESOLVED)
    body_name = response.get("body")
    if not isinstance(body_name, str) or not body_name:
        _fail("malformed-response", "Registry capture response has no body path.")
    try:
        return (capture_root / body_name).read_bytes()
    except OSError as error:
        raise ValidationError(unresolved_code, REGISTRY_OBJECT_UNRESOLVED) from error


def _validate_digest(value, code="malformed-descriptor"):
    if not isinstance(value, str) or SHA256_PATTERN.fullmatch(value) is None:
        _fail(code, "Descriptor digest is not a lowercase SHA-256 value.")
    return value


def _validate_size(value, code="malformed-descriptor"):
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        _fail(code, "Descriptor size must be a non-negative integer.")
    return value


def _validate_response_digest(response, body, expected_digest, code):
    if _sha256_digest(body) != expected_digest:
        _fail(code, "Registry response bytes do not match the expected digest.")
    response_digest = response.get("docker_content_digest")
    if response_digest not in (None, "", expected_digest):
        _fail(code, "Registry response digest header does not match the expected digest.")


def _validate_index(tag_response, immutable_response, tag_body, immutable_body):
    reported_digest = _validate_digest(
        tag_response.get("docker_content_digest"),
        "missing-registry-digest",
    )
    if _sha256_digest(tag_body) != reported_digest:
        _fail("tag-digest-body-mismatch", "Tag bytes do not match the registry digest.")
    if immutable_body != tag_body:
        _fail("tag-digest-body-mismatch", "Tag and immutable digest responses differ.")
    _validate_response_digest(
        immutable_response,
        immutable_body,
        reported_digest,
        "tag-digest-body-mismatch",
    )

    index = _parse_json(tag_body, "malformed-index", "OCI index")
    response_media_type = _media_type(tag_response.get("content_type"))
    immutable_media_type = _media_type(immutable_response.get("content_type"))
    manifest_media_type = index.get("mediaType")
    if response_media_type != manifest_media_type or immutable_media_type != manifest_media_type:
        _fail("content-type-mismatch", "Index response content type does not match its media type.")
    if manifest_media_type != OCI_INDEX_MEDIA_TYPE:
        _fail("wrong-index-media-type", "Tag does not resolve to an OCI image index.")
    if index.get("schemaVersion") != 2:
        _fail("wrong-schema-version", "OCI index schemaVersion must equal 2.")

    descriptors = index.get("manifests")
    if not isinstance(descriptors, list):
        _fail("malformed-index", "OCI index manifests must be an array.")
    return reported_digest, index, descriptors


def _descriptor_platform(descriptor):
    if not isinstance(descriptor, dict):
        _fail("malformed-descriptor", "Index descriptor must be an object.")
    _validate_digest(descriptor.get("digest"))
    _validate_size(descriptor.get("size"))
    if descriptor.get("mediaType") not in CHILD_MANIFEST_MEDIA_TYPES:
        _fail("unsupported-child-media-type", "Child descriptor media type is not recognized.")
    platform = descriptor.get("platform")
    if not isinstance(platform, dict):
        _fail("malformed-descriptor", "Child descriptor platform must be an object.")
    os_name = platform.get("os")
    architecture = platform.get("architecture")
    if not isinstance(os_name, str) or not os_name.strip():
        _fail("blank-platform", "Child descriptor OS is blank.")
    if not isinstance(architecture, str) or not architecture.strip():
        _fail("blank-platform", "Child descriptor architecture is blank.")
    if os_name == "unknown" or architecture == "unknown":
        _fail("unknown-platform", "Unknown platform descriptors are not allowed.")
    if platform.get("variant") not in (None, ""):
        _fail("variant-not-allowed", "Platform variants are not allowed.")
    return f"{os_name}/{architecture}"


def _validate_platforms(descriptors):
    platforms = [_descriptor_platform(descriptor) for descriptor in descriptors]

    if len(set(platforms)) != len(platforms):
        _fail("duplicate-platform", "Duplicate platform descriptors are not allowed.")
    if len(platforms) != len(REQUIRED_PLATFORMS) or set(platforms) != set(REQUIRED_PLATFORMS):
        _fail("platform-set-mismatch", "OCI index platform set is not exactly linux/amd64 and linux/arm64.")
    return platforms


def _validate_child(descriptor, response, body, config_resolver):
    child_digest = descriptor["digest"]
    _validate_response_digest(response, body, child_digest, "child-digest-mismatch")
    if len(body) != descriptor["size"]:
        _fail("child-size-mismatch", "Child manifest byte length does not match its descriptor.")

    descriptor_media_type = descriptor["mediaType"]
    response_media_type = _media_type(response.get("content_type"))
    if response_media_type != descriptor_media_type:
        _fail("child-content-type-mismatch", "Child response content type does not match its descriptor.")
    child = _parse_json(body, "malformed-child-manifest", "Child manifest")
    if child.get("schemaVersion") != 2:
        _fail("wrong-child-schema-version", "Child manifest schemaVersion must equal 2.")
    if child.get("mediaType") != descriptor_media_type:
        _fail("child-media-type-mismatch", "Child manifest media type does not match its descriptor.")

    config_descriptor = child.get("config")
    if not isinstance(config_descriptor, dict):
        _fail("malformed-config-descriptor", "Child config descriptor must be an object.")
    config_digest = _validate_digest(config_descriptor.get("digest"), "malformed-config-descriptor")
    config_size = _validate_size(config_descriptor.get("size"), "malformed-config-descriptor")
    config_media_type = config_descriptor.get("mediaType")
    if config_media_type not in CONFIG_MEDIA_TYPES:
        _fail("unsupported-config-media-type", "Config descriptor media type is not recognized.")

    config_response, config_body = config_resolver(config_digest)
    _validate_response_digest(config_response, config_body, config_digest, "config-digest-mismatch")
    if len(config_body) != config_size:
        _fail("config-size-mismatch", "Config byte length does not match its descriptor.")
    config = _parse_json(config_body, "malformed-config", "Image config")
    platform = descriptor["platform"]
    if config.get("os") != platform["os"] or config.get("architecture") != platform["architecture"]:
        _fail("config-platform-mismatch", "Image config platform does not match its descriptor.")

    return {
        "platform": f"{platform['os']}/{platform['architecture']}",
        "digest": child_digest,
        "size": descriptor["size"],
        "media_type": descriptor_media_type,
        "config_digest": config_digest,
        "config_size": config_size,
        "config_media_type": config_media_type,
        "manifest_bytes": body,
        "config_bytes": config_body,
    }


def _validate_graph(tag_response, immutable_resolver, child_resolver, config_resolver):
    tag_body = tag_response["body_bytes"]
    reported_digest = _validate_digest(
        tag_response.get("docker_content_digest"),
        "missing-registry-digest",
    )
    immutable_response = immutable_resolver(reported_digest)
    immutable_body = immutable_response["body_bytes"]
    index_digest, _, descriptors = _validate_index(
        tag_response,
        immutable_response,
        tag_body,
        immutable_body,
    )
    _validate_platforms(descriptors)
    children = []
    for descriptor in descriptors:
        child_response = child_resolver(descriptor["digest"])
        children.append(
            _validate_child(
                descriptor,
                child_response,
                child_response["body_bytes"],
                config_resolver,
            )
        )
    order = {platform: index for index, platform in enumerate(REQUIRED_PLATFORMS)}
    children.sort(key=lambda child: order[child["platform"]])
    return {
        "result": "pass",
        "index_digest": index_digest,
        "raw_index_sha256": hashlib.sha256(tag_body).hexdigest(),
        "index_size": len(tag_body),
        "media_type": OCI_INDEX_MEDIA_TYPE,
        "platforms": list(REQUIRED_PLATFORMS),
        "children": children,
        "index_bytes": tag_body,
    }


def validate_capture(capture_root):
    """Validate a deterministic registry capture rooted at *capture_root*."""

    capture_root = Path(capture_root)
    try:
        capture = json.loads((capture_root / "responses.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValidationError("malformed-capture", "Registry capture metadata is invalid.") from error
    tag = capture.get("tag")
    objects = capture.get("objects")
    if not isinstance(tag, dict) or not isinstance(objects, dict):
        _fail("malformed-capture", "Registry capture is missing tag or object responses.")

    def captured_response(response, unresolved_code):
        body = _read_body(capture_root, response, unresolved_code)
        return {
            "content_type": response.get("content_type"),
            "docker_content_digest": response.get("docker_content_digest"),
            "body_bytes": body,
        }

    tag_response = captured_response(tag, "unresolved-tag")

    def resolve_object(reference, unresolved_code):
        return captured_response(objects.get(reference), unresolved_code)

    return _validate_graph(
        tag_response,
        lambda reference: resolve_object(reference, "unresolved-index"),
        lambda reference: resolve_object(reference, "unresolved-child"),
        lambda reference: (
            (response := resolve_object(reference, "unresolved-config")),
            response["body_bytes"],
        ),
    )


class RegistryClient:  # noqa: D203
    """Minimal Docker Registry HTTP API client that keeps response bytes untouched."""

    def __init__(self, registry, repository, username, api_key):
        """Initialize a client for one validated registry repository."""
        repository_path = urllib.parse.quote(repository, safe="/")
        self._base_url = f"https://{registry}/v2/{repository_path}"
        credentials = base64.b64encode(f"{username}:{api_key}".encode("utf-8")).decode("ascii")
        self._authorization = f"Basic {credentials}"

    def _get(self, url, accept, unresolved_code):
        request = urllib.request.Request(
            url,
            headers={"Accept": accept, "Authorization": self._authorization},
            method="GET",
        )
        try:
            with URL_OPENER.open(request, timeout=30) as response:
                return {
                    "content_type": response.headers.get("Content-Type", ""),
                    "docker_content_digest": response.headers.get("Docker-Content-Digest"),
                    "body_bytes": response.read(),
                }
        except (urllib.error.URLError, TimeoutError) as error:
            raise ValidationError(unresolved_code, REGISTRY_OBJECT_UNRESOLVED) from error

    def manifest(self, reference, unresolved_code):
        encoded_reference = urllib.parse.quote(reference, safe=":")
        return self._get(f"{self._base_url}/manifests/{encoded_reference}", MANIFEST_ACCEPT, unresolved_code)

    def blob(self, digest_value):
        encoded_digest = urllib.parse.quote(digest_value, safe=":")
        return self._get(f"{self._base_url}/blobs/{encoded_digest}", "application/octet-stream", "unresolved-config")


def _parse_image(image):
    if not isinstance(image, str) or "/" not in image or len(image) > 255:
        _fail("invalid-image-reference", "Image must include registry, repository, and tag.")
    registry, remainder = image.split("/", 1)
    repository, separator, tag = remainder.rpartition(":")
    if (
        not separator
        or REGISTRY_PATTERN.fullmatch(registry) is None
        or REPOSITORY_PATTERN.fullmatch(repository) is None
        or TAG_PATTERN.fullmatch(tag) is None
    ):
        _fail("invalid-image-reference", "Image must use a mutable tag for initial resolution.")
    return registry, repository, tag


def validated_image_reference(value):
    """Validate a CLI image reference without changing its value."""
    _parse_image(value)
    return value


def validate_registry(image, username, api_key):
    """Validate a registry tag and all immutable descendants."""
    registry, repository, tag = _parse_image(image)
    if not username or not api_key:
        _fail("registry-authentication-missing", "Registry credentials are required.")
    client = RegistryClient(registry, repository, username, api_key)
    tag_response = client.manifest(tag, "unresolved-tag")
    return _validate_graph(
        tag_response,
        lambda reference: client.manifest(reference, "unresolved-index"),
        lambda reference: client.manifest(reference, "unresolved-child"),
        lambda reference: (
            (response := client.blob(reference)),
            response["body_bytes"],
        ),
    )


def write_evidence(evidence_directory, image, evidence):
    """Persist support-safe immutable validation evidence."""
    directory = Path(evidence_directory)
    workspace_make_directory(directory)
    index_bytes = evidence["index_bytes"]
    workspace_write_bytes(directory / "index.raw", index_bytes)
    document = {key: value for key, value in evidence.items() if key not in {"children", "index_bytes"}}
    children = []
    for child in evidence["children"]:
        child_document = {
            key: value
            for key, value in child.items()
            if key not in {"manifest_bytes", "config_bytes"}
        }
        architecture = child["platform"].split("/", 1)[1]
        manifest_name = f"child-linux-{architecture}.manifest.raw"
        config_name = f"child-linux-{architecture}.config.raw"
        workspace_write_bytes(directory / manifest_name, child["manifest_bytes"])
        workspace_write_bytes(directory / config_name, child["config_bytes"])
        child_document.update(
            {
                "manifest_raw_file": manifest_name,
                "manifest_raw_sha256": hashlib.sha256(child["manifest_bytes"]).hexdigest(),
                "config_raw_file": config_name,
                "config_raw_sha256": hashlib.sha256(child["config_bytes"]).hexdigest(),
            }
        )
        children.append(child_document)
    document["children"] = children
    document["image"] = image
    document["raw_index_file"] = "index.raw"
    workspace_write_text(
        directory / "oci-validation.json",
        json.dumps(document, indent=2, sort_keys=True) + "\n",
    )
    return document


def main():
    parser = argparse.ArgumentParser(description="Validate an exact two-platform OCI index.")
    parser.add_argument("--image", required=True, type=validated_image_reference)
    parser.add_argument("--evidence-directory", required=True, type=workspace_output_directory)
    arguments = parser.parse_args()
    try:
        evidence = validate_registry(
            arguments.image,
            os.environ.get("HEXALITH_ZOT_USERNAME", ""),
            os.environ.get("HEXALITH_ZOT_API_KEY", ""),
        )
        document = write_evidence(arguments.evidence_directory, arguments.image, evidence)
    except ValidationError as error:
        print(f"[oci-registry-validator] {error.code}: {error}", file=sys.stderr)
        return 1
    print(
        f"[oci-registry-validator] pass: {document['index_digest']} "
        f"({','.join(document['platforms'])})"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
