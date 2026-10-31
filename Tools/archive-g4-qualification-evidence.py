#!/usr/bin/env python3
"""Retain existing qualification bytes with their inventory and producer identity."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile


ARCHIVE_NAME = "g4-tool-qualification-evidence.zip"
INVENTORY_NAME = "g4-tool-package-inventory.json"
REPOSITORY = Path(__file__).resolve().parents[1]


def unique_properties(pairs):
    """Reject ambiguous JSON rather than selecting one duplicate value."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate inventory property: {key}")
        result[key] = value
    return result


def validated_payload(directory: Path, kind: str) -> dict[str, bytes]:
    """Read existing evidence and verify its inventory, source and run identity."""
    if any(path.is_symlink() for path in (directory, *directory.parents)):
        raise ValueError("Package directory must not be a symlink")
    directory = directory.resolve(strict=True)
    inventory_path = directory / INVENTORY_NAME
    if inventory_path.is_symlink():
        raise ValueError("Inventory must not be a symlink")
    inventory_bytes = inventory_path.read_bytes()
    inventory = json.loads(inventory_bytes, object_pairs_hook=unique_properties)
    if inventory["schema"] != "hexalith.g4-tool-package-inventory.v1" or inventory["configuration"] != "Release":
        raise ValueError("Expected a Release qualification inventory")
    if not isinstance(inventory["version"], str) or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?", inventory["version"]):
        raise ValueError("Expected an explicit qualification package version")
    qualification = inventory["qualification"]
    if kind == "release" and qualification["releaseEligible"] is not True:
        raise ValueError("Release carrier requires release-eligible qualification")
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPOSITORY, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain=v1"], cwd=REPOSITORY, text=True).strip()
    if (not re.fullmatch(r"[0-9a-f]{40}", source)
            or dirty
            or qualification["sourceTree"]["clean"] is not True
            or qualification["sourceTree"]["revision"] != source
            or os.environ.get("GITHUB_SHA") != source):
        raise ValueError("Inventory, clean source and GitHub source identity must agree")

    required = ("GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB",
                "GITHUB_WORKFLOW_REF", "GITHUB_WORKFLOW_SHA", "GITHUB_EVENT_NAME")
    identity = {key: os.environ.get(key) for key in required}
    workflow = "build-release.yml" if kind == "release" else "ci.yml"
    if (any(not value for value in identity.values())
            or identity["GITHUB_REPOSITORY"] != "Hexalith/Hexalith.Builds"
            or not re.fullmatch(r"[1-9][0-9]*", identity["GITHUB_RUN_ID"])
            or not re.fullmatch(r"[1-9][0-9]*", identity["GITHUB_RUN_ATTEMPT"])
            or identity["GITHUB_WORKFLOW_SHA"] != source
            or not re.fullmatch(rf"Hexalith/Hexalith.Builds/\.github/workflows/{re.escape(workflow)}@refs/(?:heads/(?:main|prerelease)|pull/[1-9][0-9]*/merge)", identity["GITHUB_WORKFLOW_REF"])):
        raise ValueError("Complete matching producer run/workflow identity is required")
    if kind == "release":
        if (identity["GITHUB_JOB"] != "release" or identity["GITHUB_EVENT_NAME"] != "workflow_dispatch"
                or "/merge" in identity["GITHUB_WORKFLOW_REF"]):
            raise ValueError("Release carrier must identify the protected release workflow job")
    elif (identity["GITHUB_JOB"] != "contracts" or identity["GITHUB_EVENT_NAME"] not in ("push", "pull_request")
          or (identity["GITHUB_EVENT_NAME"] == "pull_request") != ("/merge" in identity["GITHUB_WORKFLOW_REF"])):
        raise ValueError("CI carrier must identify the CI contracts job and event ref")

    evidence_root = directory / "qualification-evidence"
    if evidence_root.is_symlink() or not evidence_root.is_dir():
        raise ValueError("Qualification evidence directory is missing or aliased")
    payload = {INVENTORY_NAME: inventory_bytes}
    names = set()
    rows = inventory["qualificationEvidence"]
    if not rows:
        raise ValueError("Qualification evidence inventory must not be empty")
    for row in rows:
        name = row["file"]
        if (not re.fullmatch(r"qualification-evidence/[A-Za-z0-9][A-Za-z0-9._-]*", name)
                or name.casefold() in names):
            raise ValueError(f"Unsafe or duplicate evidence filename: {name}")
        names.add(name.casefold())
        path = directory / name
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"Evidence file is missing or aliased: {name}")
        data = path.read_bytes()
        if (type(row["sizeBytes"]) is not int or row["sizeBytes"] != len(data)
                or not re.fullmatch(r"[0-9A-Fa-f]{64}", row["sha256"])
                or hashlib.sha256(data).hexdigest().upper() != row["sha256"].upper()):
            raise ValueError(f"Evidence hash or size mismatch: {name}")
        payload[name] = data
    actual = set()
    for path in evidence_root.rglob("*"):
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"Unexpected evidence directory or alias: {path.name}")
        actual.add(path.relative_to(directory).as_posix())
    if actual != set(payload) - {INVENTORY_NAME}:
        raise ValueError("Declared and retained evidence filenames differ")

    identity.update(schema="hexalith.g4-qualification-carrier.v1", kind=kind,
                    version=inventory["version"], sourceRevision=source,
                    inventorySha256=hashlib.sha256(inventory_bytes).hexdigest(),
                    qualificationEvidenceFiles=len(rows))
    payload["producer-run.json"] = (json.dumps(identity, indent=2) + "\n").encode("utf-8")
    return payload


def archive_evidence(directory: Path, kind: str) -> Path:
    """Atomically retain verified bytes without replacing an existing carrier."""
    payload = validated_payload(directory, kind)
    destination = directory / ARCHIVE_NAME
    descriptor, staging = tempfile.mkstemp(prefix=".g4-carrier-", suffix=".tmp", dir=directory)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for name, data in sorted(payload.items()):
                    archive.writestr(name, data)
            stream.flush()
            os.fsync(stream.fileno())
        # Same-filesystem link is atomic and fails if the destination exists.
        # The staging name is removed below; the final archive has one link.
        os.link(staging, destination)
    except FileExistsError:
        raise ValueError("Qualification carrier already exists; use a fresh attempt") from None
    finally:
        Path(staging).unlink(missing_ok=True)
    return destination


def verify_archive(directory: Path, kind: str) -> Path:
    """Require the carrier's exact inventory, evidence and current producer identity."""
    payload = validated_payload(directory, kind)
    path = directory / ARCHIVE_NAME
    if path.is_symlink():
        raise ValueError("Qualification carrier must not be aliased")
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(payload) or set(names) != set(payload):
            raise ValueError("Qualification carrier member inventory differs")
        for name, data in payload.items():
            if archive.read(name) != data:
                raise ValueError(f"Qualification carrier bytes differ: {name}")
    return path


def record_attempt(directory: Path, kind: str) -> Path:
    """Retain diagnostic identity even when qualification never completed."""
    identity = {key: os.environ.get(key) for key in (
        "GITHUB_REPOSITORY", "GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT",
        "GITHUB_JOB", "GITHUB_WORKFLOW_REF", "GITHUB_WORKFLOW_SHA", "GITHUB_EVENT_NAME")}
    identity.update(schema="hexalith.g4-qualification-attempt.v1", kind=kind,
                    producerStepOutcome=os.environ.get("G4_PRODUCER_STEP_OUTCOME"),
                    qualificationCredit=False, purpose="diagnostic identity; not completed qualification evidence")
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "producer-attempt.json"
    with path.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(identity, indent=2) + "\n")
    return path


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-directory", type=Path, required=True)
    parser.add_argument("--kind", choices=("ci", "release"), required=True)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--verify", action="store_true", help="Verify an existing carrier without writing")
    modes.add_argument("--record-attempt", action="store_true", help="Record diagnostic attempt identity only")
    args = parser.parse_args()
    operation = record_attempt if args.record_attempt else verify_archive if args.verify else archive_evidence
    print(operation(args.package_directory, args.kind))
