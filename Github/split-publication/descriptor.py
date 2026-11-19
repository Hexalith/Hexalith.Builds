"""Closed hexalith.publication-candidate.v1 descriptor."""

from __future__ import annotations

from typing import Any

from canonical import (
    ContractError,
    expect_asset_name,
    expect_commit,
    expect_identity,
    expect_members,
    expect_path,
    expect_safe_integer,
    expect_sha256,
    expect_string,
    expect_version,
    sha256_bytes,
)


DESCRIPTOR_SCHEMA = "hexalith.publication-candidate.v1"
FILE_ROLES = frozenset({"package", "symbol", "evidence", "release-metadata"})
BUILDS_REPOSITORY = "github.com/hexalith/hexalith.builds"
BUILDS_CI_WORKFLOW = ".github/workflows/domain-ci.yml"
BUILDS_RELEASE_WORKFLOW = ".github/workflows/domain-release.yml"
FRONTCOMPOSER_REPOSITORY = "github.com/hexalith/hexalith.frontcomposer"
FRONTCOMPOSER_RELEASE_WORKFLOW = ".github/workflows/release.yml"
FRONTCOMPOSER_PUBLISH_WORKFLOW = ".github/workflows/release-publish.yml"
MAX_NOTES_BYTES = 1024 * 1024


def validate_evaluator(
    value: Any,
    context: str = "release evaluator",
    *,
    candidate: str | None = None,
    ci: bool = False,
) -> dict[str, Any]:
    """Validate one AD-13 evaluator projection and recompute its definition digest."""

    from canonical import canonical_digest

    evaluator = expect_members(value, {"caller", "reusable", "actions", "definition_digest"}, context)
    caller_members = {"repository", "workflow_path", "blob_sha256"}
    caller_value = evaluator["caller"]
    if ci or (isinstance(caller_value, dict) and "commit" in caller_value):
        caller_members.add("commit")
    caller = expect_members(caller_value, caller_members, f"{context} caller")
    expect_identity(caller["repository"], f"{context} caller repository")
    expect_path(caller["workflow_path"], f"{context} caller workflow", max_depth=16, max_bytes=240)
    expect_sha256(caller["blob_sha256"], f"{context} caller blob")
    caller_commit = None
    if "commit" in caller:
        caller_commit = expect_commit(caller["commit"], f"{context} caller commit")
        if candidate is not None and caller_commit != candidate:
            raise ContractError(f"{context} caller commit does not match the candidate")
    reusable = expect_members(
        evaluator["reusable"],
        {"repository", "workflow_path", "commit", "blob_sha256"},
        f"{context} reusable",
    )
    repository = expect_identity(reusable["repository"], f"{context} reusable repository")
    workflow = expect_string(reusable["workflow_path"], f"{context} reusable workflow")
    reusable_commit = expect_commit(reusable["commit"], f"{context} reusable commit")
    if ci:
        if (repository, workflow) != (BUILDS_REPOSITORY, BUILDS_CI_WORKFLOW):
            raise ContractError(f"{context} must select the Builds CI workflow")
    elif (repository, workflow) == (FRONTCOMPOSER_REPOSITORY, FRONTCOMPOSER_PUBLISH_WORKFLOW):
        if (
            caller["repository"] != FRONTCOMPOSER_REPOSITORY
            or caller["workflow_path"] != FRONTCOMPOSER_RELEASE_WORKFLOW
            or candidate is None
            or caller_commit != candidate
            or reusable_commit != candidate
        ):
            raise ContractError(f"{context} must bind FrontComposer release and publisher to the candidate")
    elif (repository, workflow) != (BUILDS_REPOSITORY, BUILDS_RELEASE_WORKFLOW):
        raise ContractError(f"{context} must select an eligible release workflow")
    expect_sha256(reusable["blob_sha256"], f"{context} reusable blob")
    actions = evaluator["actions"]
    if not isinstance(actions, list):
        raise ContractError(f"{context} actions must be an array")
    normalized_actions = []
    for index, action in enumerate(actions):
        row = expect_members(
            action,
            {"repository", "path", "commit", "blob_sha256"},
            f"{context} action[{index}]",
        )
        normalized_actions.append(
            {
                "repository": expect_identity(row["repository"], f"{context} action[{index}] repository"),
                "path": expect_path(row["path"], f"{context} action[{index}] path", max_depth=16),
                "commit": expect_commit(row["commit"], f"{context} action[{index}] commit"),
                "blob_sha256": expect_sha256(row["blob_sha256"], f"{context} action[{index}] blob"),
            }
        )
    action_keys = [
        (row["repository"], row["path"], row["commit"], row["blob_sha256"]) for row in normalized_actions
    ]
    if action_keys != sorted(action_keys) or len(action_keys) != len(set(action_keys)):
        raise ContractError(f"{context} actions must be unique and ordinally sorted")
    materialized = {
        "caller": dict(caller),
        "reusable": {
            "repository": reusable["repository"],
            "workflow_path": reusable["workflow_path"],
            "commit": reusable["commit"],
            "blob_sha256": reusable["blob_sha256"],
        },
        "actions": normalized_actions,
    }
    digest = canonical_digest(materialized)
    if expect_sha256(evaluator["definition_digest"], f"{context} definition digest") != digest:
        raise ContractError(f"{context} definition_digest does not match its canonical bytes")
    return {**materialized, "definition_digest": digest}


def validate_policy_projection(value: Any, context: str = "dependency policy") -> dict[str, str]:
    """Validate the AD-14 policy projection carried by a descriptor."""

    projection = expect_members(value, {"schema", "repository", "path", "commit", "sha256"}, context)
    if projection["schema"] != "hexalith.dependency-graph-policy.v1":
        raise ContractError(f"{context} schema is not the v1 policy schema")
    if expect_string(projection["path"], f"{context} path") != "eng/dependency-graph-policy.json":
        raise ContractError(f"{context} path must be eng/dependency-graph-policy.json")
    return {
        "schema": "hexalith.dependency-graph-policy.v1",
        "repository": expect_identity(projection["repository"], f"{context} repository"),
        "path": "eng/dependency-graph-policy.json",
        "commit": expect_commit(projection["commit"], f"{context} commit"),
        "sha256": expect_sha256(projection["sha256"], f"{context} sha256"),
    }


def validate_release_plan(value: Any, files: dict[str, dict[str, Any]]) -> dict[str, str] | None:
    """Validate a null or closed release plan against descriptor files."""

    if value is None:
        return None
    plan = expect_members(value, {"version", "tag", "release_name", "release_notes_path"}, "release plan")
    version = expect_version(plan["version"], "release plan version")
    tag = expect_string(plan["tag"], "release plan tag")
    name = expect_string(plan["release_name"], "release plan name")
    notes = expect_path(plan["release_notes_path"], "release notes path")
    if tag != f"v{version}" or not name:
        raise ContractError("release plan tag must be v plus the canonical version")
    row = files.get(notes)
    if row is None or row["role"] != "release-metadata" or row["asset_name"] is not None:
        raise ContractError("release notes must be a declared release-metadata file without a destination asset name")
    return {"version": version, "tag": tag, "release_name": name, "release_notes_path": notes}


def validate_descriptor(value: Any, members: dict[str, bytes] | None = None) -> dict[str, Any]:
    """Validate one publication-candidate descriptor and, when supplied, its archive bytes."""

    document = expect_members(
        value,
        {
            "schema",
            "release_run",
            "candidate",
            "ci_handoff_sha256",
            "dependency_policy",
            "evaluator",
            "release_plan",
            "files",
        },
        "publication candidate",
    )
    if document["schema"] != DESCRIPTOR_SCHEMA:
        raise ContractError(f"publication candidate schema must be {DESCRIPTOR_SCHEMA}")
    release_run = expect_members(
        document["release_run"],
        {"repository", "workflow_path", "run_id", "run_attempt"},
        "publication candidate release_run",
    )
    normalized_run = {
        "repository": expect_identity(release_run["repository"], "release_run repository"),
        "workflow_path": expect_path(release_run["workflow_path"], "release_run workflow", max_depth=16),
        "run_id": expect_safe_integer(release_run["run_id"], "release_run run_id", minimum=1),
        "run_attempt": expect_safe_integer(release_run["run_attempt"], "release_run run_attempt", minimum=1),
    }
    files = document["files"]
    if not isinstance(files, list):
        raise ContractError("publication candidate files must be an array")
    normalized_files: list[dict[str, Any]] = []
    asset_names: list[str] = []
    for index, row in enumerate(files):
        item = expect_members(row, {"path", "asset_name", "sha256", "size", "role"}, f"files[{index}]")
        path = expect_path(item["path"], f"files[{index}] path")
        role = expect_string(item["role"], f"files[{index}] role")
        if role not in FILE_ROLES:
            raise ContractError(f"files[{index}] role is not closed")
        asset_name = None if item["asset_name"] is None else expect_asset_name(item["asset_name"], f"files[{index}] asset_name")
        if role == "release-metadata" and asset_name is not None:
            raise ContractError(f"files[{index}] release metadata cannot be a destination asset")
        if role != "release-metadata" and asset_name is None:
            raise ContractError(f"files[{index}] requires a destination asset name")
        normalized_files.append(
            {
                "path": path,
                "asset_name": asset_name,
                "sha256": expect_sha256(item["sha256"], f"files[{index}] sha256"),
                "size": expect_safe_integer(item["size"], f"files[{index}] size"),
                "role": role,
            }
        )
        if asset_name is not None:
            asset_names.append(asset_name)
    paths = [row["path"] for row in normalized_files]
    if paths != sorted(paths) or len(paths) != len(set(paths)) or len(asset_names) != len(set(asset_names)):
        raise ContractError("publication candidate files or destination asset names are not unique and sorted")
    file_map = {row["path"]: row for row in normalized_files}
    plan = validate_release_plan(document["release_plan"], file_map)
    candidate = expect_commit(document["candidate"], "publication candidate commit")
    result = {
        "schema": DESCRIPTOR_SCHEMA,
        "release_run": normalized_run,
        "candidate": candidate,
        "ci_handoff_sha256": expect_sha256(document["ci_handoff_sha256"], "ci handoff sha256"),
        "dependency_policy": validate_policy_projection(document["dependency_policy"]),
        "evaluator": validate_evaluator(document["evaluator"], candidate=candidate),
        "release_plan": plan,
        "files": normalized_files,
    }
    if members is not None:
        declared = set(file_map)
        actual = set(members) - {"publication-candidate.json"}
        if declared != actual:
            raise ContractError("publication archive members do not match the descriptor inventory")
        for path, row in file_map.items():
            payload = members[path]
            if len(payload) != row["size"] or sha256_bytes(payload) != row["sha256"]:
                raise ContractError(f"publication archive member {path} does not match its descriptor digest")
            if row["role"] == "release-metadata" and (len(payload) > MAX_NOTES_BYTES or b"\x00" in payload):
                raise ContractError("release notes must be UTF-8 of at most 1 MiB")
            if row["role"] == "release-metadata":
                payload.decode("utf-8")
    return result
