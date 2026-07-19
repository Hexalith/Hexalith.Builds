#!/usr/bin/env python3
"""Validate release-owner authority and destination uniqueness before publishing."""

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
from datetime import datetime, timezone
from pathlib import Path

from oci_registry_validator import (
    SafeRedirectHandler,
    workspace_input_directory,
    workspace_input_file,
    workspace_make_directory,
    workspace_output_directory,
    workspace_read_bytes,
    workspace_read_text,
    workspace_write_bytes,
    workspace_write_text,
)


AUTHORITY_SCHEMA = "hexalith.release-publication-authority.v1"
REQUIRED_OWNER_ROLE = "EventStore release owner"
REQUIRED_PLATFORMS = ["linux/amd64", "linux/arm64"]
REQUIRED_CONTRACT_FILES = (
    "publish-containers.sh",
    "oci_registry_validator.py",
    "publication_authority.py",
    "smoke-container-platforms.sh",
    "smoke_container_platforms.py",
)
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
SEMVER_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", re.ASCII)
UTC_OFFSET = "+00:00"


URL_OPENER = urllib.request.build_opener(SafeRedirectHandler())


class AuthorityError(Exception):
    """A deterministic, support-safe authority preflight failure."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def _fail(code, message):
    raise AuthorityError(code, message)


def _sha256_bytes(value):
    return hashlib.sha256(value).hexdigest()


def _parse_json_unique(raw):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                _fail("malformed-authority", "Authority record contains a duplicate JSON key.")
            result[key] = value
        return result

    try:
        value = json.loads(raw, object_pairs_hook=unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise AuthorityError("malformed-authority", "Authority record is not valid JSON.") from error
    if not isinstance(value, dict):
        _fail("malformed-authority", "Authority record must be a JSON object.")
    return value


def _parse_timestamp(value, field):
    if not isinstance(value, str) or not value.endswith("Z"):
        _fail("malformed-authority", f"Authority {field} must be a UTC timestamp.")
    try:
        parsed = datetime.fromisoformat(value[:-1] + UTC_OFFSET)
    except ValueError as error:
        raise AuthorityError("malformed-authority", f"Authority {field} is invalid.") from error
    return parsed


def _require_nonblank(value, code, field):
    if not isinstance(value, str) or not value.strip():
        _fail(code, f"Authority {field} must be nonblank.")
    return value


def _validate_release_identity(authority, expected):
    if authority.get("schema") != AUTHORITY_SCHEMA or authority.get("decision") != "authorized":
        _fail("malformed-authority", "Authority schema or decision is invalid.")
    if authority.get("repository") != expected["repository"]:
        _fail("repository-mismatch", "Authority repository does not match the release repository.")
    if authority.get("version") != expected["version"]:
        _fail("version-mismatch", "Authority version does not match the proposed release.")
    if authority.get("source_sha") != expected["source_sha"]:
        _fail("source-mismatch", "Authority source SHA does not match the release source.")
    if authority.get("container_repository") != expected["container_repository"]:
        _fail("container-repository-mismatch", "Authority container repository does not match.")
    if authority.get("durable_source") != expected.get("durable_source"):
        _fail("durable-source-mismatch", "Authority durable source does not match the fetched record.")
    if authority.get("platforms") != expected["platforms"] or authority.get("platforms") != REQUIRED_PLATFORMS:
        _fail("platforms-mismatch", "Authority platform set does not match the exact release contract.")

    builds_sha = expected["builds_execution_sha"]
    if not isinstance(builds_sha, str) or SHA_PATTERN.fullmatch(builds_sha) is None:
        _fail("builds-identity-mismatch", "Expected Builds execution SHA is invalid.")
    return builds_sha


def _validate_builds_identity(authority, expected, builds_sha):
    builds = authority.get("builds")
    if not isinstance(builds, dict):
        _fail("builds-identity-mismatch", "Authority Builds identity is missing.")
    if builds.get("workflow_sha") != builds_sha or builds.get("action_sha") != builds_sha:
        _fail("builds-identity-mismatch", "Workflow and action must bind the same approved Builds SHA.")
    approved_files = builds.get("files")
    if not isinstance(approved_files, dict) or set(approved_files) != set(REQUIRED_CONTRACT_FILES):
        _fail("approved-file-mismatch", "Authority contract-file inventory is not exact.")
    actual_hashes = {}
    for name in REQUIRED_CONTRACT_FILES:
        path = expected["files"].get(name)
        if not isinstance(path, Path) or not path.is_file():
            _fail("approved-file-mismatch", "An approved contract file is unavailable.")
        actual_hash = _sha256_bytes(path.read_bytes())
        approved_hash = approved_files.get(name)
        if not isinstance(approved_hash, str) or SHA256_PATTERN.fullmatch(approved_hash) is None:
            _fail("approved-file-mismatch", "An approved contract-file hash is invalid.")
        if actual_hash != approved_hash:
            _fail("approved-file-mismatch", "Installed contract bytes do not match authority.")
        actual_hashes[name] = actual_hash
    return actual_hashes


def _validate_owner_window(authority, checked_at):
    owner = authority.get("owner")
    if not isinstance(owner, dict) or owner.get("role") != REQUIRED_OWNER_ROLE:
        _fail("wrong-owner-role", "Authority owner role is not EventStore release owner.")
    owner_name = _require_nonblank(owner.get("name"), "wrong-owner-role", "owner name")
    _require_nonblank(authority.get("rationale"), "malformed-authority", "rationale")
    authorized_at = _parse_timestamp(authority.get("authorized_at"), "authorized_at")
    expires_at = _parse_timestamp(authority.get("expires_at"), "expires_at")
    if authorized_at > checked_at:
        _fail("authority-not-yet-valid", "Authority is not valid at the action-time timestamp.")
    if checked_at >= expires_at or expires_at <= authorized_at:
        _fail("expired-authority", "Authority is expired at the action-time timestamp.")
    return owner_name


def validate_authority_bytes(raw, expected, checked_at):
    """Validate immutable authority bytes against the exact release contract."""

    if checked_at.tzinfo is None or checked_at.utcoffset() is None:
        _fail("invalid-checked-at", "Authority checked-at timestamp must be timezone-aware.")
    checked_at = checked_at.astimezone(timezone.utc)
    authority = _parse_json_unique(raw)
    builds_sha = _validate_release_identity(authority, expected)
    actual_hashes = _validate_builds_identity(authority, expected, builds_sha)
    owner_name = _validate_owner_window(authority, checked_at)

    checked_at_text = checked_at.isoformat().replace(UTC_OFFSET, "Z")
    return {
        "result": "pass",
        "authority_sha256": _sha256_bytes(raw),
        "checked_at": checked_at_text,
        "repository": expected["repository"],
        "version": expected["version"],
        "source_sha": expected["source_sha"],
        "container_repository": expected["container_repository"],
        "platforms": list(REQUIRED_PLATFORMS),
        "builds_execution_sha": builds_sha,
        "contract_file_sha256": actual_hashes,
        "owner": owner_name,
        "authorized_at": authority["authorized_at"],
        "expires_at": authority["expires_at"],
    }


def validate_destination_absence(package_ids, version, container_repository, probe):
    """Require exactly 14 new package IDs and one new container tag to be absent."""

    if len(package_ids) != 14 or len(set(package_ids)) != 14:
        _fail("package-inventory-mismatch", "Release package inventory must contain exactly 14 unique IDs.")
    if not isinstance(version, str) or SEMVER_PATTERN.fullmatch(version) is None:
        _fail("invalid-version", "Proposed release version is invalid.")
    checked = []
    for package_id in package_ids:
        status = probe("nuget", package_id, version)
        if status == 200:
            _fail("version-collision", "A proposed NuGet package version already exists.")
        if status != 404:
            _fail("destination-probe-failure", "NuGet destination absence could not be proved.")
        checked.append(package_id)
    status = probe("container", container_repository, version)
    if status == 200:
        _fail("version-collision", "The proposed container tag already exists.")
    if status != 404:
        _fail("destination-probe-failure", "Container destination absence could not be proved.")
    return {
        "result": "pass",
        "version": version,
        "package_count": len(checked),
        "container_repository": container_repository,
    }


def _http_status(request):
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            response.read(1)
            return response.status
    except urllib.error.HTTPError as error:
        return error.code
    except (urllib.error.URLError, TimeoutError) as error:
        raise AuthorityError(
            "destination-probe-failure",
            "Destination absence could not be proved.",
        ) from error


def destination_probe(username, api_key):
    """Create a read-only NuGet/registry destination probe."""

    def probe(kind, identity, version):
        if kind == "nuget":
            package = urllib.parse.quote(identity.lower(), safe="")
            package_version = urllib.parse.quote(version.lower(), safe="")
            url = (
                f"https://api.nuget.org/v3-flatcontainer/{package}/{package_version}/"
                f"{package}.{package_version}.nupkg"
            )
            request = urllib.request.Request(url, method="HEAD")
            return _http_status(request)
        registry, separator, repository = identity.partition("/")
        if not separator or not registry or not repository or not username or not api_key:
            _fail("destination-probe-failure", "Registry destination probe is not configured.")
        credentials = base64.b64encode(f"{username}:{api_key}".encode("utf-8")).decode("ascii")
        repository_path = urllib.parse.quote(repository, safe="/")
        tag = urllib.parse.quote(version, safe="")
        request = urllib.request.Request(
            f"https://{registry}/v2/{repository_path}/manifests/{tag}",
            headers={
                "Accept": "application/vnd.oci.image.index.v1+json",
                "Authorization": f"Basic {credentials}",
            },
            method="HEAD",
        )
        return _http_status(request)

    return probe


def _load_authority_url(url, github_token):
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "https" or not parsed.netloc:
        _fail("invalid-authority-source", "Authority source must be an HTTPS URL.")
    headers = {"Accept": "application/json"}
    if github_token:
        headers["Authorization"] = f"Bearer {github_token}"
    request = urllib.request.Request(url, headers=headers, method="GET")
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            raw = response.read(131073)
            if len(raw) > 131072:
                _fail("malformed-authority", "Authority record exceeds the size limit.")
            metadata = {
                "source_url": url,
                "retrieved_at": datetime.now(timezone.utc).isoformat().replace(UTC_OFFSET, "Z"),
                "etag": response.headers.get("ETag"),
                "last_modified": response.headers.get("Last-Modified"),
            }
            return raw, metadata
    except (urllib.error.URLError, TimeoutError) as error:
        raise AuthorityError("authority-source-unavailable", "Authority source could not be loaded.") from error


def _load_package_ids(path):
    try:
        manifest = json.loads(workspace_read_text(Path(path)))
        packages = manifest["packages"]
        package_ids = [item["id"] for item in packages]
    except (OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise AuthorityError("package-inventory-mismatch", "Package manifest is invalid.") from error
    return package_ids


def _write_evidence(directory, authority_raw, metadata, authority_evidence, destination_evidence):
    directory = Path(directory)
    workspace_make_directory(directory)
    authority_path = directory / "release-owner-publication-authority.json"
    metadata_path = directory / "release-owner-publication-authority.source.json"
    checked_at_path = directory / "release-owner-publication-authority.checked-at.txt"
    workspace_write_bytes(authority_path, authority_raw)
    workspace_write_text(metadata_path, json.dumps(metadata, indent=2, sort_keys=True) + "\n")
    workspace_write_text(checked_at_path, authority_evidence["checked_at"] + "\n")
    evidence = {
        "result": "pass",
        "authority": authority_evidence,
        "destinations": destination_evidence,
        "files": {
            authority_path.name: _sha256_bytes(workspace_read_bytes(authority_path)),
            metadata_path.name: _sha256_bytes(workspace_read_bytes(metadata_path)),
            checked_at_path.name: _sha256_bytes(workspace_read_bytes(checked_at_path)),
        },
    }
    workspace_write_text(
        directory / "publication-preflight.json",
        json.dumps(evidence, indent=2, sort_keys=True) + "\n",
    )


def main():
    parser = argparse.ArgumentParser(description="Validate release publication authority.")
    parser.add_argument("--authority-url", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--container-repository", required=True)
    parser.add_argument("--builds-execution-sha", required=True)
    parser.add_argument("--package-manifest", required=True, type=workspace_input_file)
    parser.add_argument("--contract-directory", required=True, type=workspace_input_directory)
    parser.add_argument("--evidence-directory", required=True, type=workspace_output_directory)
    arguments = parser.parse_args()
    checked_at = datetime.now(timezone.utc)
    contract_directory = arguments.contract_directory
    expected = {
        "repository": arguments.repository,
        "version": arguments.version,
        "source_sha": arguments.source_sha,
        "container_repository": arguments.container_repository,
        "durable_source": arguments.authority_url,
        "platforms": list(REQUIRED_PLATFORMS),
        "builds_execution_sha": arguments.builds_execution_sha,
        "files": {name: contract_directory / name for name in REQUIRED_CONTRACT_FILES},
    }
    try:
        authority_raw, metadata = _load_authority_url(
            arguments.authority_url,
            os.environ.get("GITHUB_TOKEN", ""),
        )
        _ = validate_authority_bytes(authority_raw, expected, checked_at)
        package_ids = _load_package_ids(arguments.package_manifest)
        destination_evidence = validate_destination_absence(
            package_ids,
            arguments.version,
            arguments.container_repository,
            destination_probe(
                os.environ.get("HEXALITH_ZOT_USERNAME", ""),
                os.environ.get("HEXALITH_ZOT_API_KEY", ""),
            ),
        )
        authority_evidence = validate_authority_bytes(
            authority_raw,
            expected,
            datetime.now(timezone.utc),
        )
        _write_evidence(
            arguments.evidence_directory,
            authority_raw,
            metadata,
            authority_evidence,
            destination_evidence,
        )
    except AuthorityError as error:
        print(f"[publication-authority] {error.code}: {error}", file=sys.stderr)
        return 1
    print(
        f"[publication-authority] pass: {arguments.repository} {arguments.version} "
        f"at {authority_evidence['checked_at']}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
