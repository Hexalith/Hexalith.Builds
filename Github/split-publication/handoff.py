"""CI handoff binding and hexalith.release-verification-handoff.v3 emission."""

from __future__ import annotations

from typing import Any

from canonical import (
    ContractError,
    expect_commit,
    expect_identity,
    expect_members,
    expect_path,
    expect_safe_integer,
    expect_sha256,
    expect_string,
    sha256_bytes,
)
from descriptor import validate_evaluator, validate_policy_projection
from graph import validate_graph


CI_HANDOFF_SCHEMA = "hexalith.dependency-release-handoff.v1"
VERIFICATION_HANDOFF_SCHEMA = "hexalith.release-verification-handoff.v3"
JOB_RESULTS = frozenset({"success", "failure", "cancelled", "skipped"})
DENIALS = frozenset(
    {
        "builder-failed",
        "attestation-unavailable",
        "fallback-not-authorized",
        "manifest-invalid",
        "live-verification-failed",
        "classification-blocked",
    }
)
DURABLE_ASSETS = frozenset(
    {
        "publication-candidate.json",
        "dependency-release-handoff.json",
        "release-evidence.json",
        "provenance-attestation.json",
        "attestation-fallback-authorization.json",
    }
)


def validate_ci_handoff(value: Any, raw: bytes) -> dict[str, Any]:
    """Validate one CI handoff and bind its raw digest."""

    document = expect_members(
        value,
        {"schema", "run", "revisions", "evaluator", "dependency_policy", "dependency_graph"},
        "CI handoff",
    )
    if document["schema"] != CI_HANDOFF_SCHEMA:
        raise ContractError(f"CI handoff schema must be {CI_HANDOFF_SCHEMA}")
    run = expect_members(
        document["run"],
        {"repository", "workflow_path", "run_id", "run_attempt", "event", "branch", "candidate"},
        "CI handoff run",
    )
    if run["event"] != "push" or run["branch"] != "main" or run["workflow_path"] != ".github/workflows/ci.yml":
        raise ContractError("CI handoff run must be a push of ci.yml on main")
    revisions = expect_members(document["revisions"], {"base", "candidate", "merge_base"}, "CI handoff revisions")
    if revisions["merge_base"] is not None:
        raise ContractError("push CI handoff merge_base must be null")
    candidate = expect_commit(run["candidate"], "CI handoff candidate")
    if expect_commit(revisions["candidate"], "CI handoff revision candidate") != candidate:
        raise ContractError("CI handoff revisions do not match its run candidate")
    policy = validate_policy_projection(document["dependency_policy"], "CI handoff policy")
    if policy["commit"] != expect_commit(revisions["base"], "CI handoff base"):
        raise ContractError("CI handoff policy commit does not equal revisions.base")
    graph = validate_graph(document["dependency_graph"])
    if graph["root"]["repository"] != policy["repository"] or graph["root"]["commit"] != candidate:
        raise ContractError("CI handoff graph root does not match the candidate and policy repository")
    return {
        "schema": CI_HANDOFF_SCHEMA,
        "run": {
            "repository": expect_identity(run["repository"], "CI handoff repository"),
            "workflow_path": ".github/workflows/ci.yml",
            "run_id": expect_safe_integer(run["run_id"], "CI handoff run id", minimum=1),
            "run_attempt": expect_safe_integer(run["run_attempt"], "CI handoff run attempt", minimum=1),
            "event": "push",
            "branch": "main",
            "candidate": candidate,
        },
        "revisions": {"base": policy["commit"], "candidate": candidate, "merge_base": None},
        "evaluator": validate_evaluator(document["evaluator"], "CI handoff evaluator"),
        "dependency_policy": policy,
        "dependency_graph": graph,
        "evidence_sha256": sha256_bytes(raw),
    }


def project_conclusion(job_results: dict[str, str]) -> str:
    """Project the AD-15 three-state conclusion from closed job results."""

    if set(job_results) != {"build-publication-candidate", "publish-publication-candidate"}:
        raise ContractError("split handoff job_results must name exactly the builder and publisher")
    values = []
    for name in ("build-publication-candidate", "publish-publication-candidate"):
        value = job_results[name]
        if value not in JOB_RESULTS:
            raise ContractError(f"split handoff job result {name} is not closed")
        values.append(value)
    if "failure" in values:
        return "failure"
    if "cancelled" in values:
        return "cancelled"
    return "success"


def build_verification_handoff(value: dict[str, Any]) -> dict[str, Any]:
    """Build one closed handoff v3 document from owner-controlled fields."""

    job_results = value["job_results"]
    conclusion = project_conclusion(job_results)
    denial = value["prepublication_denial"]
    if denial is not None and denial not in DENIALS:
        raise ContractError("prepublication denial is not a closed reason")
    assets = value["assets"]
    if not isinstance(assets, list):
        raise ContractError("handoff assets must be an array")
    normalized_assets = []
    for index, asset in enumerate(assets):
        row = expect_members(asset, {"name", "sha256", "size"}, f"handoff asset[{index}]")
        normalized_assets.append(
            {
                "name": expect_string(row["name"], f"handoff asset[{index}] name"),
                "sha256": expect_sha256(row["sha256"], f"handoff asset[{index}] sha256"),
                "size": expect_safe_integer(row["size"], f"handoff asset[{index}] size"),
            }
        )
    names = [asset["name"] for asset in normalized_assets]
    if names != sorted(names) or len(names) != len(set(names)):
        raise ContractError("handoff assets must be unique and sorted by name")
    release = expect_members(
        value["release"],
        {"mode", "version", "tag", "github_release_id", "publication_started", "published", "publish_gate_variable"},
        "handoff release",
    )
    if release["mode"] != "split-publication-v1":
        raise ContractError("handoff release mode must be split-publication-v1")
    for flag in ("publication_started", "published"):
        if not isinstance(release[flag], bool):
            raise ContractError(f"handoff {flag} must be a JSON boolean")
    if release["published"] and not release["publication_started"]:
        raise ContractError("handoff cannot report published without publication_started")
    publication_candidate = value["publication_candidate"]
    if publication_candidate is not None:
        publication_candidate = expect_members(
            publication_candidate,
            {"artifact_name", "artifact_id", "run_id", "run_attempt", "archive_sha256", "descriptor_sha256"},
            "publication candidate coordinate",
        )
    manifest = value["manifest"]
    if manifest is not None:
        manifest = expect_members(manifest, {"schema", "path", "sha256", "seal"}, "handoff manifest")
        if manifest["schema"] != "hexalith.release-evidence.v4" or manifest["path"] != "release-evidence.json":
            raise ContractError("handoff manifest projection is not release-evidence v4")
        manifest = {
            "schema": "hexalith.release-evidence.v4",
            "path": "release-evidence.json",
            "sha256": expect_sha256(manifest["sha256"], "handoff manifest sha256"),
            "seal": expect_sha256(manifest["seal"], "handoff manifest seal"),
        }
    attestation = None if value["attestation"] is None else value["attestation"]
    document = {
        "schema": VERIFICATION_HANDOFF_SCHEMA,
        "release_run": value["release_run"],
        "quality_run": value["quality_run"],
        "ci_handoff": value["ci_handoff"],
        "candidate": expect_commit(value["candidate"], "handoff candidate"),
        "dependency_policy": validate_policy_projection(value["dependency_policy"], "handoff policy"),
        "publication_candidate": publication_candidate,
        "release": {
            "mode": "split-publication-v1",
            "version": release["version"],
            "tag": release["tag"],
            "github_release_id": release["github_release_id"],
            "publication_started": release["publication_started"],
            "published": release["published"],
            "publish_gate_variable": expect_string(release["publish_gate_variable"], "publish gate variable"),
        },
        "prepublication_denial": denial,
        "manifest": manifest,
        "attestation": attestation,
        "assets": normalized_assets,
        "evaluator": validate_evaluator(value["evaluator"], "handoff evaluator"),
    }
    release_run = expect_members(
        value["release_run"],
        {"repository", "workflow_path", "run_id", "run_attempt"},
        "handoff release run",
    )
    document["release_run"] = {
        "repository": expect_identity(release_run["repository"], "handoff release repository"),
        "workflow_path": expect_path(release_run["workflow_path"], "handoff release workflow", max_depth=16),
        "run_id": expect_safe_integer(release_run["run_id"], "handoff release run", minimum=1),
        "run_attempt": expect_safe_integer(release_run["run_attempt"], "handoff release attempt", minimum=1),
        "job_results": {
            "build-publication-candidate": job_results["build-publication-candidate"],
            "publish-publication-candidate": job_results["publish-publication-candidate"],
        },
        "conclusion": conclusion,
    }
    return document


def durable_asset_names(run_id: int, run_attempt: int, descriptor_names: list[str], authorization_name: str) -> list[str]:
    """Return the sorted destination names, rejecting any collision."""

    archive_name = f"publication-candidate-{run_id}-{run_attempt}.zip"
    names = [*descriptor_names, archive_name, "publication-candidate.json", "dependency-release-handoff.json", "release-evidence.json", authorization_name]
    if len(names) != len(set(names)):
        raise ContractError("destination asset names collide before publication")
    return sorted(names)
