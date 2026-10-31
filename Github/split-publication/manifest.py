"""hexalith.release-evidence.v4 sealing and offline verification.

The closed member set is the set AD-14 and AD-17 name. FrontComposer still owns any
later addition to that set; this publisher does not reseal v2 or v3 evidence.
"""

from __future__ import annotations

from typing import Any

from canonical import (
    ContractError,
    canonical_bytes,
    canonical_digest,
    expect_commit,
    expect_identity,
    expect_members,
    expect_path,
    expect_safe_integer,
    expect_sha256,
    expect_string,
    expect_timestamp,
    sha256_bytes,
)
from descriptor import validate_policy_projection
from graph import validate_graph


MANIFEST_SCHEMA = "hexalith.release-evidence.v4"
ATTESTED_BUNDLE = "provenance-attestation.json"
FALLBACK_RECORD = "attestation-fallback-authorization.json"
MANIFEST_PATH = "release-evidence.json"


def validate_quality_run(value: Any) -> dict[str, Any]:
    """Validate the deny-only quality-run projection carried by manifest v4."""

    run = expect_members(
        value,
        {"repository", "workflow_path", "run_id", "run_attempt", "event", "branch", "head_sha", "conclusion"},
        "quality run",
    )
    if run["event"] != "push" or run["branch"] != "main" or run["conclusion"] != "success":
        raise ContractError("quality run must be the successful push run on main")
    if run["workflow_path"] != ".github/workflows/quality.yml":
        raise ContractError("quality run workflow must be .github/workflows/quality.yml")
    return {
        "repository": expect_identity(run["repository"], "quality run repository"),
        "workflow_path": ".github/workflows/quality.yml",
        "run_id": expect_safe_integer(run["run_id"], "quality run id", minimum=1),
        "run_attempt": expect_safe_integer(run["run_attempt"], "quality run attempt", minimum=1),
        "event": "push",
        "branch": "main",
        "head_sha": expect_commit(run["head_sha"], "quality run head"),
        "conclusion": "success",
    }


def validate_attestation_projection(value: Any) -> dict[str, Any]:
    """Validate the closed AD-15 attestation projection."""

    projection = expect_members(value, {"status", "bundle", "fallback"}, "attestation")
    status = expect_string(projection["status"], "attestation status")
    if status == "attested":
        bundle = expect_members(projection["bundle"], {"path", "sha256"}, "attestation bundle")
        if projection["fallback"] is not None or bundle["path"] != ATTESTED_BUNDLE:
            raise ContractError("an attested projection requires provenance-attestation.json and no fallback")
        return {
            "status": "attested",
            "bundle": {"path": ATTESTED_BUNDLE, "sha256": expect_sha256(bundle["sha256"], "attestation bundle sha256")},
            "fallback": None,
        }
    if status == "approved-unsupported":
        fallback = expect_members(projection["fallback"], {"path", "sha256"}, "attestation fallback")
        if projection["bundle"] is not None or fallback["path"] != FALLBACK_RECORD:
            raise ContractError("an approved fallback requires attestation-fallback-authorization.json and no bundle")
        return {
            "status": "approved-unsupported",
            "bundle": None,
            "fallback": {
                "path": FALLBACK_RECORD,
                "sha256": expect_sha256(fallback["sha256"], "attestation fallback sha256"),
            },
        }
    raise ContractError("attestation status must be attested or approved-unsupported")


def validate_workflow_provenance(value: Any, candidate: str) -> dict[str, Any]:
    """Validate AD-17 workflow provenance and recompute its definition digest."""

    provenance = expect_members(value, {"ci", "release", "definition_digest"}, "workflow provenance")
    ci = expect_members(provenance["ci"], {"run", "evidence_sha256"}, "workflow provenance ci")
    run = expect_members(
        ci["run"],
        {"repository", "workflow_path", "run_id", "run_attempt", "event", "branch", "head_sha"},
        "workflow provenance ci run",
    )
    if run["event"] != "push" or run["branch"] != "main" or run["workflow_path"] != ".github/workflows/ci.yml":
        raise ContractError("workflow provenance CI run must be the push of ci.yml on main")
    head = expect_commit(run["head_sha"], "workflow provenance CI head")
    if head != candidate:
        raise ContractError("workflow provenance CI head does not match the candidate")
    release = expect_members(
        provenance["release"],
        {"caller", "reusable", "builds_execution_sha"},
        "workflow provenance release",
    )
    caller = expect_members(
        release["caller"],
        {"repository", "workflow_path", "commit", "blob_sha256"},
        "workflow provenance caller",
    )
    reusable = expect_members(
        release["reusable"],
        {"repository", "workflow_path", "commit", "blob_sha256"},
        "workflow provenance reusable",
    )
    builds_sha = expect_commit(release["builds_execution_sha"], "builds execution sha")
    if (
        expect_identity(reusable["repository"], "workflow provenance reusable repository") != "github.com/hexalith/hexalith.builds"
        or reusable["workflow_path"] != ".github/workflows/domain-release.yml"
        or expect_commit(reusable["commit"], "workflow provenance reusable commit") != builds_sha
    ):
        raise ContractError("workflow provenance reusable coordinate is not the selected Builds execution SHA")
    normalized = {
        "ci": {
            "run": {
                "repository": expect_identity(run["repository"], "workflow provenance CI repository"),
                "workflow_path": ".github/workflows/ci.yml",
                "run_id": expect_safe_integer(run["run_id"], "workflow provenance CI run", minimum=1),
                "run_attempt": expect_safe_integer(run["run_attempt"], "workflow provenance CI attempt", minimum=1),
                "event": "push",
                "branch": "main",
                "head_sha": head,
            },
            "evidence_sha256": expect_sha256(ci["evidence_sha256"], "workflow provenance CI evidence"),
        },
        "release": {
            "caller": {
                "repository": expect_identity(caller["repository"], "workflow provenance caller repository"),
                "workflow_path": expect_path(caller["workflow_path"], "workflow provenance caller workflow", max_depth=16),
                "commit": expect_commit(caller["commit"], "workflow provenance caller commit"),
                "blob_sha256": expect_sha256(caller["blob_sha256"], "workflow provenance caller blob"),
            },
            "reusable": {
                "repository": "github.com/hexalith/hexalith.builds",
                "workflow_path": ".github/workflows/domain-release.yml",
                "commit": builds_sha,
                "blob_sha256": expect_sha256(reusable["blob_sha256"], "workflow provenance reusable blob"),
            },
            "builds_execution_sha": builds_sha,
        },
    }
    digest = canonical_digest({"ci": normalized["ci"], "release": normalized["release"]})
    if expect_sha256(provenance["definition_digest"], "workflow provenance digest") != digest:
        raise ContractError("workflow provenance definition_digest does not match its canonical bytes")
    return {**normalized, "definition_digest": digest}


def seal_manifest(
    *,
    dependency_graph: dict[str, Any],
    dependency_policy: dict[str, Any],
    quality_run: dict[str, Any],
    attestation: dict[str, Any],
    workflow_provenance: dict[str, Any],
    candidate: str,
    sealed_at: str,
) -> dict[str, Any]:
    """Seal a v4 manifest. sealed_at is unsealed metadata and is not a trust input."""

    body = {
        "schema": MANIFEST_SCHEMA,
        "dependency_graph": validate_graph(dependency_graph),
        "dependency_policy": validate_policy_projection(dependency_policy),
        "quality_run": validate_quality_run(quality_run),
        "attestation": validate_attestation_projection(attestation),
        "workflow_provenance": validate_workflow_provenance(workflow_provenance, candidate),
    }
    if body["dependency_graph"]["root"]["commit"] != candidate or body["quality_run"]["head_sha"] != candidate:
        raise ContractError("manifest candidate does not match the sealed graph and quality run")
    sealed = {
        **body,
        "seal": {
            "algorithm": "sha256",
            "hash": sha256_bytes(canonical_bytes(body)),
            "sealed_at": expect_timestamp(sealed_at, "manifest sealed_at"),
        },
    }
    return verify_manifest(sealed, candidate)


def verify_manifest(value: Any, candidate: str) -> dict[str, Any]:
    """Offline-verify a v4 manifest, including its seal and nested digests."""

    document = expect_members(
        value,
        {
            "schema",
            "dependency_graph",
            "dependency_policy",
            "quality_run",
            "attestation",
            "workflow_provenance",
            "seal",
        },
        "release manifest",
    )
    if document["schema"] != MANIFEST_SCHEMA:
        raise ContractError(f"release manifest schema must be {MANIFEST_SCHEMA}")
    seal = expect_members(document["seal"], {"algorithm", "hash", "sealed_at"}, "manifest seal")
    if seal["algorithm"] != "sha256":
        raise ContractError("manifest seal algorithm must be sha256")
    body = {key: document[key] for key in document if key != "seal"}
    graph = validate_graph(body["dependency_graph"])
    policy = validate_policy_projection(body["dependency_policy"])
    quality = validate_quality_run(body["quality_run"])
    attestation = validate_attestation_projection(body["attestation"])
    provenance = validate_workflow_provenance(body["workflow_provenance"], candidate)
    if graph["root"]["commit"] != candidate or quality["head_sha"] != candidate:
        raise ContractError("manifest candidate does not match the sealed graph and quality run")
    normalized = {
        "schema": MANIFEST_SCHEMA,
        "dependency_graph": graph,
        "dependency_policy": policy,
        "quality_run": quality,
        "attestation": attestation,
        "workflow_provenance": provenance,
    }
    digest = sha256_bytes(canonical_bytes(normalized))
    if expect_sha256(seal["hash"], "manifest seal hash") != digest:
        raise ContractError("manifest seal does not match its canonical body")
    return {
        **normalized,
        "seal": {
            "algorithm": "sha256",
            "hash": digest,
            "sealed_at": expect_timestamp(seal["sealed_at"], "manifest sealed_at"),
        },
    }
