#!/usr/bin/env python3
"""Prepare and verify GitHub build provenance for released OCI indexes."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess  # nosec B404 -- invokes the GitHub CLI with fixed options.
import sys

from oci_registry_validator import (
    OCI_INDEX_MEDIA_TYPE, REGISTRY_PATTERN, TAG_PATTERN, workspace_input_file, workspace_read_bytes,
)


ISSUER = "https://token.actions.githubusercontent.com"
PROVENANCE = "https://slsa.dev/provenance/v1"
BUILDS_WORKFLOW = "Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml"
REPOSITORY_NAME = r"[a-z0-9]+(?:[._-][a-z0-9]+)*"
SHA256 = r"[0-9a-f]{64}"
COMMIT = r"[0-9a-f]{40}"


def subject_row(evidence_directory, image):
    """Bind a checksum row to the validated index bytes and release image name."""
    record = workspace_input_file(str(evidence_directory / "oci-validation.json"))
    raw = workspace_input_file(str(evidence_directory / "index.raw"))
    evidence = json.loads(workspace_read_bytes(record))
    index = workspace_read_bytes(raw)
    digest = hashlib.sha256(index).hexdigest()
    if not isinstance(evidence, dict) or any(evidence.get(key) != value for key, value in {
        "image": image,
        "result": "pass",
        "media_type": OCI_INDEX_MEDIA_TYPE,
        "index_digest": f"sha256:{digest}",
        "raw_index_sha256": digest,
        "index_size": len(index),
    }.items()):
        raise ValueError(f"Validated OCI index evidence does not bind the released image {image}.")
    return f"{digest} *{image.rsplit(':', 1)[0]}"


def expected_certificate(env):
    """Derive the Platform intake identity from the GitHub run and exact Builds pin."""
    repository = env.get("GITHUB_REPOSITORY", "")
    ref = env.get("GITHUB_REF", "")
    commit = env.get("GITHUB_SHA", "")
    caller = env.get("GITHUB_WORKFLOW_REF", "")
    builds_sha = env.get("BUILDS_SHA", "")
    signer = f"{BUILDS_WORKFLOW}@{builds_sha}"
    if env.get("GITHUB_SERVER_URL") != "https://github.com" or env.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise ValueError("OCI provenance requires a GitHub-hosted runner on GitHub.com.")
    if ref != "refs/heads/main" or env.get("GITHUB_REF_PROTECTED") != "true":
        raise ValueError("OCI provenance requires the protected refs/heads/main source ref.")
    if not re.fullmatch(COMMIT, commit) or commit != env.get("RELEASE_SOURCE_SHA"):
        raise ValueError("The publication source commit must match the GitHub run source commit.")
    if not re.fullmatch(rf"{re.escape(repository)}/\.github/workflows/[^/@]+\.ya?ml@{re.escape(ref)}", caller):
        raise ValueError("The caller workflow must belong to the release source repository and ref.")
    if not re.fullmatch(COMMIT, builds_sha) or signer != env.get("BUILDS_WORKFLOW_REF"):
        raise ValueError("OCI provenance requires a full-SHA pin of the Builds domain-release workflow.")
    return {
        "issuer": ISSUER,
        "runnerEnvironment": "github-hosted",
        "sourceRepositoryURI": f"https://github.com/{repository}",
        "sourceRepositoryDigest": commit,
        "buildConfigURI": f"https://github.com/{caller}",
        "sourceRepositoryRef": ref,
        "buildSignerURI": f"https://github.com/{signer}",
        "buildSignerDigest": builds_sha,
    }


def release_subjects(checksums, projects, registry):
    """Require one tag-free, exact SHA-256 subject for every declared image."""
    if REGISTRY_PATTERN.fullmatch(registry) is None:
        raise ValueError("Invalid OCI release registry.")
    mappings = [line.strip().split("|", 2) for line in projects.splitlines() if line.strip()]
    if any(len(mapping) < 2 or any(part.strip() for part in mapping[2:]) for mapping in mappings):
        raise ValueError("Invalid OCI release image mappings.")
    names = {f"{registry}/{mapping[1].strip()}" for mapping in mappings}
    if not names or len(mappings) != len(names):
        raise ValueError("Invalid OCI release image mappings.")
    subjects = {}
    for row in checksums.splitlines():
        match = re.fullmatch(rf"({SHA256}) \*({re.escape(registry)}/{REPOSITORY_NAME})", row)
        if match is None or match[2] in subjects:
            raise ValueError("Malformed or duplicate OCI provenance subject.")
        subjects[match[2]] = match[1]
    if subjects.keys() != names:
        raise ValueError("OCI provenance subjects do not match the declared image set.")
    return subjects


def verify_subject(name, digest, repository, certificate, bundle, index, run=subprocess.run):
    """Check the certificate and the named digest in the same verified statement."""
    reference = f"{name}@sha256:{digest}"
    command = [
        "gh", "attestation", "verify", str(index), "--hostname", "github.com",
        "--repo", repository, "--bundle", str(bundle), "--predicate-type", PROVENANCE,
        "--cert-oidc-issuer", ISSUER, "--cert-identity-regex", r"^https://github[.]com/",
        "--format", "json",
    ]
    result = run(command, capture_output=True, timeout=60)  # nosec B603 -- no shell.
    if result.returncode:
        raise ValueError(f"GitHub attestation verification failed for {reference} (exit {result.returncode}).")
    try:
        entries = json.loads(result.stdout)
        if not isinstance(entries, list) or not entries:
            raise TypeError
        for entry in entries:
            verified = entry["verificationResult"]
            actual = verified["signature"]["certificate"]
            statement = verified["statement"]
            if all(actual.get(field) == value for field, value in certificate.items()) and any(
                subject["name"] == name and subject["digest"].get("sha256") == digest
                for subject in statement["subject"]
            ):
                return
    except (AttributeError, KeyError, TypeError, ValueError) as error:
        raise ValueError(f"Malformed GitHub attestation verification result for {reference}.") from error
    raise ValueError(f"No verified attestation binds {reference} to the expected release certificate.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    subject = modes.add_parser("subject")
    subject.add_argument("evidence_directory", type=Path)
    subject.add_argument("image")
    modes.add_parser("check-source")
    prepare = modes.add_parser("prepare")
    verify = modes.add_parser("verify")
    for mode in (prepare, verify):
        mode.add_argument("checksums", type=Path)
        mode.add_argument("evidence", type=Path)
        mode.add_argument("version")
    verify.add_argument("bundle", type=Path)
    verify.add_argument("attestation_id")
    args = parser.parse_args()
    try:
        if args.mode == "subject":
            print(subject_row(args.evidence_directory, args.image))
            return 0
        certificate = expected_certificate(os.environ)
        if args.mode == "check-source":
            return 0
        checksums = os.environ["OCI_SUBJECTS"] if args.mode == "prepare" else args.checksums.read_text(encoding="utf-8")
        subjects = release_subjects(checksums, os.environ["CONTAINER_PROJECTS"], os.environ["REGISTRY"])
        if not TAG_PATTERN.fullmatch(args.version):
            raise ValueError("Invalid OCI provenance release version.")
        if args.mode == "verify" and (
            not re.fullmatch(r"[1-9][0-9]*", args.attestation_id)
            or not args.bundle.is_file()
            or not args.bundle.stat().st_size
        ):
            raise ValueError("GitHub did not return an uploaded OCI attestation and a nonempty bundle.")
        for name, digest in subjects.items():
            directory = args.evidence / args.version / name.split("/", 1)[1]
            if subject_row(directory, f"{name}:{args.version}") != f"{digest} *{name}":
                raise ValueError(f"Publication evidence does not match the attested OCI subject {name}.")
            if args.mode == "verify":
                verify_subject(name, digest, os.environ["GITHUB_REPOSITORY"], certificate, args.bundle, directory / "index.raw")
                print(f"Verified OCI build provenance for {name}@sha256:{digest}.")
        if args.mode == "prepare":
            args.checksums.write_text("".join(f"{digest} *{name}\n" for name, digest in subjects.items()), encoding="utf-8")
    except (argparse.ArgumentTypeError, KeyError, OSError, ValueError, subprocess.TimeoutExpired) as error:
        print(f"OCI build provenance failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
