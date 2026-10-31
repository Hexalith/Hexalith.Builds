"""Attestation subject binding, statement checks, and run-bound fallback authorization."""

from __future__ import annotations

import base64
from typing import Any

from canonical import (
    ContractError,
    canonical_bytes,
    canonical_digest,
    expect_commit,
    expect_identity,
    expect_login,
    expect_members,
    expect_path,
    expect_safe_integer,
    expect_sha256,
    expect_string,
    expect_timestamp,
    parse_json,
    sha256_bytes,
)


FALLBACK_REQUEST_SCHEMA = "hexalith.attestation-fallback-request.v1"
FALLBACK_AUTHORIZATION_SCHEMA = "hexalith.attestation-fallback-authorization.v3"
STATEMENT_TYPE = "https://in-toto.io/Statement/v1"
PREDICATE_TYPE = "https://slsa.dev/provenance/v1"
BUILD_TYPE = "https://actions.github.io/buildtypes/workflow/v1"
TOKEN_ISSUER = "https://token.actions.githubusercontent.com"
DEFINITION_FILES = (
    ".github/workflows/ci.yml",
    ".github/workflows/release.yml",
    "eng/dependency-graph-policy.json",
    "eng/dependency_graph.py",
    "eng/dependency_handoff.py",
    "eng/release_contract.py",
    "eng/release_prepublish.py",
    "eng/workflow_source_closure.py",
)
HELPER_VERSION = "1.0.0"


def helper_version_digest() -> str:
    """Return the SHA-256 of this publisher helper's version string."""

    return sha256_bytes(HELPER_VERSION.encode("utf-8"))


def subject_checksums(packages: list[tuple[str, str]]) -> bytes:
    """Return the ordinally sorted shasum file for package asset-name/digest pairs."""

    rows = []
    for name, digest in packages:
        expect_string(name, "package asset name")
        expect_sha256(digest, "package digest")
        rows.append((name, digest))
    rows.sort()
    if len(rows) != len(set(name for name, _digest in rows)):
        raise ContractError("attestation subjects contain duplicate package asset names")
    return "".join(f"{digest} *{name}\n" for name, digest in rows).encode("ascii")


def _pae(payload_type: bytes, payload: bytes) -> bytes:
    return b"DSSEv1 " + str(len(payload_type)).encode("ascii") + b" " + payload_type + b" " + str(len(payload)).encode("ascii") + b" " + payload


def validate_statement(statement: dict[str, Any], expected: dict[str, Any]) -> None:
    """Require the GitHub workflow build predicate bound to this exact attempt."""

    document = expect_members(
        statement,
        {"_type", "subject", "predicateType", "predicate"},
        "attestation statement",
    )
    if document["_type"] != STATEMENT_TYPE or document["predicateType"] != PREDICATE_TYPE:
        raise ContractError("attestation statement type is not an in-toto SLSA provenance statement")
    subjects = document["subject"]
    if not isinstance(subjects, list):
        raise ContractError("attestation subjects must be an array")
    normalized = []
    for index, subject in enumerate(subjects):
        row = expect_members(subject, {"name", "digest"}, f"attestation subject[{index}]")
        digest = expect_members(row["digest"], {"sha256"}, f"attestation subject[{index}] digest")
        normalized.append(
            {
                "name": expect_string(row["name"], f"attestation subject[{index}] name"),
                "digest": {"sha256": expect_sha256(digest["sha256"], f"attestation subject[{index}] sha256")},
            }
        )
    expected_subjects = [
        {"name": name, "digest": {"sha256": digest}}
        for name, digest in sorted(expected["packages"])
    ]
    if normalized != expected_subjects:
        raise ContractError("attestation subjects are not the authenticated package set")
    predicate = expect_members(
        document["predicate"],
        {"buildDefinition", "runDetails"},
        "attestation predicate",
    )
    build = expect_members(
        predicate["buildDefinition"],
        {"buildType", "externalParameters", "internalParameters", "resolvedDependencies"},
        "build definition",
    )
    if build["buildType"] != BUILD_TYPE:
        raise ContractError("attestation build type is not the GitHub workflow build type")
    external = expect_members(build["externalParameters"], {"workflow"}, "attestation external parameters")
    workflow = expect_members(
        external["workflow"],
        {"repository", "ref", "path"},
        "attestation workflow parameters",
    )
    repository_url = f"https://github.com/{expected['repository_slug']}"
    if (
        workflow["repository"] != repository_url
        or workflow["ref"] != "refs/heads/main"
        or workflow["path"] != expected["caller_workflow"]
    ):
        raise ContractError("attestation workflow parameters do not match the authenticated caller")
    internal = expect_members(build["internalParameters"], {"github"}, "attestation internal parameters")
    github = expect_members(
        internal["github"],
        {"event_name", "repository_id", "repository_owner_id", "runner_environment"},
        "attestation github parameters",
    )
    if (
        github["event_name"] != "workflow_dispatch"
        or github["runner_environment"] != "github-hosted"
        or expect_safe_integer(github["repository_id"], "repository id", minimum=1) != expected["repository_id"]
        or expect_safe_integer(github["repository_owner_id"], "repository owner id", minimum=1) != expected["owner_id"]
    ):
        raise ContractError("attestation GitHub parameters do not match the authenticated run")
    dependencies = build["resolvedDependencies"]
    expected_dependency = {
        "uri": f"git+{repository_url}@refs/heads/main",
        "digest": {"gitCommit": expected["candidate"]},
    }
    if dependencies != [expected_dependency]:
        raise ContractError("attestation resolved dependency is not the authenticated candidate")
    run_details = expect_members(predicate["runDetails"], {"builder", "metadata"}, "attestation run details")
    builder = expect_members(run_details["builder"], {"id"}, "attestation builder")
    expected_builder = (
        "https://github.com/Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@"
        f"{expected['builds_sha']}"
    )
    if builder["id"] != expected_builder:
        raise ContractError("attestation builder id is not the selected reusable commit")
    metadata = expect_members(run_details["metadata"], {"invocationId"}, "attestation metadata")
    expected_invocation = (
        f"https://github.com/{expected['repository_slug']}/actions/runs/"
        f"{expected['run_id']}/attempts/{expected['run_attempt']}"
    )
    if metadata["invocationId"] != expected_invocation:
        raise ContractError("attestation invocation id is not the current Release attempt")


def certificate_identity(extensions: dict[str, str], expected: dict[str, str]) -> None:
    """Require the Fulcio certificate identity extensions for this attempt."""

    required = {
        "1.3.6.1.4.1.57264.1.8": TOKEN_ISSUER,
        "1.3.6.1.4.1.57264.1.10": expected["builds_sha"],
        "1.3.6.1.4.1.57264.1.11": "github-hosted",
        "1.3.6.1.4.1.57264.1.12": f"https://github.com/{expected['repository_slug']}",
        "1.3.6.1.4.1.57264.1.13": expected["candidate"],
        "1.3.6.1.4.1.57264.1.14": "refs/heads/main",
    }
    for oid, value in required.items():
        if extensions.get(oid) != value:
            raise ContractError(f"attestation certificate extension {oid} does not match the authenticated attempt")


def decode_statement(bundle: dict[str, Any]) -> tuple[bytes, bytes, dict[str, Any]]:
    """Decode a Sigstore bundle payload without treating it as executable."""

    envelope = bundle.get("dsseEnvelope")
    if not isinstance(envelope, dict):
        raise ContractError("attestation bundle has no DSSE envelope")
    try:
        payload = base64.b64decode(envelope["payload"], validate=True)
        signature = base64.b64decode(envelope["signatures"][0]["sig"], validate=True)
    except (KeyError, IndexError, TypeError, ValueError) as error:
        raise ContractError("attestation bundle payload or signature is not canonical base64") from error
    statement = parse_json(payload, "attestation statement", max_bytes=1024 * 1024)
    if not isinstance(statement, dict):
        raise ContractError("attestation statement must be a JSON object")
    return payload, signature, statement


def preauthentication_encoding(payload: bytes) -> bytes:
    """Return the DSSE v1 pre-authentication encoding of an in-toto payload."""

    return _pae(b"application/vnd.in-toto+json", payload)


def validate_fallback_request(value: Any, expected: dict[str, Any]) -> dict[str, Any]:
    """Validate one unpadded canonical fallback request against the authenticated attempt."""

    request = expect_members(
        value,
        {
            "schema",
            "candidate",
            "ci_run",
            "reason",
            "requested_by",
            "created_at",
            "expires_at",
            "fingerprints_sha256",
            "authority",
        },
        "attestation fallback request",
    )
    if request["schema"] != FALLBACK_REQUEST_SCHEMA or request["reason"] != "github-attestations-unavailable":
        raise ContractError("attestation fallback request schema or reason is not closed")
    ci_run = expect_members(
        request["ci_run"],
        {"repository", "workflow_path", "run_id", "run_attempt"},
        "fallback CI run",
    )
    normalized = {
        "schema": FALLBACK_REQUEST_SCHEMA,
        "candidate": expect_commit(request["candidate"], "fallback candidate"),
        "ci_run": {
            "repository": expect_identity(ci_run["repository"], "fallback CI repository"),
            "workflow_path": expect_path(ci_run["workflow_path"], "fallback CI workflow", max_depth=16),
            "run_id": expect_safe_integer(ci_run["run_id"], "fallback CI run", minimum=1),
            "run_attempt": expect_safe_integer(ci_run["run_attempt"], "fallback CI attempt", minimum=1),
        },
        "reason": "github-attestations-unavailable",
        "requested_by": expect_login(request["requested_by"], "fallback requester"),
        "created_at": expect_timestamp(request["created_at"], "fallback created_at"),
        "expires_at": expect_timestamp(request["expires_at"], "fallback expires_at"),
        "fingerprints_sha256": expect_sha256(request["fingerprints_sha256"], "fallback fingerprints"),
        "authority": validate_authority(request["authority"]),
    }
    if normalized["candidate"] != expected["candidate"] or normalized["ci_run"] != expected["ci_run"]:
        raise ContractError("attestation fallback request does not match the authenticated CI handoff")
    if normalized["requested_by"] != expected["actor"] or normalized["authority"] != expected["authority"]:
        raise ContractError("attestation fallback request actor or policy authority does not match")
    if normalized["fingerprints_sha256"] != expected["fingerprints_sha256"]:
        raise ContractError("attestation fallback fingerprints do not match the authenticated definition")
    if normalized["expires_at"] <= normalized["created_at"]:
        raise ContractError("attestation fallback request does not expire after it was created")
    created = _timestamp_seconds(normalized["created_at"])
    expires = _timestamp_seconds(normalized["expires_at"])
    if expires - created > 24 * 60 * 60:
        raise ContractError("attestation fallback request expires more than 24 hours after creation")
    return normalized


def validate_authority(value: Any) -> dict[str, str]:
    """Validate a policy commit and raw policy digest pair."""

    authority = expect_members(value, {"policy_commit", "policy_sha256"}, "fallback authority")
    return {
        "policy_commit": expect_commit(authority["policy_commit"], "fallback policy commit"),
        "policy_sha256": expect_sha256(authority["policy_sha256"], "fallback policy sha256"),
    }


def _timestamp_seconds(value: str) -> int:
    from datetime import datetime, timezone

    parsed = datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    return int(parsed.timestamp())


def decode_fallback_request(encoded: str) -> Any:
    """Decode the unpadded base64url workflow input into canonical JSON."""

    if "=" in encoded or not encoded.isascii():
        raise ContractError("attestation fallback request must be unpadded base64url")
    padded = encoded + "=" * ((4 - len(encoded) % 4) % 4)
    try:
        payload = base64.urlsafe_b64decode(padded.encode("ascii"))
    except ValueError as error:
        raise ContractError("attestation fallback request is not base64url") from error
    value = parse_json(payload, "attestation fallback request", max_bytes=1024 * 1024)
    if canonical_bytes(value) != payload:
        raise ContractError("attestation fallback request is not AD-5 canonical JSON")
    return value


def fallback_digest(definition: dict[str, str], package_set: str, graph_digest: str, policy_sha256: str, workflow_digest: str) -> str:
    """Return the AD-9 fallback approval digest."""

    if set(definition) != set(DEFINITION_FILES) | {"helper_version"}:
        raise ContractError("fallback definition is not the closed fingerprint set")
    for path, digest in definition.items():
        expect_sha256(digest, path)
    return canonical_digest(
        {
            "definition": definition,
            "package_set": expect_sha256(package_set, "package set fingerprint"),
            "dependency_graph": expect_sha256(graph_digest, "fallback graph digest"),
            "dependency_policy": expect_sha256(policy_sha256, "fallback policy digest"),
            "workflow_definition": expect_sha256(workflow_digest, "fallback workflow digest"),
        }
    )


def authorize_fallback(request: dict[str, Any], approvals: list[dict[str, Any]], expected: dict[str, Any]) -> dict[str, Any]:
    """Bind one approved production review comment to this Release attempt."""

    if expected["attestation_capability"] != "unsupported":
        raise ContractError("attestation fallback is unavailable while attestation capability is supported")
    request_sha = sha256_bytes(canonical_bytes(request))
    comment = (
        f"hexalith-attestation-fallback-v1:{request_sha}:{expected['run_id']}:{expected['run_attempt']}"
    )
    projected = []
    for row in approvals:
        if str(row.get("state", "")).lower() != "approved" or row.get("comment") != comment:
            continue
        environments = row.get("environments") or []
        if len(environments) != 1:
            raise ContractError("attestation fallback approval must name only production")
        environment = environments[0]
        if (
            environment.get("name") != "production"
            or environment.get("can_admins_bypass") is not False
            or expect_safe_integer(environment.get("id"), "production environment id", minimum=1) != expected["environment_id"]
        ):
            raise ContractError("attestation fallback approval environment is not the protected production environment")
        user = row.get("user") or {}
        projected.append((expect_login(user.get("login"), "fallback approver"), expect_safe_integer(user.get("id"), "fallback approver id", minimum=1)))
    unique = sorted(set(projected))
    if len(unique) != len(projected) or not unique:
        raise ContractError("attestation fallback approval has no unique authorized approver")
    owners = set(expected["release_owner_logins"])
    if any(login not in owners for login, _user_id in unique):
        raise ContractError("attestation fallback approver is not an authorized Release Owner")
    observed = expect_timestamp(expected["observed_at"], "fallback observed_at")
    if observed > request["expires_at"]:
        raise ContractError("attestation fallback approval was observed after the request expired")
    authorization = {
        "schema": FALLBACK_AUTHORIZATION_SCHEMA,
        "request": request,
        "request_sha256": request_sha,
        "release_run": {
            "repository": expected["repository"],
            "workflow_path": expected["caller_workflow"],
            "run_id": expected["run_id"],
            "run_attempt": expected["run_attempt"],
        },
        "approval": {
            "environment_id": expected["environment_id"],
            "environment_name": "production",
            "state": "approved",
            "comment": comment,
            "approvers": [{"login": login, "user_id": user_id} for login, user_id in unique],
            "observed_at": observed,
        },
    }
    parse_json(canonical_bytes(authorization), "fallback authorization", max_bytes=1024 * 1024)
    return authorization


def load_json_object(payload: bytes, context: str) -> dict[str, Any]:
    """Parse one JSON object."""

    value = parse_json(payload, context, max_bytes=16 * 1024 * 1024)
    if not isinstance(value, dict):
        raise ContractError(f"{context} must be a JSON object")
    return value
