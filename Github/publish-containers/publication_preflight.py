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
from datetime import datetime, timezone
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


PREFLIGHT_SCHEMA = "hexalith.release-publication-preflight.v2"
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
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", re.ASCII)
CONTAINER_REPOSITORY_PATTERN = re.compile(
    r"^[A-Za-z0-9.-]+(?::[0-9]+)?/[a-z0-9]+(?:[._/-][a-z0-9]+)*$",
    re.ASCII,
)
POSITIVE_INTEGER_PATTERN = re.compile(r"^[1-9][0-9]*$", re.ASCII)
WORKFLOW_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+\.ya?ml$", re.ASCII)


class FailClosedRedirectHandler(SafeRedirectHandler):
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
    except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError) as error:
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


def prove_current_green_source(repository, source_sha, source_branch, source_ci_workflow, token):
    """Prove the exact source is still current main and has successful push CI."""
    if source_branch != "main":
        _fail("source-branch-invalid", "Publication source branch must be exactly main.")
    if WORKFLOW_PATTERN.fullmatch(source_ci_workflow) is None:
        _fail("source-workflow-invalid", "Source CI workflow must be a workflow filename.")

    repository_path = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
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
    if live_sha != source_sha:
        _fail("source-no-longer-current", "The release source is no longer the current main tip.")

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
    runs_document = _github_json(
        f"https://api.github.com/repos/{repository_path}/actions/workflows/{workflow_path}/runs?{query}",
        token,
    )
    runs = runs_document.get("workflow_runs")
    if not isinstance(runs, list):
        _fail("source-proof-invalid", "GitHub CI runs response is invalid.")
    successful_runs = [
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
    if CONTAINER_REPOSITORY_PATTERN.fullmatch(arguments.container_repository) is None:
        _fail("container-repository-invalid", "Container repository is invalid.")

    proof = source_proof or prove_current_green_source(
        arguments.repository,
        arguments.source_sha,
        arguments.source_branch,
        arguments.source_ci_workflow,
        os.environ.get("GITHUB_TOKEN", ""),
    )

    return {
        "schema": PREFLIGHT_SCHEMA,
        "repository": arguments.repository,
        "version": arguments.version,
        "source_sha": arguments.source_sha,
        "source": proof,
        "container_repository": arguments.container_repository,
        "platforms": list(REQUIRED_PLATFORMS),
        "environment": _validate_environment_name(arguments.environment_name),
        "packages": _load_package_identity(arguments.package_manifest),
        "builds": {
            "workflow_sha": arguments.builds_execution_sha,
            "action_sha": arguments.builds_execution_sha,
            "files": _contract_hashes(arguments.contract_directory),
        },
        "run": _runtime_identity(arguments.repository, arguments.source_sha, arguments.source_branch),
    }


def validate_destination_absence(package_ids, version, container_repository, probe):
    """Require exactly 14 new package IDs and one new container tag to be absent."""
    if (
        len(package_ids) != 14
        or any(not isinstance(package_id, str) or not package_id.strip() for package_id in package_ids)
        or len({package_id.lower() for package_id in package_ids}) != 14
    ):
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
        "package_ids": checked,
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


def _load_package_identity(path):
    try:
        manifest = json.loads(workspace_read_text(Path(path)))
        packages = manifest["packages"]
        package_ids = [item["id"] for item in packages]
    except (OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise PreflightError("package-inventory-mismatch", "Package manifest is invalid.") from error
    if (
        not isinstance(manifest, dict)
        or not isinstance(packages, list)
        or len(package_ids) != 14
        or any(
            not isinstance(package_id, str)
            or not package_id
            or package_id != package_id.strip()
            or any(ord(character) < 32 or ord(character) == 127 for character in package_id)
            for package_id in package_ids
        )
        or len({package_id.lower() for package_id in package_ids}) != 14
    ):
        _fail("package-inventory-mismatch", "Release package inventory must contain exactly 14 unique IDs.")
    return {
        "ids": package_ids,
        "normalized_ids": [package_id.lower() for package_id in package_ids],
        "manifest_sha256": _sha256_bytes(_canonical_bytes(manifest)),
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


def _parse_arguments():
    parser = argparse.ArgumentParser(description="Validate release publication identity and destination absence.")
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--source-branch", required=False, default="main")
    parser.add_argument("--source-ci-workflow", required=False, default="ci.yml")
    parser.add_argument("--container-repository", required=True)
    parser.add_argument("--builds-execution-sha", required=True)
    parser.add_argument("--environment-name", required=True)
    parser.add_argument("--package-manifest", required=True, type=workspace_input_file)
    parser.add_argument("--contract-directory", required=True, type=workspace_input_directory)
    parser.add_argument("--evidence-directory", required=True, type=workspace_output_directory)
    parser.add_argument("--phase", required=True, choices=("verify", "publish", "container"))
    return parser.parse_args()


def _validate_destinations(arguments, probe):
    if arguments.phase == "container":
        return validate_container_absence(
            arguments.version,
            arguments.container_repository,
            probe,
        )
    package_identity = _load_package_identity(arguments.package_manifest)
    return validate_destination_absence(
        package_identity["ids"],
        arguments.version,
        arguments.container_repository,
        probe,
    )


def _validate_publication(arguments):
    identity = build_publication_identity(arguments)
    if arguments.phase != "verify":
        _require_frozen_identity(arguments.evidence_directory, identity)
    probe = destination_probe(
        os.environ.get("HEXALITH_ZOT_USERNAME", ""),
        os.environ.get("HEXALITH_ZOT_API_KEY", ""),
    )
    destination_evidence = _validate_destinations(arguments, probe)
    revalidated_identity = build_publication_identity(arguments)
    if revalidated_identity != identity:
        _fail("publication-identity-changed", "Publication identity changed during destination probing.")
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
