#!/usr/bin/env python3
"""Validate immutable OCI registry response captures for the shared publisher."""

import hashlib
import json
from pathlib import Path


OCI_INDEX_MEDIA_TYPE = "application/vnd.oci.image.index.v1+json"


class ValidationError(Exception):
    """A deterministic, support-safe OCI validation failure."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def _read_body(capture_root, response):
    body_name = response.get("body")
    if not isinstance(body_name, str) or not body_name:
        raise ValidationError("malformed-response", "Registry capture response has no body path.")
    body_path = capture_root / body_name
    try:
        return body_path.read_bytes()
    except OSError as error:
        raise ValidationError("unresolved-object", "Registry capture body could not be resolved.") from error


def _sha256_digest(body):
    return f"sha256:{hashlib.sha256(body).hexdigest()}"


def validate_capture(capture_root):
    """Validate a deterministic registry capture rooted at *capture_root*."""

    capture_root = Path(capture_root)
    try:
        capture = json.loads((capture_root / "responses.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValidationError("malformed-capture", "Registry capture metadata is invalid.") from error

    tag_response = capture.get("tag")
    objects = capture.get("objects")
    if not isinstance(tag_response, dict) or not isinstance(objects, dict):
        raise ValidationError("malformed-capture", "Registry capture is missing tag or object responses.")

    tag_body = _read_body(capture_root, tag_response)
    reported_digest = tag_response.get("docker_content_digest")
    if reported_digest != _sha256_digest(tag_body):
        raise ValidationError("tag-digest-body-mismatch", "Tag bytes do not match the registry digest.")

    try:
        manifest = json.loads(tag_body)
    except json.JSONDecodeError as error:
        raise ValidationError("malformed-index", "Tag response is not valid JSON.") from error

    response_media_type = tag_response.get("content_type")
    manifest_media_type = manifest.get("mediaType")
    if response_media_type != manifest_media_type:
        raise ValidationError("content-type-mismatch", "Tag content type does not match its manifest media type.")
    if manifest_media_type != OCI_INDEX_MEDIA_TYPE:
        raise ValidationError("wrong-index-media-type", "Tag does not resolve to an OCI image index.")

    return {"platforms": []}
