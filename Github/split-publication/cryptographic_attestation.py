"""Verify the FrontComposer publisher's Sigstore bundle against a Release attempt."""

from __future__ import annotations

import os
import subprocess
from collections.abc import Mapping
from pathlib import Path

import attestation
from canonical import ContractError, expect_commit, expect_safe_integer, parse_json
from descriptor import FRONTCOMPOSER_PUBLISH_WORKFLOW, FRONTCOMPOSER_RELEASE_WORKFLOW, FRONTCOMPOSER_REPOSITORY
from manifest import validate_workflow_provenance


MAX_BUNDLE_BYTES = 16 * 1024 * 1024
FRONTCOMPOSER_SLUG = "Hexalith/Hexalith.FrontComposer"
_VERIFIER = Path(__file__).resolve().parent / "sigstore-verifier" / "dist.cjs"


def read_bundle(path: Path) -> bytes:
    with path.open("rb") as stream:
        bundle = stream.read(MAX_BUNDLE_BYTES + 1)
    if len(bundle) > MAX_BUNDLE_BYTES:
        raise ContractError("attestation bundle exceeds the size limit")
    return bundle


def _verified_extensions(bundle: bytes) -> dict[str, str]:
    """Obtain the certificate identity from the packaged offline verifier."""

    try:
        result = subprocess.run(
            ["node", str(_VERIFIER)],
            input=bundle,
            capture_output=True,
            # Keep the platform runtime discoverable without passing credentials or Node preload options.
            env={"PATH": os.environ.get("PATH", os.defpath)},
            check=False,
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ContractError("the packaged Sigstore verifier could not complete") from error
    if result.returncode:
        raise ContractError("Sigstore bundle cryptographic verification failed")
    extensions = parse_json(result.stdout, "verified Fulcio identity", max_bytes=16 * 1024)
    if not isinstance(extensions, dict):
        raise ContractError("Sigstore verifier did not return a certificate identity")
    return extensions


def _github_number(environment: Mapping[str, str], name: str) -> int:
    value = environment.get(name, "")
    if not value.isascii() or not value.isdecimal() or value[0] == "0" or len(value) > 16:
        raise ContractError(f"{name} must be a positive GitHub identifier")
    return expect_safe_integer(int(value), name, minimum=1)


def verify_frontcomposer_attestation(
    bundle_bytes: bytes,
    descriptor: dict,
    workflow_provenance: dict,
    *,
    builds_execution_sha: str,
    environment: Mapping[str, str],
) -> None:
    """Validate one bundle using the byte-validated descriptor and trusted runner context."""

    main_ref = attestation.MAIN_REF
    if (
        environment.get("GITHUB_SERVER_URL") != "https://github.com"
        or environment.get("GITHUB_REPOSITORY") != FRONTCOMPOSER_SLUG
        or environment.get("GITHUB_WORKFLOW_REF") != f"{FRONTCOMPOSER_SLUG}/{FRONTCOMPOSER_RELEASE_WORKFLOW}@{main_ref}"
        or environment.get("GITHUB_REF") != main_ref
        or environment.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
    ):
        raise ContractError("attestation verification requires the FrontComposer Release run on refs/heads/main")
    candidate = expect_commit(environment.get("GITHUB_SHA"), "GitHub Release candidate")
    builds_sha = expect_commit(builds_execution_sha, "Builds action execution SHA")
    run_id = _github_number(environment, "GITHUB_RUN_ID")
    run_attempt = _github_number(environment, "GITHUB_RUN_ATTEMPT")
    release_run = {
        "repository": FRONTCOMPOSER_REPOSITORY,
        "workflow_path": FRONTCOMPOSER_RELEASE_WORKFLOW,
        "run_id": run_id,
        "run_attempt": run_attempt,
    }
    if descriptor["candidate"] != candidate or descriptor["release_run"] != release_run:
        raise ContractError("authenticated descriptor does not match the current Release candidate and attempt")

    provenance = validate_workflow_provenance(workflow_provenance, candidate)
    release = provenance["release"]
    evaluator = descriptor["evaluator"]
    if (
        release["reusable"]["repository"] != FRONTCOMPOSER_REPOSITORY
        or release["reusable"]["workflow_path"] != FRONTCOMPOSER_PUBLISH_WORKFLOW
        or release["builds_execution_sha"] != builds_sha
        or evaluator["caller"] != release["caller"]
        or evaluator["reusable"] != release["reusable"]
    ):
        raise ContractError("attestation workflow provenance does not match the authenticated Release evaluator")

    packages = [(row["asset_name"], row["sha256"]) for row in descriptor["files"] if row["role"] == "package"]
    if descriptor["release_plan"] is None or len(packages) != 8:
        raise ContractError("FrontComposer attestation requires eight authenticated publication packages")
    expected = {
        "packages": packages,
        "repository_slug": FRONTCOMPOSER_SLUG,
        "caller_workflow": FRONTCOMPOSER_RELEASE_WORKFLOW,
        "repository_id": _github_number(environment, "GITHUB_REPOSITORY_ID"),
        "owner_id": _github_number(environment, "GITHUB_REPOSITORY_OWNER_ID"),
        "candidate": candidate,
        "builds_sha": builds_sha,
        "run_id": run_id,
        "run_attempt": run_attempt,
    }

    # Reject duplicate JSON keys before Node parses the exact same bytes.
    bundle = attestation.load_json_object(bundle_bytes, "attestation bundle")
    extensions = _verified_extensions(bundle_bytes)
    _payload, _signature, statement = attestation.decode_statement(bundle)
    attestation.validate_statement(
        statement, expected, workflow_provenance=provenance, verified_certificate_extensions=extensions
    )
