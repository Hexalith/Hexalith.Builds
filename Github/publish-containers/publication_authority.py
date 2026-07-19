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
    MANIFEST_ACCEPT,
    SafeRedirectHandler,
    _origin,
    workspace_input_directory,
    workspace_input_file,
    workspace_make_directory,
    workspace_output_directory,
    workspace_path_exists,
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
GITHUB_AUTHORITY_URL_PATTERN = re.compile(
    r"^https://api\.github\.com/repos/Hexalith/Hexalith\.EventStore/issues/comments/(?P<comment_id>\d+)$",
    re.ASCII,
)
GITHUB_API_ORIGIN = ("https", "api.github.com", 443)
GITHUB_LOGIN_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", re.ASCII)


URL_OPENER = urllib.request.build_opener(SafeRedirectHandler())


class AuthorityError(Exception):  # noqa: D203
    """A deterministic, support-safe authority preflight failure."""

    def __init__(self, code, message):
        """Initialize a categorized authority failure."""
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


def _validate_owner_window(authority, expected, checked_at):
    owner = authority.get("owner")
    if not isinstance(owner, dict) or owner.get("role") != REQUIRED_OWNER_ROLE:
        _fail("wrong-owner-role", "Authority owner role is not EventStore release owner.")
    owner_name = _require_nonblank(owner.get("name"), "wrong-owner-role", "owner name")
    if owner_name != expected.get("owner_name"):
        _fail("wrong-owner", "Authority owner is not an approved EventStore release owner.")
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
    owner_name = _validate_owner_window(authority, expected, checked_at)

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


def validate_container_absence(version, container_repository, probe):
    """Require the exact container version tag to remain absent."""
    if not isinstance(version, str) or SEMVER_PATTERN.fullmatch(version) is None:
        _fail("invalid-version", "Proposed release version is invalid.")
    status = probe("container", container_repository, version)
    if status == 200:
        _fail("version-collision", "The proposed container tag already exists.")
    if status != 404:
        _fail("destination-probe-failure", "Container destination absence could not be proved.")
    return {
        "result": "pass",
        "version": version,
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
                "Accept": MANIFEST_ACCEPT,
                "Authorization": f"Basic {credentials}",
            },
            method="HEAD",
        )
        return _http_status(request)

    return probe


def _load_role_allowlist(path, repository):
    try:
        document = json.loads(workspace_read_text(Path(path)))
        owners = document["roles"]["release_owner"]
    except (OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise AuthorityError("owner-allowlist-invalid", "Release-owner allowlist is invalid.") from error
    if (
        document.get("schema") != "hexalith.eventstore.github-approval-role-allowlist/v1"
        or document.get("repository") != repository
        or not isinstance(owners, list)
        or not owners
        or len(owners) != len(set(owners))
        or any(not isinstance(owner, str) or GITHUB_LOGIN_PATTERN.fullmatch(owner) is None for owner in owners)
    ):
        _fail("owner-allowlist-invalid", "Release-owner allowlist is invalid.")
    return set(owners)


def _load_authority_url(url, github_token, allowed_owners):
    source_match = GITHUB_AUTHORITY_URL_PATTERN.fullmatch(url)
    if source_match is None:
        _fail("invalid-authority-source", "Authority source must be an EventStore GitHub issue-comment API URL.")
    trusted_url = (
        "https://api.github.com/repos/Hexalith/Hexalith.EventStore/issues/comments/"
        f"{source_match.group('comment_id')}"
    )
    headers = {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if github_token and _origin(trusted_url) == GITHUB_API_ORIGIN:
        headers["Authorization"] = f"Bearer {github_token}"
    request = urllib.request.Request(trusted_url, headers=headers, method="GET")
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            response_raw = response.read(262145)
            if len(response_raw) > 262144:
                _fail("malformed-authority", "Authority record exceeds the size limit.")
            github_record = _parse_json_unique(response_raw)
            body = github_record.get("body")
            owner = github_record.get("user")
            owner_login = owner.get("login") if isinstance(owner, dict) else None
            html_url = github_record.get("html_url")
            if not isinstance(body, str) or not body.strip():
                _fail("malformed-authority", "GitHub authority comment body is empty.")
            if owner_login not in allowed_owners:
                _fail("wrong-owner", "GitHub authority author is not an approved release owner.")
            if not isinstance(html_url, str) or not html_url.startswith(
                "https://github.com/Hexalith/Hexalith.EventStore/"
            ):
                _fail("invalid-authority-source", "GitHub authority HTML URL is invalid.")
            raw = body.encode("utf-8")
            if len(raw) > 131072:
                _fail("malformed-authority", "Authority record exceeds the size limit.")
            metadata = {
                "api_url": trusted_url,
                "html_url": html_url,
                "login": owner_login,
                "role": "release_owner",
                "body_sha256": _sha256_bytes(raw),
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


def _stable_source_metadata(metadata):
    return {
        key: metadata.get(key)
        for key in ("api_url", "html_url", "login", "role", "body_sha256", "etag", "last_modified")
    }


def _write_evidence(directory, phase, authority_raw, metadata, authority_evidence, destination_evidence):
    directory = Path(directory)
    workspace_make_directory(directory)
    authority_path = directory / "release-owner-publication-authority.json"
    metadata_path = directory / "release-owner-publication-authority.source.json"
    if phase == "verify":
        if workspace_path_exists(authority_path) or workspace_path_exists(metadata_path):
            _fail("frozen-authority-collision", "Frozen authority evidence already exists.")
        workspace_write_bytes(authority_path, authority_raw)
        workspace_write_text(metadata_path, json.dumps(metadata, indent=2, sort_keys=True) + "\n")
    else:
        try:
            frozen_authority = workspace_read_bytes(authority_path)
            frozen_metadata = json.loads(workspace_read_text(metadata_path))
        except (OSError, json.JSONDecodeError) as error:
            raise AuthorityError("frozen-authority-missing", "Frozen authority evidence is unavailable.") from error
        if frozen_authority != authority_raw:
            _fail("authority-bytes-changed", "Live authority bytes differ from the frozen pre-tag record.")
        if _stable_source_metadata(frozen_metadata) != _stable_source_metadata(metadata):
            _fail("authority-source-changed", "Live authority source identity differs from the frozen record.")
    checked_name = (
        "release-owner-publication-authority.checked-at.txt"
        if phase == "verify"
        else f"release-owner-publication-authority.{phase}-revalidated-at.txt"
    )
    checked_at_path = directory / checked_name
    if workspace_path_exists(checked_at_path):
        _fail("authority-phase-collision", "Authority phase evidence already exists.")
    workspace_write_text(checked_at_path, authority_evidence["checked_at"] + "\n")
    evidence = {
        "result": "pass",
        "authority": authority_evidence,
        "destinations": destination_evidence,
        "source_revalidation": metadata,
        "files": {
            authority_path.name: _sha256_bytes(workspace_read_bytes(authority_path)),
            metadata_path.name: _sha256_bytes(workspace_read_bytes(metadata_path)),
            checked_at_path.name: _sha256_bytes(workspace_read_bytes(checked_at_path)),
        },
    }
    workspace_write_text(
        directory / f"publication-preflight.{phase}.json",
        json.dumps(evidence, indent=2, sort_keys=True) + "\n",
    )


def _parse_arguments():
    parser = argparse.ArgumentParser(description="Validate release publication authority.")
    parser.add_argument("--authority-url", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--container-repository", required=True)
    parser.add_argument("--builds-execution-sha", required=True)
    parser.add_argument("--package-manifest", type=workspace_input_file)
    parser.add_argument("--role-allowlist", required=True, type=workspace_input_file)
    parser.add_argument("--contract-directory", required=True, type=workspace_input_directory)
    parser.add_argument("--evidence-directory", required=True, type=workspace_output_directory)
    parser.add_argument("--phase", required=True, choices=("verify", "publish", "container"))
    return parser.parse_args()


def _expected_contract(arguments, metadata):
    contract_directory = arguments.contract_directory
    return {
        "repository": arguments.repository,
        "version": arguments.version,
        "source_sha": arguments.source_sha,
        "container_repository": arguments.container_repository,
        "durable_source": metadata["html_url"],
        "owner_name": metadata["login"],
        "platforms": list(REQUIRED_PLATFORMS),
        "builds_execution_sha": arguments.builds_execution_sha,
        "files": {name: contract_directory / name for name in REQUIRED_CONTRACT_FILES},
    }


def _validate_destinations(arguments, probe):
    if arguments.phase == "container":
        return validate_container_absence(
            arguments.version,
            arguments.container_repository,
            probe,
        )
    if arguments.package_manifest is None:
        _fail("package-inventory-mismatch", "Package manifest is required before NuGet publication.")
    return validate_destination_absence(
        _load_package_ids(arguments.package_manifest),
        arguments.version,
        arguments.container_repository,
        probe,
    )


def _validate_publication(arguments):
    checked_at = datetime.now(timezone.utc)
    allowed_owners = _load_role_allowlist(arguments.role_allowlist, arguments.repository)
    authority_raw, metadata = _load_authority_url(
        arguments.authority_url,
        os.environ.get("GITHUB_TOKEN", ""),
        allowed_owners,
    )
    expected = _expected_contract(arguments, metadata)
    _ = validate_authority_bytes(authority_raw, expected, checked_at)
    probe = destination_probe(
        os.environ.get("HEXALITH_ZOT_USERNAME", ""),
        os.environ.get("HEXALITH_ZOT_API_KEY", ""),
    )
    destination_evidence = _validate_destinations(arguments, probe)
    authority_evidence = validate_authority_bytes(
        authority_raw,
        expected,
        datetime.now(timezone.utc),
    )
    _write_evidence(
        arguments.evidence_directory,
        arguments.phase,
        authority_raw,
        metadata,
        authority_evidence,
        destination_evidence,
    )
    return authority_evidence


def main():
    arguments = _parse_arguments()
    try:
        authority_evidence = _validate_publication(arguments)
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
