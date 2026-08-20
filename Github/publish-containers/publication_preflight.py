#!/usr/bin/env python3
"""Validate immutable release identity and destination absence before publishing."""

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
from datetime import datetime, timedelta, timezone
from pathlib import Path

from oci_registry_validator import (
    MANIFEST_ACCEPT,
    SafeRedirectHandler,
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


PREFLIGHT_SCHEMA = "hexalith.release-publication-preflight.v4"
AUTHORITY_SCHEMA = "hexalith.release-publication-authority.v1"
AUTHORITY_CONSUMPTION_SCHEMA = "hexalith.release-publication-authority-consumption.v1"
REQUIRED_PLATFORMS = ["linux/amd64", "linux/arm64"]
REQUIRED_CONTRACT_FILES = (
    "publish-containers.sh",
    "oci_registry_validator.py",
    "publication_preflight.py",
    "smoke-container-platforms.sh",
    "smoke_container_platforms.py",
)
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
SEMVER_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", re.ASCII)
STABLE_SEMVER_PATTERN = re.compile(r"^\d+\.\d+\.\d+$", re.ASCII)
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", re.ASCII)
CONTAINER_REPOSITORY_PATTERN = re.compile(
    r"^[A-Za-z0-9.-]+(?::[0-9]+)?/[a-z0-9]+(?:[._/-][a-z0-9]+)*$",
    re.ASCII,
)
POSITIVE_INTEGER_PATTERN = re.compile(r"^[1-9][0-9]*$", re.ASCII)
WORKFLOW_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+\.ya?ml$", re.ASCII)
GITHUB_LOGIN_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$", re.ASCII)
NONCE_PATTERN = re.compile(r"^[A-Za-z0-9_-]{20,128}$", re.ASCII)
MAX_AUTHORITY_VALIDITY = timedelta(hours=24)
MAX_AUTHORITY_CLOCK_SKEW = timedelta(minutes=5)


class FailClosedRedirectHandler(SafeRedirectHandler):  # noqa: D203,D211
    """Reject redirects while proving mutable publication destinations and source state."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        """Reject every redirect so a different response cannot prove source or absence."""
        redirected = super().redirect_request(
            request,
            file_pointer,
            code,
            message,
            headers,
            new_url,
        )
        if redirected is not None:
            raise urllib.error.HTTPError(request.full_url, code, message, headers, file_pointer)
        return None


URL_OPENER = urllib.request.build_opener(FailClosedRedirectHandler())


class PreflightError(Exception):  # noqa: D203,D211
    """A deterministic, support-safe publication preflight failure."""

    def __init__(self, code, message):
        """Initialize a categorized publication preflight failure."""
        super().__init__(message)
        self.code = code


def _fail(code, message):
    raise PreflightError(code, message)


def _sha256_bytes(value):
    return hashlib.sha256(value).hexdigest()


def _canonical_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


def _required_text(value, code, field):
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        _fail(code, f"{field} must be a nonblank canonical string.")
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        _fail(code, f"{field} contains a control character.")
    return value


def _validate_environment_name(value):
    value = _required_text(value, "environment-invalid", "Release environment")
    if len(value) > 255:
        _fail("environment-invalid", "Release environment exceeds GitHub's name limit.")
    return value


def _canonical_container_repositories(value):
    """Return one-or-more unique container repositories in canonical set order."""
    repositories = [value] if isinstance(value, str) else value
    if not isinstance(repositories, (list, tuple)) or not repositories:
        _fail(
            "container-repository-invalid",
            "At least one container repository is required.",
        )

    canonical = []
    for repository in repositories:
        if (
            not isinstance(repository, str)
            or CONTAINER_REPOSITORY_PATTERN.fullmatch(repository) is None
        ):
            _fail("container-repository-invalid", "A container repository is invalid.")
        canonical.append(repository.lower())

    if len(set(canonical)) != len(canonical):
        _fail(
            "container-repository-invalid",
            "Container repositories must be unique.",
        )
    return sorted(canonical)


def _argument_container_repositories(arguments):
    """Read the repeatable CLI value while accepting the legacy singular test seam."""
    repositories = getattr(arguments, "container_repositories", None)
    if repositories is None:
        repositories = getattr(arguments, "container_repository", None)
    return _canonical_container_repositories(repositories)


def _runtime_identity(repository, source_sha, source_branch):
    runtime_repository = os.environ.get("GITHUB_REPOSITORY", "")
    runtime_sha = os.environ.get("GITHUB_SHA", "")
    workflow_sha = os.environ.get("GITHUB_WORKFLOW_SHA", "")
    if runtime_repository != repository:
        _fail("repository-mismatch", "Runtime repository does not match the release repository.")
    if runtime_sha != source_sha:
        _fail("source-mismatch", "Runtime source SHA does not match the release source.")
    runtime_ref = os.environ.get("GITHUB_REF", "")
    if runtime_ref != f"refs/heads/{source_branch}":
        _fail("source-ref-mismatch", "Runtime ref does not match the approved release branch.")
    if SHA_PATTERN.fullmatch(workflow_sha) is None:
        _fail("run-identity-invalid", "GITHUB_WORKFLOW_SHA must be an exact lowercase commit SHA.")

    run_id = os.environ.get("GITHUB_RUN_ID", "")
    run_attempt = os.environ.get("GITHUB_RUN_ATTEMPT", "")
    run_number = os.environ.get("GITHUB_RUN_NUMBER", "")
    for field, value in (
        ("GITHUB_RUN_ID", run_id),
        ("GITHUB_RUN_ATTEMPT", run_attempt),
        ("GITHUB_RUN_NUMBER", run_number),
    ):
        if POSITIVE_INTEGER_PATTERN.fullmatch(value) is None:
            _fail("run-identity-invalid", f"{field} must be a positive integer.")

    return {
        "id": run_id,
        "attempt": run_attempt,
        "number": run_number,
        "event": _required_text(
            os.environ.get("GITHUB_EVENT_NAME", ""),
            "run-identity-invalid",
            "GITHUB_EVENT_NAME",
        ),
        "workflow_ref": _required_text(
            os.environ.get("GITHUB_WORKFLOW_REF", ""),
            "run-identity-invalid",
            "GITHUB_WORKFLOW_REF",
        ),
        "workflow_sha": workflow_sha,
        "actor": _required_text(
            os.environ.get("GITHUB_ACTOR", ""),
            "run-identity-invalid",
            "GITHUB_ACTOR",
        ),
        "triggering_actor": _required_text(
            os.environ.get("GITHUB_TRIGGERING_ACTOR", ""),
            "run-identity-invalid",
            "GITHUB_TRIGGERING_ACTOR",
        ),
        "ref": runtime_ref,
    }


def _github_json(url, token):
    if not token:
        _fail("source-proof-unavailable", "GITHUB_TOKEN is required to prove the current release source.")
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
        },
        method="GET",
    )
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            if response.status != 200:
                _fail("source-proof-unavailable", "GitHub source proof did not return HTTP 200.")
            body = response.read()
    except (urllib.error.URLError, TimeoutError) as error:
        raise PreflightError(
            "source-proof-unavailable",
            "GitHub source proof could not be completed.",
        ) from error
    try:
        document = json.loads(body)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise PreflightError("source-proof-invalid", "GitHub source proof is not valid JSON.") from error
    if not isinstance(document, dict):
        _fail("source-proof-invalid", "GitHub source proof must be a JSON object.")
    return document


def _github_json_array(url, token):
    if not token:
        _fail("authority-unavailable", "GITHUB_TOKEN is required to prove publication authority.")
    parsed = urllib.parse.urlsplit(url)
    query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
    query = [(key, value) for key, value in query if key not in {"page", "per_page"}]
    documents = []
    for page in range(1, 101):
        page_url = urllib.parse.urlunsplit(
            parsed._replace(query=urllib.parse.urlencode([*query, ("per_page", "100"), ("page", str(page))]))
        )
        request = urllib.request.Request(
            page_url,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {token}",
                "X-GitHub-Api-Version": "2022-11-28",
            },
            method="GET",
        )
        try:
            with URL_OPENER.open(request, timeout=30) as response:
                if response.status != 200:
                    _fail("authority-unavailable", "GitHub authority lookup did not return HTTP 200.")
                document = json.loads(response.read())
        except (urllib.error.URLError, TimeoutError, UnicodeDecodeError, json.JSONDecodeError) as error:
            raise PreflightError(
                "authority-unavailable",
                "GitHub authority lookup could not be completed.",
            ) from error
        if not isinstance(document, list):
            _fail("authority-invalid", "GitHub authority comment list must be a JSON array.")
        documents.extend(document)
        if len(document) < 100:
            return documents
    _fail("authority-unavailable", "GitHub authority comment pagination exceeded the safe limit.")


def _github_post_comment(issue_url, body, token):
    if not token:
        _fail("authority-unavailable", "GITHUB_TOKEN is required to consume publication authority.")
    request = urllib.request.Request(
        issue_url.rstrip("/") + "/comments",
        data=_canonical_bytes({"body": body}),
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "X-GitHub-Api-Version": "2022-11-28",
        },
        method="POST",
    )
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            if response.status != 201:
                _fail("authority-consumption-failed", "GitHub authority consumption did not return HTTP 201.")
            document = json.loads(response.read())
    except (urllib.error.URLError, TimeoutError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise PreflightError(
            "authority-consumption-failed",
            "GitHub authority consumption could not be completed.",
        ) from error
    if not isinstance(document, dict):
        _fail("authority-consumption-failed", "GitHub authority consumption response is invalid.")
    return document


def _parse_timestamp(value, code, field):
    value = _required_text(value, code, field)
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise PreflightError(code, f"{field} must be an ISO-8601 timestamp.") from error
    if parsed.tzinfo is None:
        _fail(code, f"{field} must include a timezone.")
    return parsed.astimezone(timezone.utc)


def _authority_issue_api_url(repository, value):
    expected_prefix = f"https://api.github.com/repos/{repository}/issues/"
    if not isinstance(value, str) or not value.startswith(expected_prefix):
        _fail("authority-url-invalid", "Authority URL must identify an issue in the release repository.")
    issue_id = value.removeprefix(expected_prefix)
    if not issue_id.isdigit() or int(issue_id) <= 0:
        _fail("authority-url-invalid", "Authority issue URL must end in a positive issue ID.")
    return value, int(issue_id)


def _unique_json_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field")
        result[key] = value
    return result


def _embedded_json(value):
    return json.loads(value, object_pairs_hook=_unique_json_object)


def _has_release_owner_role(repository, owner, record, token):
    if record.get("author_association") in {"OWNER", "MEMBER", "COLLABORATOR"}:
        return True

    repository_path = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    owner_path = urllib.parse.quote(owner, safe="")
    permission = _github_json(
        f"https://api.github.com/repos/{repository_path}/collaborators/{owner_path}/permission",
        token,
    )
    return isinstance(permission, dict) and permission.get("permission") in {
        "admin",
        "maintain",
        "write",
    }


def validate_publication_authority(arguments, identity, token, now=None):
    """Validate one authenticated, expiring GitHub authority for this exact identity."""
    issue_url, _ = _authority_issue_api_url(arguments.repository, arguments.authority_issue_url)
    expected_owner = arguments.authority_owner.removeprefix("github:")
    if (
        arguments.authority_owner != f"github:{expected_owner}"
        or GITHUB_LOGIN_PATTERN.fullmatch(expected_owner) is None
    ):
        _fail("authority-owner-invalid", "Expected release-owner identity is invalid.")
    expected_identity_sha256 = _sha256_bytes(_canonical_bytes(identity))
    records = []
    for candidate in _github_json_array(issue_url.rstrip("/") + "/comments", token):
        if not isinstance(candidate, dict):
            continue
        try:
            candidate_body = _embedded_json(candidate.get("body", ""))
        except (TypeError, json.JSONDecodeError, ValueError):
            continue
        if (
            isinstance(candidate_body, dict)
            and candidate_body.get("schema") == AUTHORITY_SCHEMA
            and candidate_body.get("identity_sha256") == expected_identity_sha256
        ):
            records.append((candidate, candidate_body))
    if not records:
        _fail("authority-missing", "No publication authority binds the exact run identity.")
    if len(records) != 1:
        _fail("authority-ambiguous", "More than one publication authority binds the exact run identity.")
    record, body = records[0]
    comment_id = record.get("id")
    authority_url = record.get("url")
    if (
        not isinstance(comment_id, int)
        or comment_id <= 0
        or authority_url != f"https://api.github.com/repos/{arguments.repository}/issues/comments/{comment_id}"
    ):
        _fail("authority-invalid", "GitHub authority comment identity is invalid.")
    author = record.get("user")
    if not isinstance(author, dict) or author.get("login") != expected_owner:
        _fail("authority-wrong-role", "GitHub authority was not issued by the expected release owner.")
    # GitHub can redact a private organization membership as CONTRIBUTOR when
    # the repository-scoped Actions token reads the comment, even though a
    # user token with read:org reports MEMBER. Fall back to the repository's
    # authoritative collaborator permission instead of trusting that hint.
    if not _has_release_owner_role(arguments.repository, expected_owner, record, token):
        _fail("authority-wrong-role", "GitHub authority issuer has no repository release-owner role.")
    if record.get("issue_url") != issue_url:
        _fail("authority-invalid", "GitHub authority is not attached to the release repository.")
    if not isinstance(body, dict) or set(body) != {
        "schema",
        "role",
        "identity_sha256",
        "rationale",
        "authorized_at",
        "expires_at",
        "nonce",
    }:
        _fail("authority-invalid", "GitHub authority field set is invalid.")
    if body.get("schema") != AUTHORITY_SCHEMA or body.get("role") != "release-owner":
        _fail("authority-wrong-role", "GitHub authority role is invalid.")
    if body.get("identity_sha256") != expected_identity_sha256:
        _fail("authority-mismatch", "GitHub authority does not bind the exact publication identity.")
    if NONCE_PATTERN.fullmatch(body.get("nonce", "")) is None:
        _fail("authority-invalid", "GitHub authority nonce is invalid.")
    rationale = _required_text(body.get("rationale"), "authority-invalid", "Authority rationale")
    if len(rationale) > 500:
        _fail("authority-invalid", "Authority rationale exceeds the retained evidence limit.")
    expires_at = _parse_timestamp(body.get("expires_at"), "authority-invalid", "Authority expiry")
    authorized_at = _parse_timestamp(body.get("authorized_at"), "authority-invalid", "Authority timestamp")
    checked_at = now or datetime.now(timezone.utc)
    if checked_at >= expires_at:
        _fail("authority-expired", "GitHub publication authority has expired.")
    created_at = _parse_timestamp(record.get("created_at"), "authority-invalid", "Authority creation")
    updated_at = _parse_timestamp(record.get("updated_at"), "authority-invalid", "Authority update")
    if (
        created_at != updated_at
        or created_at >= expires_at
        or expires_at - created_at > MAX_AUTHORITY_VALIDITY
        or abs(authorized_at - created_at) > MAX_AUTHORITY_CLOCK_SKEW
    ):
        _fail("authority-invalid", "GitHub authority must be immutable and precede its expiry.")
    return {
        "url": authority_url,
        "comment_id": comment_id,
        "issue_url": issue_url,
        "owner": f"github:{expected_owner}",
        "created_at": record["created_at"],
        "authorized_at": body["authorized_at"],
        "expires_at": body["expires_at"],
        "rationale": rationale,
        "nonce": body["nonce"],
        "identity_sha256": expected_identity_sha256,
        "record_sha256": _sha256_bytes(_canonical_bytes(record)),
    }


def _consumption_body(authority, identity):
    return {
        "schema": AUTHORITY_CONSUMPTION_SCHEMA,
        "authority_comment_id": authority["comment_id"],
        "authority_record_sha256": authority["record_sha256"],
        "identity_sha256": authority["identity_sha256"],
        "run_id": identity["run"]["id"],
        "run_attempt": identity["run"]["attempt"],
        "nonce": authority["nonce"],
    }


def _matching_consumptions(authority, identity, token):
    expected = _consumption_body(authority, identity)
    comments = _github_json_array(authority["issue_url"].rstrip("/") + "/comments", token)
    matches = []
    for comment in comments:
        if not isinstance(comment, dict):
            continue
        try:
            body = _embedded_json(comment.get("body", ""))
        except (TypeError, json.JSONDecodeError, ValueError):
            continue
        if isinstance(body, dict) and body.get("authority_comment_id") == authority["comment_id"]:
            if body != expected:
                _fail("authority-replayed", "Publication authority already has a mismatched consumption.")
            user = comment.get("user")
            if not isinstance(user, dict) or user.get("login") != "github-actions[bot]":
                _fail("authority-replayed", "Publication authority consumption was not issued by GitHub Actions.")
            matches.append(comment)
    return matches


def require_authority_state(authority, identity, phase, token):
    """Require unconsumed verify/publish authority or its single exact container receipt."""
    matches = _matching_consumptions(authority, identity, token)
    if phase in {"verify", "publish"}:
        if matches:
            _fail("authority-replayed", "Publication authority has already been consumed.")
        return None
    if len(matches) != 1:
        _fail("authority-consumption-missing", "Container publication requires one exact authority consumption.")
    return matches[0]


def consume_publication_authority(authority, identity, token):
    """Consume authority once by creating and rereading one exact GitHub receipt."""
    require_authority_state(authority, identity, "publish", token)
    body = json.dumps(_consumption_body(authority, identity), sort_keys=True, separators=(",", ":"))
    created = _github_post_comment(authority["issue_url"], body, token)
    matches = _matching_consumptions(authority, identity, token)
    if len(matches) != 1 or matches[0].get("id") != created.get("id"):
        _fail("authority-replayed", "Publication authority consumption is not unique.")
    return matches[0]


def _live_source_sha(repository_path, source_branch, token):
    branch_path = urllib.parse.quote(source_branch, safe="")
    ref_document = _github_json(
        f"https://api.github.com/repos/{repository_path}/git/ref/heads/{branch_path}",
        token,
    )
    try:
        live_sha = ref_document["object"]["sha"]
    except (KeyError, TypeError) as error:
        raise PreflightError("source-proof-invalid", "GitHub main ref response is invalid.") from error
    if SHA_PATTERN.fullmatch(live_sha or "") is None:
        _fail("source-proof-invalid", "GitHub main ref SHA is invalid.")
    return live_sha


def _successful_ci_runs(repository_path, source_sha, source_branch, source_ci_workflow, token):
    workflow_path = urllib.parse.quote(source_ci_workflow, safe="")
    query = urllib.parse.urlencode(
        {
            "branch": source_branch,
            "event": "push",
            "head_sha": source_sha,
            "status": "success",
            "per_page": "100",
        }
    )
    document = _github_json(
        f"https://api.github.com/repos/{repository_path}/actions/workflows/{workflow_path}/runs?{query}",
        token,
    )
    runs = document.get("workflow_runs")
    if not isinstance(runs, list):
        _fail("source-proof-invalid", "GitHub CI runs response is invalid.")
    return [
        run
        for run in runs
        if isinstance(run, dict)
        and isinstance(run.get("id"), int)
        and run.get("id") > 0
        and run.get("head_sha") == source_sha
        and run.get("head_branch") == source_branch
        and run.get("event") == "push"
        and run.get("status") == "completed"
        and run.get("conclusion") == "success"
    ]


def prove_current_green_source(repository, source_sha, source_branch, source_ci_workflow, token):
    """Prove the exact source is still current main and has successful push CI."""
    if source_branch != "main":
        _fail("source-branch-invalid", "Publication source branch must be exactly main.")
    if WORKFLOW_PATTERN.fullmatch(source_ci_workflow) is None:
        _fail("source-workflow-invalid", "Source CI workflow must be a workflow filename.")

    repository_path = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    live_sha = _live_source_sha(repository_path, source_branch, token)
    if live_sha != source_sha:
        _fail("source-no-longer-current", "The release source is no longer the current main tip.")

    successful_runs = _successful_ci_runs(
        repository_path,
        source_sha,
        source_branch,
        source_ci_workflow,
        token,
    )
    if not successful_runs:
        _fail("source-ci-not-successful", "No successful push CI run exists for the exact current main source.")
    selected = min(successful_runs, key=lambda run: run["id"])
    return {
        "branch": source_branch,
        "ref": f"refs/heads/{source_branch}",
        "live_sha": live_sha,
        "ci_workflow": source_ci_workflow,
        "ci_run": {
            "id": selected["id"],
            "head_sha": selected["head_sha"],
            "head_branch": selected["head_branch"],
            "event": selected["event"],
            "status": selected["status"],
            "conclusion": selected["conclusion"],
        },
    }


def _contract_hashes(directory):
    hashes = {}
    for name in REQUIRED_CONTRACT_FILES:
        path = directory / name
        if not path.is_file():
            _fail("contract-file-missing", "An immutable publication contract file is unavailable.")
        hashes[name] = _sha256_bytes(path.read_bytes())
    return hashes


def build_publication_identity(arguments, source_proof=None):
    """Build the exact, comment-free identity frozen across publication phases."""
    if REPOSITORY_PATTERN.fullmatch(arguments.repository) is None:
        _fail("repository-invalid", "Release repository is invalid.")
    if SEMVER_PATTERN.fullmatch(arguments.version) is None:
        _fail("invalid-version", "Proposed release version is invalid.")
    if SHA_PATTERN.fullmatch(arguments.source_sha) is None:
        _fail("source-invalid", "Release source SHA must be an exact lowercase commit SHA.")
    if SHA_PATTERN.fullmatch(arguments.builds_execution_sha) is None:
        _fail("builds-identity-invalid", "Builds execution SHA must be an exact lowercase commit SHA.")
    container_repositories = _argument_container_repositories(arguments)

    proof = source_proof or prove_current_green_source(
        arguments.repository,
        arguments.source_sha,
        arguments.source_branch,
        arguments.source_ci_workflow,
        os.environ.get("GITHUB_TOKEN", ""),
    )

    identity = {
        "schema": PREFLIGHT_SCHEMA,
        "repository": arguments.repository,
        "version": arguments.version,
        "source_sha": arguments.source_sha,
        "source": proof,
        "container_repositories": container_repositories,
        "platforms": list(REQUIRED_PLATFORMS),
        "environment": _validate_environment_name(arguments.environment_name),
        "packages": _load_package_identity(arguments.package_manifest, arguments.expected_package_count),
        "builds": {
            "workflow_sha": arguments.builds_execution_sha,
            "action_sha": arguments.builds_execution_sha,
            "files": _contract_hashes(arguments.contract_directory),
        },
        "run": _runtime_identity(arguments.repository, arguments.source_sha, arguments.source_branch),
    }
    if len(container_repositories) == 1:
        identity["container_repository"] = container_repositories[0]
    return identity


def validate_destination_absence(package_ids, version, container_repositories, probe, expected_package_count):
    """Require the declared package IDs and complete container set to be absent."""
    if (
        len(package_ids) != expected_package_count
        or any(not isinstance(package_id, str) or not package_id.strip() for package_id in package_ids)
        or len({package_id.lower() for package_id in package_ids}) != expected_package_count
    ):
        _fail(
            "package-inventory-mismatch",
            f"Release package inventory must contain exactly {expected_package_count} unique IDs.",
        )
    if not isinstance(version, str) or SEMVER_PATTERN.fullmatch(version) is None:
        _fail("invalid-version", "Proposed release version is invalid.")
    repositories = _canonical_container_repositories(container_repositories)
    checked = []
    for package_id in package_ids:
        status = probe("nuget", package_id, version)
        if status == 200:
            _fail("version-collision", "A proposed NuGet package version already exists.")
        if status != 404:
            _fail("destination-probe-failure", "NuGet destination absence could not be proved.")
        checked.append(package_id)
    for repository in repositories:
        status = probe("container", repository, version)
        if status == 200:
            _fail("version-collision", "A proposed container tag already exists.")
        if status != 404:
            _fail("destination-probe-failure", "Container destination absence could not be proved.")
    evidence = {
        "result": "pass",
        "version": version,
        "package_count": len(checked),
        "package_ids": checked,
        "container_count": len(repositories),
        "container_repositories": repositories,
    }
    if len(repositories) == 1:
        evidence["container_repository"] = repositories[0]
    return evidence


def _stable_version_tuple(value):
    if not isinstance(value, str) or STABLE_SEMVER_PATTERN.fullmatch(value) is None:
        return None
    return tuple(int(part) for part in value.split("."))


def validate_version_floor(version, observations):
    """Require a stable candidate newer than every stable version observed at every destination."""
    candidate = _stable_version_tuple(version)
    if candidate is None:
        _fail("invalid-version", "Corrective release version must be stable semantic version.")
    if not isinstance(observations, list) or not observations:
        _fail("version-floor-unavailable", "No external release version observations are available.")
    evidence = []
    for observation in observations:
        if not isinstance(observation, dict) or set(observation) != {"kind", "identity", "versions"}:
            _fail("version-floor-invalid", "External release version observation is malformed.")
        kind = _required_text(observation["kind"], "version-floor-invalid", "Destination kind")
        identity = _required_text(
            observation["identity"],
            "version-floor-invalid",
            "Destination identity",
        )
        versions = observation["versions"]
        if not isinstance(versions, list) or any(not isinstance(item, str) for item in versions):
            _fail("version-floor-invalid", "External release version list is malformed.")
        stable = sorted(
            {item for item in versions if _stable_version_tuple(item) is not None},
            key=_stable_version_tuple,
        )
        highest = stable[-1] if stable else None
        if highest is not None and candidate <= _stable_version_tuple(highest):
            _fail(
                "version-not-newer",
                f"Corrective release version is not newer than {kind} destination {identity}.",
            )
        evidence.append(
            {
                "kind": kind,
                "identity": identity,
                "highest_stable": highest,
                "observed_versions_sha256": _sha256_bytes(_canonical_bytes(versions)),
            }
        )
    return {"result": "pass", "candidate": version, "destinations": evidence}


def _read_json_response(url, headers, code):
    request = urllib.request.Request(url, headers=headers, method="GET")
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            if response.status != 200:
                _fail(code, "External release version lookup did not return HTTP 200.")
            return json.loads(response.read())
    except (urllib.error.URLError, TimeoutError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise PreflightError(code, "External release version lookup could not be completed.") from error


def _github_version_pages(repository, endpoint, token):
    if not token:
        _fail("version-floor-unavailable", "GITHUB_TOKEN is required to prove the release version floor.")
    values = []
    repository_path = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    for page in range(1, 101):
        url = (
            f"https://api.github.com/repos/{repository_path}/{endpoint}"
            f"?per_page=100&page={page}"
        )
        document = _read_json_response(
            url,
            {
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {token}",
                "X-GitHub-Api-Version": "2022-11-28",
            },
            "version-floor-unavailable",
        )
        if not isinstance(document, list):
            _fail("version-floor-invalid", "GitHub release version response is not an array.")
        values.extend(document)
        if len(document) < 100:
            return values
    _fail("version-floor-unavailable", "GitHub release version pagination exceeded the safe limit.")


def _exclude_semantic_release_self_tag(repository, tags, version, source_sha):
    if _stable_version_tuple(version) is None or SHA_PATTERN.fullmatch(source_sha or "") is None:
        _fail("semantic-release-tag-invalid", "Semantic Release self-tag proof is invalid.")
    expected_name = f"v{version}"
    matching_tags = [
        item
        for item in tags
        if isinstance(item, dict) and item.get("name") == expected_name
    ]
    if len(matching_tags) != 1:
        _fail(
            "semantic-release-tag-invalid",
            "Semantic Release must create exactly one reserved-version tag before publishing.",
        )
    self_tag = matching_tags[0]
    if not isinstance(self_tag.get("commit"), dict) or self_tag["commit"].get("sha") != source_sha:
        _fail(
            "semantic-release-tag-invalid",
            "Semantic Release reserved-version tag does not target the approved source.",
        )
    return (
        [item for item in tags if item is not self_tag],
        {
            "kind": "semantic-release-tag",
            "identity": f"{repository}#refs/tags/{expected_name}@{source_sha}",
            "versions": [],
        },
    )


def _github_version_observations(
    repository,
    token,
    semantic_release_version=None,
    source_sha=None,
):
    releases = _github_version_pages(repository, "releases", token)
    tags = _github_version_pages(repository, "tags", token)
    self_tag_observation = None
    if semantic_release_version is not None:
        tags, self_tag_observation = _exclude_semantic_release_self_tag(
            repository,
            tags,
            semantic_release_version,
            source_sha,
        )

    observations = [
        {
            "kind": "github-release",
            "identity": repository,
            "versions": [
                item.get("tag_name", "").removeprefix("v")
                for item in releases
                if isinstance(item, dict) and not item.get("draft", False)
            ],
        },
        {
            "kind": "git-tag",
            "identity": repository,
            "versions": [
                item.get("name", "").removeprefix("v")
                for item in tags
                if isinstance(item, dict)
            ],
        },
    ]
    if self_tag_observation is not None:
        observations.append(self_tag_observation)
    return observations


def _nuget_version_observation(package_id):
    normalized = urllib.parse.quote(package_id.lower(), safe="")
    document = _read_json_response(
        f"https://api.nuget.org/v3-flatcontainer/{normalized}/index.json",
        {"Accept": "application/json"},
        "version-floor-unavailable",
    )
    versions = document.get("versions") if isinstance(document, dict) else None
    if not isinstance(versions, list):
        _fail("version-floor-invalid", "NuGet version response is malformed.")
    return {"kind": "nuget", "identity": package_id, "versions": versions}


def _registry_version_observation(container_repository, authorization):
    registry, repository_path = container_repository.split("/", 1)
    document = _read_json_response(
        f"https://{registry}/v2/{repository_path}/tags/list",
        {"Accept": "application/json", "Authorization": authorization},
        "version-floor-unavailable",
    )
    versions = document.get("tags") if isinstance(document, dict) else None
    if not isinstance(versions, list):
        _fail("version-floor-invalid", "Registry tag response is malformed.")
    return {"kind": "oci-registry", "identity": container_repository, "versions": versions}


def read_external_version_observations(
    repository,
    package_ids,
    container_repositories,
    token,
    registry_username,
    registry_api_key,
    semantic_release_version=None,
    source_sha=None,
):
    """Read versions from GitHub releases/tags, every NuGet ID, and every registry repository."""
    observations = _github_version_observations(
        repository,
        token,
        semantic_release_version,
        source_sha,
    )
    observations.extend(_nuget_version_observation(package_id) for package_id in package_ids)
    if not registry_username or not registry_api_key:
        _fail("version-floor-unavailable", "Registry credentials are required to prove the version floor.")
    registry_authorization = "Basic " + base64.b64encode(
        f"{registry_username}:{registry_api_key}".encode("utf-8")
    ).decode("ascii")
    observations.extend(
        _registry_version_observation(container_repository, registry_authorization)
        for container_repository in _canonical_container_repositories(container_repositories)
    )
    return observations


def validate_container_absence(version, container_repositories, probe):
    """Require every exact container version tag in the frozen set to remain absent."""
    if not isinstance(version, str) or SEMVER_PATTERN.fullmatch(version) is None:
        _fail("invalid-version", "Proposed release version is invalid.")
    repositories = _canonical_container_repositories(container_repositories)
    for repository in repositories:
        status = probe("container", repository, version)
        if status == 200:
            _fail("version-collision", "A proposed container tag already exists.")
        if status != 404:
            _fail("destination-probe-failure", "Container destination absence could not be proved.")
    evidence = {
        "result": "pass",
        "version": version,
        "container_count": len(repositories),
        "container_repositories": repositories,
    }
    if len(repositories) == 1:
        evidence["container_repository"] = repositories[0]
    return evidence


def _http_status(request):
    try:
        with URL_OPENER.open(request, timeout=30) as response:
            response.read(1)
            return response.status
    except urllib.error.HTTPError as error:
        return error.code
    except (urllib.error.URLError, TimeoutError) as error:
        raise PreflightError(
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
            return _http_status(urllib.request.Request(url, method="HEAD"))
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


def _canonical_package_id(package_id):
    if not isinstance(package_id, str) or not package_id or package_id != package_id.strip():
        return False
    return not any(ord(character) < 32 or ord(character) == 127 for character in package_id)


def _load_package_identity(path, expected_package_count):
    try:
        manifest_bytes = workspace_read_bytes(Path(path))
        manifest = json.loads(manifest_bytes, object_pairs_hook=_unique_json_object)
        packages = manifest["packages"]
        package_ids = [item["id"] for item in packages]
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as error:
        raise PreflightError("package-inventory-mismatch", "Package manifest is invalid.") from error
    if (
        not isinstance(manifest, dict)
        or not isinstance(packages, list)
        or len(package_ids) != expected_package_count
        or not all(_canonical_package_id(package_id) for package_id in package_ids)
        or len({package_id.lower() for package_id in package_ids}) != expected_package_count
    ):
        _fail(
            "package-inventory-mismatch",
            f"Release package inventory must contain exactly {expected_package_count} unique IDs.",
        )
    return {
        "ids": package_ids,
        "normalized_ids": [package_id.lower() for package_id in package_ids],
        "manifest_sha256": _sha256_bytes(manifest_bytes),
    }


def _checked_at():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _require_frozen_identity(directory, identity):
    identity_path = directory / "publication-identity.json"
    try:
        frozen = workspace_read_bytes(identity_path)
    except OSError as error:
        raise PreflightError("frozen-identity-missing", "Frozen publication identity is unavailable.") from error
    if frozen != _canonical_bytes(identity):
        _fail("publication-identity-changed", "Current publication identity differs from the frozen verify phase.")


def _write_evidence(directory, phase, identity, destination_evidence):
    directory = Path(directory)
    workspace_make_directory(directory)
    identity_path = directory / "publication-identity.json"
    verify_path = directory / "publication-preflight.verify.json"
    publish_path = directory / "publication-preflight.publish.json"
    phase_path = directory / f"publication-preflight.{phase}.json"

    if workspace_path_exists(phase_path):
        _fail("preflight-phase-collision", "Publication preflight phase evidence already exists.")
    if phase == "verify":
        if workspace_path_exists(identity_path):
            _fail("frozen-identity-collision", "Frozen publication identity already exists.")
        workspace_write_bytes(identity_path, _canonical_bytes(identity))
    else:
        _require_frozen_identity(directory, identity)
        required_previous = verify_path if phase == "publish" else publish_path
        if not workspace_path_exists(required_previous):
            _fail("preflight-sequence-invalid", "A required earlier publication preflight phase is missing.")

    frozen_bytes = workspace_read_bytes(identity_path)
    evidence = {
        "schema": PREFLIGHT_SCHEMA,
        "result": "pass",
        "phase": phase,
        "checked_at": _checked_at(),
        "identity_sha256": _sha256_bytes(frozen_bytes),
        "identity": identity,
        "destinations": destination_evidence,
    }
    workspace_write_text(phase_path, json.dumps(evidence, indent=2, sort_keys=True) + "\n")


def _write_authority_evidence(directory, authority, consumption=None):
    directory = Path(directory)
    workspace_make_directory(directory)
    authority_path = directory / "publication-authority.json"
    authority_bytes = _canonical_bytes(authority)
    if workspace_path_exists(authority_path):
        if workspace_read_bytes(authority_path) != authority_bytes:
            _fail("authority-mismatch", "Retained publication authority changed between phases.")
    else:
        workspace_write_bytes(authority_path, authority_bytes)
    if consumption is not None:
        consumption_path = directory / "publication-authority-consumption.json"
        if workspace_path_exists(consumption_path):
            if workspace_read_bytes(consumption_path) != _canonical_bytes(consumption):
                _fail("authority-replayed", "Publication authority consumption evidence changed.")
        else:
            workspace_write_bytes(consumption_path, _canonical_bytes(consumption))


def _positive_integer_argument(value):
    """Return a positive integer argument, rejecting zero, negatives, padding and non-digits."""
    if POSITIVE_INTEGER_PATTERN.fullmatch(value) is None:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return int(value)


def _parse_arguments():
    parser = argparse.ArgumentParser(description="Validate release publication identity and destination absence.")
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--source-branch", required=False, default="main")
    parser.add_argument("--source-ci-workflow", required=False, default="ci.yml")
    parser.add_argument(
        "--container-repository",
        dest="container_repositories",
        action="append",
        required=True,
        help="Container repository in registry/repository form. Repeat for the complete release set.",
    )
    parser.add_argument("--builds-execution-sha", required=True)
    parser.add_argument("--environment-name", required=True)
    parser.add_argument("--authority-issue-url", required=True)
    parser.add_argument("--authority-owner", required=True)
    parser.add_argument("--package-manifest", required=True, type=workspace_input_file)
    # No default: each module declares its own inventory size, and a default would
    # silently reinstate one module's package count as every other module's gate.
    parser.add_argument("--expected-package-count", required=True, type=_positive_integer_argument)
    parser.add_argument("--contract-directory", required=True, type=workspace_input_directory)
    parser.add_argument("--evidence-directory", required=True, type=workspace_output_directory)
    parser.add_argument("--phase", required=True, choices=("verify", "publish", "container"))
    return parser.parse_args()


def _validate_destinations(arguments, probe):
    container_repositories = _argument_container_repositories(arguments)
    if arguments.phase == "container":
        return validate_container_absence(
            arguments.version,
            container_repositories,
            probe,
        )
    package_identity = _load_package_identity(arguments.package_manifest, arguments.expected_package_count)
    version_floor = validate_version_floor(
        arguments.version,
        read_external_version_observations(
            arguments.repository,
            package_identity["ids"],
            container_repositories,
            os.environ.get("GITHUB_TOKEN", ""),
            os.environ.get("HEXALITH_ZOT_USERNAME", ""),
            os.environ.get("HEXALITH_ZOT_API_KEY", ""),
            arguments.version if arguments.phase == "publish" else None,
            arguments.source_sha,
        ),
    )
    absence = validate_destination_absence(
        package_identity["ids"],
        arguments.version,
        container_repositories,
        probe,
        arguments.expected_package_count,
    )
    return {"result": "pass", "version_floor": version_floor, "absence": absence}


def _validate_publication(arguments):
    identity = build_publication_identity(arguments)
    token = os.environ.get("GITHUB_TOKEN", "")
    authority = validate_publication_authority(arguments, identity, token)
    if arguments.phase != "verify":
        _require_frozen_identity(arguments.evidence_directory, identity)
    require_authority_state(authority, identity, arguments.phase, token)
    probe = destination_probe(
        os.environ.get("HEXALITH_ZOT_USERNAME", ""),
        os.environ.get("HEXALITH_ZOT_API_KEY", ""),
    )
    destination_evidence = _validate_destinations(arguments, probe)
    revalidated_identity = build_publication_identity(arguments)
    if revalidated_identity != identity:
        _fail("publication-identity-changed", "Publication identity changed during destination probing.")
    revalidated_authority = validate_publication_authority(arguments, revalidated_identity, token)
    if revalidated_authority != authority:
        _fail("authority-mismatch", "Publication authority changed during destination probing.")
    consumption = None
    if arguments.phase == "publish":
        consumption = consume_publication_authority(authority, identity, token)
    elif arguments.phase == "container":
        consumption = require_authority_state(authority, identity, "container", token)
    _write_authority_evidence(arguments.evidence_directory, authority, consumption)
    _write_evidence(
        arguments.evidence_directory,
        arguments.phase,
        revalidated_identity,
        destination_evidence,
    )
    return revalidated_identity


def main():
    arguments = _parse_arguments()
    try:
        identity = _validate_publication(arguments)
    except PreflightError as error:
        print(f"[publication-preflight] {error.code}: {error}", file=sys.stderr)
        return 1
    print(
        f"[publication-preflight] pass: {arguments.repository} {arguments.version} "
        f"run {identity['run']['id']}/{identity['run']['attempt']} phase {arguments.phase}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
