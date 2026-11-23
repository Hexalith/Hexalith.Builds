#!/usr/bin/env python3
"""Validate Story 4.14 capture and Story 4.15 OQ8 platform closure evidence."""

from __future__ import annotations

import argparse
import hashlib
import importlib
import importlib.metadata
import json
import math
import os
import re
import selectors
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

try:
    yaml = importlib.import_module("yaml")
except Exception:
    yaml = None


DEFAULT_ROOT = Path(__file__).resolve().parents[1]
ROOT = DEFAULT_ROOT
GIT_ROOT = DEFAULT_ROOT
GIT_TIMEOUT_SECONDS = 30.0
GIT_OUTPUT_LIMIT_BYTES = 131072
GIT_BLOB_LIMIT_BYTES = 8 * 1024 * 1024
PACKET = ROOT / "_bmad-output/implementation-artifacts/4-8-eventstore-oq8-platform-evidence.yaml"
EVIDENCE = (
    ROOT
    / "_bmad-output/implementation-artifacts/evidence/story-4-14"
    / "e60a3777c581d70b62f67173ccc2372b5b64a425"
)
CLOSURE = (
    ROOT
    / "_bmad-output/implementation-artifacts/evidence/story-4-15"
    / "5e8f175b2ced4715f7c6f765386812cc1001dbb4"
)
SUCCESSOR_SELECTOR = (
    ROOT
    / "_bmad-output/implementation-artifacts/4-15-oq8-platform-closure-successor.json"
)
SUCCESSOR = (
    ROOT
    / "_bmad-output/implementation-artifacts/evidence/story-4-15/successors"
    / "sdk-10.0.400-xunit4-mtp"
)
V2_SUCCESSOR = (
    ROOT
    / "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v2"
)
V3_SUCCESSOR = (
    ROOT
    / "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v3"
)
V4_SUCCESSOR = (
    ROOT
    / "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v4"
)
LIFECYCLE_STATE = (
    ROOT
    / "_bmad-output/implementation-artifacts/4-15-oq8-platform-lifecycle-state.json"
)
DESIGN_VERSION = "1.0.0"
DESIGN_SHA256 = "1a55b0302e91233e12db91e6e245f0a22d6bf13fcf6cdf5ee0cbe5759f08dcd8"
BASELINE = "e60a3777c581d70b62f67173ccc2372b5b64a425"
LANDED_SOURCE = "5e8f175b2ced4715f7c6f765386812cc1001dbb4"
LANDED_TREE = "96fdfbba56df41b58889bf7f3b532a64d15314bd"
COMPLETED_V1_CLOSURE_COMMIT = "17e47a390fdfecafba84dce14779ad13b97be339"
COMPLETED_V2_CLOSURE_COMMIT = "83b32fcfad7bb608098aebccdc15002636ffb431"
COMPLETED_V3_CLOSURE_COMMIT = "d578e7626df1d8afeada440c7989eb04ba2bdfe6"
LEGACY_SUCCESSOR_SNAPSHOT_COMMIT = "10051a68eb1db322a4f7fa91934d880ce1409687"
V1_REVIEW_SUBJECT_SHA256 = "26a0afd67c14befc3d7b5045c13c1532b27663e3409026d6f5d5e8fc5b3b5e6f"
PRIOR_VALIDATOR_SHA256 = "9652019053810366ec3a7682490a5b85385880b63bb3bf2ca7023b0a49c18dde"
SUCCESSOR_REVIEW_BASE = "cf320fd907430156d1d82e54f0aa404bdef73704"
PRIOR_PACKET_SHA256 = "ab6931160b1b9574f6f0e8c5698a0982e45978071cc311951d9604d27a5650a4"
PRIOR_CLOSURE_MANIFEST_SHA256 = "da3994dfd687b4ecf7a150b0b8b2d9fa41e54d1ea8d57641163dc6532bfb9e47"
PROFILE = "oq8-postgresql-v1"
POSTGRES_IMAGE = "postgres@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636"
POSTGRES_TAG = "postgres:18.4"
POSTGRES_AMD64_CHILD = "postgres@sha256:4cc13dede823cab4e05290c7fb3350fb4e599ecabd9b07e6706b5d5e8f5bc929"
COMMITTED_DAPR_RUNTIME_VERSION = "1.18.1"
CURRENT_REVIEW_DATE = "2026-08-27"
PINNED_PYYAML_VERSION = "6.0.3"
PINNED_PYYAML_REQUIREMENT = f"PyYAML=={PINNED_PYYAML_VERSION}"
PINNED_PYYAML_HASHES = (
    "0150219816b6a1fa26fb4699fb7daa9caf09eb1999f3b70fb6e786805e80375a",
    "0f29edc409a6392443abf94b9cf89ce99889a1dd5376d94316ae5145dfedd5d6",
    "22ba7cfcad58ef3ecddc7ed1db3409af68d023b7f940da23c6c2a1890976eda6",
    "9c7708761fccb9397fe64bbc0395abcae8c4bf7b0eac081e12b809bf47700d0b",
    "b8bb0864c5a28024fac8a632c443c87c5aa6f215c0b126c449ae1a150412f31d",
    "ba1cc08a7ccde2d2ec775841541641e4548226580ab850948cbfda66a1befcdc",
    "c458b6d084f9b935061bc36216e8a69a7e293a2f1e68bf956dcd9e6cbcd143f5",
)
MAX_SPRINT_STATUS_BYTES = 1_048_576
MAX_CANDIDATE_JSON_BYTES = 1_048_576
MAX_HISTORICAL_MANIFEST_BYTES = 65_536
MAX_V2_ARTIFACT_BYTES = 65_536
MAX_V5_CANDIDATE_BYTES = 65_536
MAX_V2_BOUND_SOURCE_BYTES = 524_288
MAX_RAW_CTRF_BYTES = 8 * 1024 * 1024
EVIDENCE_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-14/e60a3777c581d70b62f67173ccc2372b5b64a425"
CLOSURE_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-15/5e8f175b2ced4715f7c6f765386812cc1001dbb4"
SUCCESSOR_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-15/successors/sdk-10.0.400-xunit4-mtp"
SUCCESSOR_SELECTOR_PATH = "_bmad-output/implementation-artifacts/4-15-oq8-platform-closure-successor.json"
V2_SUCCESSOR_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v2"
V3_SUCCESSOR_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v3"
V4_SUCCESSOR_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v4"
V5_SUCCESSOR_DIRECTORY = "_bmad-output/implementation-artifacts/evidence/story-4-15-successors/v5"
V5_SELECTION_REASON = "Retain immutable v1-v4 history and activate the reviewed v5 source-only successor for EventStore-owned trusted publishing."
LIFECYCLE_STATE_PATH = "_bmad-output/implementation-artifacts/4-15-oq8-platform-lifecycle-state.json"
SDK_SUCCESSOR_MANIFEST_SHA256 = "fd8cc0b86d2cf4f624495c64168e6a2dc727b5aa0f830dca39c382dd28ba094d"
FOCUSED_METHOD = "Hexalith.EventStore.Server.LiveSidecar.Tests.Actors.IdempotencyAdmissionOq8PostgresqlTests.ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants"
FOCUSED_TRAITS = {
    "Category": ["LiveSidecar"],
    "Profile": [PROFILE],
}
FOCUSED_LABELS = {
    "Category": "LiveSidecar",
    "Profile": PROFILE,
}
FOCUSED_LEGACY_COMMAND = f"dotnet tests/Hexalith.EventStore.Server.LiveSidecar.Tests/bin/Release/net10.0/Hexalith.EventStore.Server.LiveSidecar.Tests.dll -method {FOCUSED_METHOD} -noColor -ctrf raw-runner-temp"
FOCUSED_CURRENT_COMMAND = f"dotnet tests/Hexalith.EventStore.Server.LiveSidecar.Tests/bin/Release/net10.0/Hexalith.EventStore.Server.LiveSidecar.Tests.dll -method {FOCUSED_METHOD} -noColor -result-ctrf raw-runner-temp"
SUPPORT_LEGACY_COMMAND = "dotnet tests/Hexalith.EventStore.Server.Tests/bin/Release/net10.0/Hexalith.EventStore.Server.Tests.dll -method (21 validator-pinned selectors) -noColor -ctrf raw-runner-temp"
SUPPORT_CURRENT_COMMAND = "dotnet tests/Hexalith.EventStore.Server.Tests/bin/Release/net10.0/Hexalith.EventStore.Server.Tests.dll -method (21 validator-pinned selectors) -noColor -result-ctrf raw-runner-temp"
EXPECTED_SOURCE_INPUTS = {
    "deploy/dapr/resiliency.yaml",
    "deploy/dapr/statestore-postgresql.yaml",
    "samples/Hexalith.EventStore.Sample/Program.cs",
    "src/Hexalith.EventStore/Program.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/DaprTestContainerFixture.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlCollection.cs",
    "tests/Hexalith.EventStore.Server.Tests/Actors/IdempotencyAdmissionActorTests.cs",
    "tests/Hexalith.EventStore.Server.Tests/Actors/IdempotencyAdmissionDirectoryActorTests.cs",
    "tests/Hexalith.EventStore.Server.Tests/Actors/IdempotencyAdmissionExpiryTests.cs",
    "tests/Hexalith.EventStore.Server.Tests/Actors/IdempotencyTenantLifecycleActorTests.cs",
    "tests/Hexalith.EventStore.Server.Tests/Actors/PublicationRecoveryActivationTests.cs",
    "tests/Hexalith.EventStore.Server.Tests/Pipeline/SubmitCommandHandlerIdempotencyAdmissionTests.cs",
}
EXPECTED_CANDIDATE_FILES = {
    ".github/workflows/integration.yml",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Actors/IdempotencyAdmissionOq8PostgresqlTests.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/AssemblyInfo.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8AdmissionSnapshot.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8BoundaryCounterStartupFilter.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8BoundedLog.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8CommandObservation.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8FileTimeProvider.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8HostingStartup.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlFixture.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlSnapshot.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8ProcessNode.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8RotatedAuthoritySnapshot.cs",
    "tools/validate-oq8-platform-evidence.py",
}
EXPECTED_CAPTURE_PATHS = EXPECTED_CANDIDATE_FILES | EXPECTED_SOURCE_INPUTS
EXPECTED_EVOLVED_PATHS = {
    ".github/workflows/integration.yml",
    "tools/validate-oq8-platform-evidence.py",
}
EXPECTED_CURRENT_BOUND_PATHS = EXPECTED_CAPTURE_PATHS - EXPECTED_EVOLVED_PATHS
REPLACED_PRIOR_BOUND_PATHS = {
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/AssemblyInfo.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlFixture.cs",
}
SUCCESSOR_SOURCE_PATHS = {
    ".github/workflows/ci.yml",
    ".github/workflows/integration.yml",
    "docs/ci.md",
    "global.json",
    "tests/Directory.Build.props",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/Oq8PlatformClosureTests.cs",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/ReleasePackageManifestTests.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/AssemblyInfo.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/DockerPublishedPortResolver.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/DockerPublishedPortResolverTests.cs",
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlFixture.cs",
    "tools/validate-oq8-platform-evidence.py",
}
REQUIRED_FILES = {
    "commands.json",
    "deterministic-support.json",
    "environment.json",
    "observations.json",
    "review-records.json",
    "source-state.json",
    "test-results.json",
}
CLOSURE_FILES = {
    "capture-packet-v1.json",
    "closure-crosswalk.json",
    "limitations.json",
    "review-subject.json",
    "reviews/architecture.json",
    "reviews/security.json",
    "reviews/test.json",
    "source-artifact-identity.json",
    "source-only-handoff.json",
    "pre-review-execution.json",
    "validator-sha256.txt",
}
SUCCESSOR_FILES = {
    "review-subject.json",
    "reviews/architecture.json",
    "reviews/security.json",
    "reviews/test.json",
    "source-artifact-identity.json",
    "source-only-handoff.json",
}
V2_SUCCESSOR_FILES = {
    "limitations.json",
    "pre-review-execution.json",
    "review-subject.json",
    "reviews/architecture.json",
    "reviews/security.json",
    "reviews/test.json",
    "source-artifact-identity.json",
    "source-only-handoff.json",
    "validator-sha256.txt",
}
V2_SOURCE_PATHS = {
    ".github/workflows/integration.yml": {
        "predecessorSha256": "343163fd164bb49252ad2ec67c7fbc90aa2f3aaecafa4d4d51640ccc39e7b777",
        "predecessorImage": POSTGRES_TAG,
    },
    "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlFixture.cs": {
        "predecessorSha256": "7f29993a470d179288a367c8d877e01b7f0f7be4206faf329f5d889b6171cae6",
        "predecessorImage": POSTGRES_TAG,
    },
}
V2_GATE_INPUT_PATHS = {
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/PostgreSqlImageGovernanceTests.cs",
    "tools/validate-oq8-platform-evidence.py",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/Oq8PlatformClosureTests.cs",
    "docs/ci.md",
}
V2_REVIEW_SCOPES = {
    "architecture": "phase-separated v2 source identity, immutable v1 lineage, gate-input closure, and source-only authority boundary",
    "security": "reviewed multi-platform index identity, fail-closed drift detection, receipt binding, and every external-authority exclusion",
    "test": "workflow and fixture agreement, negative image mutations, v1 historical validation, and complete v2 closure mutations",
}
V2_LIMITATIONS = [
    "Story 4.15 v1 remains immutable historical evidence and does not authorize workflow, fixture, validator, test, or documentation bytes changed after completed-v1 closure snapshot 17e47a390fdfecafba84dce14779ad13b97be339.",
    "The reviewed PostgreSQL identity is the multi-platform index postgres@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636; the amd64 child manifest and historical Docker image/config identity are not registry pins.",
    "The successor validates repository source identity only and does not grant runtime deployment or consumer acceptance of any external system.",
    "The v2 successor grants no release approval, package authority, registry authority, deployment authority, runtime-pin authority, consumer-migration authority, external-repository authority, Folders final closure, or final-consumer authority.",
]
V2_GOVERNANCE_COMMAND = "dotnet tests/Hexalith.EventStore.Contracts.Tests/bin/Release/net10.0/Hexalith.EventStore.Contracts.Tests.dll -class Hexalith.EventStore.Contracts.Tests.Packaging.PostgreSqlImageGovernanceTests -noColor"
V2_CLOSURE_COMMAND = "dotnet tests/Hexalith.EventStore.Contracts.Tests/bin/Release/net10.0/Hexalith.EventStore.Contracts.Tests.dll -class Hexalith.EventStore.Contracts.Tests.Packaging.Oq8PlatformClosureTests -noColor"
V2_LIVE_SIDECAR_COMMAND = "dotnet test tests/Hexalith.EventStore.Server.LiveSidecar.Tests/ --configuration Release -p:UseHexalithProjectReferences=false"
V2_PRE_REVIEW_COMMANDS = [
    ("validator-syntax", "python3 -m py_compile tools/validate-oq8-platform-evidence.py", 0),
    ("contracts-release-build", "dotnet build tests/Hexalith.EventStore.Contracts.Tests/Hexalith.EventStore.Contracts.Tests.csproj --configuration Release -p:UseHexalithProjectReferences=false -m:1", 0),
    ("postgres-image-governance", V2_GOVERNANCE_COMMAND, 18),
    ("workflow-actionlint", "actionlint .github/workflows/integration.yml", 0),
    ("historical-v1-validation", "python3 tools/validate-oq8-platform-evidence.py --historical-v1-only", 0),
    ("live-sidecar", V2_LIVE_SIDECAR_COMMAND, 115),
]
V2_FINAL_CLOSURE_TEST_COUNT = 368
V2_TEST_RECEIPT_VERIFICATION = [
    ("postgres-image-governance", V2_GOVERNANCE_COMMAND, 18),
    ("oq8-platform-closure", V2_CLOSURE_COMMAND, V2_FINAL_CLOSURE_TEST_COUNT),
    ("live-sidecar", V2_LIVE_SIDECAR_COMMAND, 115),
]
V2_REVIEW_SUBJECT_SHA256 = "f67dabaee86a7383f845225fecf33487597b432a40eb01b16ec033b7b40ed107"
V2_CLOSURE_MANIFEST_SHA256 = "ca8e8fe4ed26508aa6f7c0e742924a475dfe383b46f0e52f216000b330cea5a1"
V3_SUCCESSOR_FILES = {
    "limitations.json",
    "pre-review-execution.json",
    "review-subject.json",
    "reviews/architecture.json",
    "reviews/security.json",
    "reviews/test.json",
    "source-artifact-identity.json",
    "source-only-handoff.json",
    "validator-sha256.txt",
}
V3_SOURCE_PATHS = {
    "tools/validate-oq8-platform-evidence.py",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/Oq8PlatformClosureTests.cs",
    "docs/ci.md",
}
V3_GATE_INPUT_PATHS = SUCCESSOR_SOURCE_PATHS | set(V2_SOURCE_PATHS) | V2_GATE_INPUT_PATHS | {".gitattributes"}
V3_SELECTION_DATE = "2026-09-09"
V3_SELECTION_REASON = (
    "Unify the SDK, PostgreSQL image-governance, and current-source successors under one "
    "content-bound v3 lineage while retaining every predecessor as historical evidence."
)
V3_BINDING_RULE = (
    "V1, the SDK successor, and v2 remain immutable historical evidence; v3 binds the original "
    "landed commit and tree, requires current HEAD ancestry from that commit, and resolves every "
    "SDK and current gate input only from regular non-symlink candidate files."
)
V3_REVIEW_SCOPES = {
    "architecture": "v1, SDK, and v2 historical preservation, unified v3 current-source succession, landed Git identity, line-ending gate authority, complete limitations, bounded dependency snapshots, historical-child cleanup, and source-only authority boundaries",
    "security": "immutable predecessor lineage, landed commit and tree identity, HEAD ancestry, fail-closed source drift, deep and scheme-prefixed and slash-UNC redaction, prior-approval withdrawal, bounded non-symlink bootstrap inputs, child-process cleanup, receipt binding, and external-authority exclusions",
    "test": "historical v1/v2 validation, active v3 selector and bootstrap coverage, unsafe bootstrap rejection, focused xUnit 4 CTRF conversion, historical-child cleanup, UNC redaction mutation controls, limitation coverage, workflow static validation, the focused OQ8 LiveSidecar lane, rejected or incomplete receipt mutations, and the direct-assembly full Contracts lane",
}
V3_LIMITATIONS = [
    "Story 4.15 v1, the SDK 10.0.400 successor, and v2 remain immutable historical evidence and do not authorize source bytes changed after completed-v2 closure commit 83b32fcfad7bb608098aebccdc15002636ffb431.",
    "The active v3 successor validates exact current repository source bytes while preserving the reviewed PostgreSQL multi-platform index authority recorded by v2.",
    "The v3 landed-commit, landed-tree, and HEAD-ancestry proofs resolve through the local Git object store, so a shallow or history-rewritten clone that lacks commit 5e8f175b2ced4715f7c6f765386812cc1001dbb4 fails closed rather than validating.",
    "The immutable Story 4.14 capture remains historical evidence of Dapr runtime 1.18.1; the current content-bound integration workflow and fresh OQ8 capture lane require Dapr runtime 1.18.2, without granting runtime-pin authority.",
    "Exact UTC-second timestamps are parsed generically and must not be later than the validator's captured current UTC; chronology remains strictly execution, subject freeze, receipts, then handoff.",
    "Exact-tree enumeration before sealed OQ8 directory comparison is not depth- or entry-count-bounded, so a hostile evidence tree can consume unbounded time or memory before fail-closed rejection; remediation remains deferred as DW-520.",
    "Private-path redaction is intentionally incomplete: non-users/home UNC roots, generic drive and home shorthand, 8.3 paths, terminal filenames containing spaces, repr-doubled Windows paths, and EvidenceError messages can remain visible; unexpected-exception redaction runs before the 256-character output truncation. Because the approved receipt schema can encode only decision approved, this limitation withdraws the prior false security approval for nested-UNC redaction; the new receipts bind only this successor subject.",
    "The v3 successor grants no release approval, package authority, registry authority, deployment authority, runtime-pin authority, consumer-migration authority, external-repository authority, Folders final closure, or final-consumer authority.",
]
V3_CONTRACTS_RESTORE_COMMAND = "dotnet restore tests/Hexalith.EventStore.Contracts.Tests/Hexalith.EventStore.Contracts.Tests.csproj -m:1 -p:Configuration=Release -p:UseHexalithProjectReferences=false"
V3_CONTRACTS_BUILD_COMMAND = "dotnet build tests/Hexalith.EventStore.Contracts.Tests/Hexalith.EventStore.Contracts.Tests.csproj --no-restore --configuration Release -warnaserror -m:1 -p:UseHexalithProjectReferences=false"
V3_CONTRACTS_TEST_COMMAND = "dotnet tests/Hexalith.EventStore.Contracts.Tests/bin/Release/net10.0/Hexalith.EventStore.Contracts.Tests.dll -noColor"
V3_ACTIONLINT_COMMAND = "actionlint .github/workflows/ci.yml .github/workflows/integration.yml"
V3_LIVE_SIDECAR_BUILD_COMMAND = "dotnet build tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Hexalith.EventStore.Server.LiveSidecar.Tests.csproj --configuration Release -warnaserror -m:1 -p:UseHexalithProjectReferences=false"
V3_LIVE_SIDECAR_TEST_COMMAND = "dotnet tests/Hexalith.EventStore.Server.LiveSidecar.Tests/bin/Release/net10.0/Hexalith.EventStore.Server.LiveSidecar.Tests.dll -method Hexalith.EventStore.Server.LiveSidecar.Tests.Actors.IdempotencyAdmissionOq8PostgresqlTests.ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants -noColor"
V3_PRE_REVIEW_COMMANDS = [
    ("validator-syntax", "python3 -m py_compile tools/validate-oq8-platform-evidence.py", 0),
    ("historical-v1-validation", "python3 tools/validate-oq8-platform-evidence.py --historical-v1-only", 0),
    ("historical-v2-validation", "python3 tools/validate-oq8-platform-evidence.py --historical-v2-only", 0),
    ("workflow-static-validation", V3_ACTIONLINT_COMMAND, 0),
    ("live-sidecar-build", V3_LIVE_SIDECAR_BUILD_COMMAND, 0),
    ("live-sidecar-focused", V3_LIVE_SIDECAR_TEST_COMMAND, 1),
    ("contracts-restore", V3_CONTRACTS_RESTORE_COMMAND, 0),
    ("contracts-build", V3_CONTRACTS_BUILD_COMMAND, 0),
]
V3_REVIEW_DATE = "2026-09-20"
V3_FINAL_CLOSURE_TEST_COUNT = 467
V3_FULL_CONTRACTS_TEST_COUNT = 2070
V3_CONSUMER_HISTORICAL_RULE = (
    "Validate Story 4.15 v1, the SDK 10.0.400 successor, and v2 only against their immutable "
    "historical artifacts and Git snapshots. A full Git object store (fetch-depth: 0) is required; "
    f"a shallow clone that lacks commit {LANDED_SOURCE} fails closed."
)
V3_CONSUMER_CURRENT_RULE = (
    "Treat current source as closed only when this complete v3 successor validates against the "
    "current candidate bytes. A full Git object store (fetch-depth: 0) is required; "
    f"a shallow clone that lacks commit {LANDED_SOURCE} fails closed."
)
V3_CONSUMER_INSTALL_COMMAND = (
    "python3 -m venv .oq8-python && .oq8-python/bin/python -m pip install "
    "--require-hashes --no-deps --only-binary=:all: --requirement requirements-oq8.txt"
)
V3_TEST_RECEIPT_VERIFICATION = [
    ("workflow-static-validation", V3_ACTIONLINT_COMMAND, 0),
    ("live-sidecar-focused", V3_LIVE_SIDECAR_TEST_COMMAND, 1),
    ("oq8-platform-closure", V2_CLOSURE_COMMAND, V3_FINAL_CLOSURE_TEST_COUNT),
    ("contracts-full", V3_CONTRACTS_TEST_COMMAND, V3_FULL_CONTRACTS_TEST_COUNT),
]
V3_REVIEW_SUBJECT_SHA256 = "583269ac56f7ca75371b6c70a507a820bf21a4ff1fada790ac91feea34ca8b7e"
V3_CLOSURE_MANIFEST_SHA256 = "f20a01ba874eaca8e3d99eef065534a8211eef16db601bf9dc1723ef0cc90493"
V3_SOURCE_IDENTITY_SHA256 = "43192e12f240e37ecb88573f59ee0bf836e35e71637918ef85842df061e23a37"
V3_HANDOFF_SHA256 = "14e618e8c53d8f04983b6f7d0f33edfea6b7e243ca457c5370419e01e3bd849a"
V4_SUCCESSOR_FILES = {
    "limitations.json",
    "pre-review-execution.json",
    "review-subject.json",
    "reviews/architecture.json",
    "reviews/security.json",
    "reviews/test.json",
    "source-artifact-identity.json",
    "source-only-handoff.json",
    "validator-sha256.txt",
}
V4_SOURCE_PATHS = {
    "tools/validate-oq8-platform-evidence.py",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/Oq8PlatformClosureTests.cs",
    "_bmad-output/implementation-artifacts/spec-4-15-oq8-platform-closure-and-handoff.md",
}
V4_GATE_INPUT_PATHS = V3_GATE_INPUT_PATHS | {
    "_bmad-output/implementation-artifacts/spec-4-15-oq8-platform-closure-and-handoff.md",
}
V4_SELECTION_DATE = "2026-09-20"
V4_SELECTION_REASON = (
    "Retain the completed v3 successor as immutable historical evidence and activate one freshly "
    "reviewed v4 successor whose lifecycle state remains outside the sealed review subject."
)
V4_BINDING_RULE = (
    "V1, the SDK successor, v2, and v3 remain immutable historical evidence through completed-v3 "
    f"commit {COMPLETED_V3_CLOSURE_COMMIT}; v4 binds the changed validator, Contracts tests, "
    "approved parent contract, and every current gate input from regular non-symlink candidate files."
)
V4_REVIEW_SCOPES = {
    "architecture": "immutable v1 through v3 lineage, active v4 phase separation, lifecycle-record isolation, approved parent-contract evolution, and source-only authority boundaries",
    "security": "immutable predecessor lineage, bounded lifecycle record, fail-closed evidence-before-lifecycle ordering, receipt binding, protected-content gates, and external-authority exclusions",
    "test": "historical v1/v2/v3 lineage, active v4 chronology, handoff, PostgreSQL semantic, selector, lifecycle-identity, and anti-gaming mutations, plus the focused corrective Contracts lane with wider full verification left to the parent run",
}
V4_LIMITATIONS = [
    f"Story 4.15 v1, the SDK 10.0.400 successor, v2, and v3 remain immutable historical evidence through completed-v3 commit {COMPLETED_V3_CLOSURE_COMMIT} and do not authorize current v4 source bytes.",
    "The active v4 successor validates exact current repository gate inputs and content-binds the validator, Contracts tests, and approved Story 4.15 parent-contract evolution.",
    "The mutable lifecycle record and sprint tracking remain outside the sealed v4 review subject; they can select only ready-to-close or closed tracking after the complete v4 packet validates.",
    "Isolated final and closed lifecycle modes validate only the bounded lifecycle record and exact repository tracking pair and never approve evidence.",
    f"Historical and current Git identity proofs require a full object store containing completed-v3 commit {COMPLETED_V3_CLOSURE_COMMIT}; shallow or rewritten history fails closed.",
    "Exact-tree enumeration before sealed OQ8 directory comparison is not depth- or entry-count-bounded, so a hostile evidence tree can consume unbounded time or memory before fail-closed rejection; remediation remains deferred as DW-520.",
    "Private-path redaction remains intentionally incomplete as disclosed by the completed v3 predecessor; v4 grants no broader diagnostic-safety claim.",
    "The v4 successor grants no release approval, package authority, registry authority, deployment authority, runtime-pin authority, consumer-migration authority, external-repository authority, Folders final closure, or final-consumer authority.",
]
V4_CONTRACTS_RESTORE_COMMAND = V3_CONTRACTS_RESTORE_COMMAND
V4_CONTRACTS_BUILD_COMMAND = V3_CONTRACTS_BUILD_COMMAND
V4_CONTRACTS_TEST_COMMAND = V3_CONTRACTS_TEST_COMMAND
V4_ACTIONLINT_COMMAND = V3_ACTIONLINT_COMMAND
V4_LIVE_SIDECAR_BUILD_COMMAND = V3_LIVE_SIDECAR_BUILD_COMMAND
V4_LIVE_SIDECAR_TEST_COMMAND = V3_LIVE_SIDECAR_TEST_COMMAND
V4_PRE_REVIEW_COMMANDS = [
    ("validator-syntax", "python3 -m py_compile tools/validate-oq8-platform-evidence.py", 0),
    ("contracts-build", V4_CONTRACTS_BUILD_COMMAND, 0),
]
V4_REVIEW_DATE = "2026-09-23"
V4_FINAL_CLOSURE_TEST_COUNT = 476
V4_CONSUMER_HISTORICAL_RULE = (
    "Validate Story 4.15 v1, the SDK 10.0.400 successor, v2, and v3 only against immutable "
    f"historical artifacts and Git snapshots through commit {COMPLETED_V3_CLOSURE_COMMIT}."
)
V4_CONSUMER_CURRENT_RULE = (
    "Treat current source as evidence-approved only when the complete v4 successor validates; "
    "then require the separate lifecycle record and exact sprint/spec pair for ready-to-close or closed state."
)
V4_CONSUMER_INSTALL_COMMAND = V3_CONSUMER_INSTALL_COMMAND
V4_LIFECYCLE_MUTATION_COMMAND = (
    "dotnet tests/Hexalith.EventStore.Contracts.Tests/bin/Release/net10.0/"
    "Hexalith.EventStore.Contracts.Tests.dll -method "
    "Hexalith.EventStore.Contracts.Tests.Packaging.Oq8PlatformClosureTests."
    "LifecycleRecordMutationsFailClosed -noColor"
)
V4_LIFECYCLE_SNAPSHOT_COMMAND = (
    "dotnet tests/Hexalith.EventStore.Contracts.Tests/bin/Release/net10.0/"
    "Hexalith.EventStore.Contracts.Tests.dll -method "
    "Hexalith.EventStore.Contracts.Tests.Packaging.Oq8PlatformClosureTests."
    "LifecycleValidationUsesProvidedValidatedSelectorSnapshot -noColor"
)
V4_TEST_RECEIPT_VERIFICATION = [
    ("lifecycle-record-mutations", V4_LIFECYCLE_MUTATION_COMMAND, 10),
    ("lifecycle-selector-snapshot", V4_LIFECYCLE_SNAPSHOT_COMMAND, 1),
]
REVIEW_ROSTER = {
    "architecture": "Winston (System Architect)",
    "security": "Security Reviewer",
    "test": "Murat (Test Architect)",
}
REVIEW_SCOPES = {
    "architecture": "OQ8 design reference, invariant crosswalk, landed-source identity, architecture boundaries, and source-only handoff",
    "security": "protected-data leakage gates, current-fence evidence, sanitized-state limitation, and external authority exclusions",
    "test": "production/deterministic evidence coverage, commands and counts, negative validation, and matrix coverage",
}
SUCCESSOR_REVIEW_SCOPES = {
    "architecture": "SDK 10.0.400/MTP source rebinding, prior-seal retention, Linux control-plane discovery, and source-only authority boundaries",
    "security": "successor identity integrity, immutable prior evidence, bounded Docker port parsing, dependency probing, and external authority exclusions",
    "test": "xUnit 4 serialization metadata, maintained MTP coverage, focused resolver/production evidence, and fail-closed successor selection",
}
SUCCESSOR_REVIEW_FINDINGS = {
    "architecture": [
        "The successor is additive and selects exact current source bytes without changing or deleting the original Story 4.15 packet, manifest, or artifacts.",
        "The xUnit 4 assembly metadata and Linux-published control-plane port adaptation preserve serialized production evidence behavior under SDK 10.0.400.",
    ],
    "security": [
        "Docker port discovery accepts one consistent published TCP port and fails closed on absent, malformed, or conflicting mappings.",
        "The EventStore-owned NuGet package-cache probing path is bound here without granting package, shared-workflow, release, or external-repository authority.",
    ],
    "test": [
        "Nine focused Docker published-port resolver cases and the production OQ8 case passed with no failures or skips.",
        "The EventStore-owned MTP workflow integration and xUnit ParallelMode.None source are content-bound; external Builds catalog and reusable-workflow authority remain excluded.",
    ],
}
EXTERNAL_AUTHORITY_FIELDS = {
    "releaseApproved",
    "foldersFinalClosure",
    "packageAuthority",
    "registryAuthority",
    "deploymentAuthority",
    "runtimePinAuthority",
    "consumerMigrationAuthority",
    "externalRepositoryAuthority",
    "finalConsumerAuthority",
}
EXPECTED_DOCUMENT_MARKER = "OQ8-SOURCE-ONLY-HANDOFF"
EXPECTED_DOCUMENTS = {
    "docs/concepts/command-lifecycle.md",
    "docs/concepts/architecture-overview.md",
    "docs/reference/command-api.md",
    "docs/guides/configuration-reference.md",
}
EXPECTED_DOCUMENT_HASHES = {
    "docs/concepts/architecture-overview.md": "8eb99ee1053be809e9e0b136d183a9ad3a591f766f82247a2109e344027043db",
    "docs/concepts/command-lifecycle.md": "c82edf5422be21e096afaecef0bd254f28f924fb5557c73b6fc22d9da7334cd0",
    "docs/guides/configuration-reference.md": "e2fde4db539fc2fadfa545394cb81cabc270bd37bba4f3cce2687e25407f8f0d",
    "docs/reference/command-api.md": "6b0bfd403d371c1278ad0e09516cfd995b26e8de98f3c85c9e1a20d68d5cc821",
}
DOCUMENT_REQUIRED_TEXT = (
    "reviewed source-only handoff",
    LANDED_SOURCE,
    "approved Folders design bytes are not tracked here",
    "test-only deterministic-time, intent-adapter, and boundary-counter seams",
    "raw PostgreSQL values and diagnostics were replaced by sanitized structural projections",
    "original dirty candidate capture is independently rebound to the 26-path landed source",
    "no release approval",
    "Folders final closure",
    "package or registry authority",
    "deployment authority",
    "runtime-pin authority",
    "consumer-migration authority",
    "external-repository authority",
    "final-consumer authority",
    "fresh content-bound architecture, security, and test receipts",
    "EventStore platform completion and the source-only handoff are recorded",
    "only while",
    "python3 -m venv .oq8-python",
    ".oq8-python/bin/python -m pip install --require-hashes --no-deps --only-binary=:all: --requirement requirements-oq8.txt",
    ".oq8-python/bin/python tools/validate-oq8-platform-evidence.py",
)
DOCUMENT_FORBIDDEN_TEXT = (
    "source-only handoff candidate",
    "are required and are not yet recorded",
    "only after those receipts",
)
CLOSURE_TEST_SOURCE = "tests/Hexalith.EventStore.Contracts.Tests/Packaging/Oq8PlatformClosureTests.cs"
PRIOR_ROOT_BINDING_HASHES = {
    ".github/workflows/ci.yml": "6a28bd968ad3c865226e3a0c2bccdd75f520ac5455b887f5b7efdaa3b1c0bcce",
    ".github/workflows/integration.yml": "343163fd164bb49252ad2ec67c7fbc90aa2f3aaecafa4d4d51640ccc39e7b777",
    "requirements-oq8.txt": "0969da99a0bc2a1b71ed50584560f4588a37567ac63af3ddbaf3c4617ca5621a",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/Oq8PlatformClosureTests.cs": "4085ae558bad0eed21759a3e4f561e35710f3cd3375705f4041b0089083e83e3",
    "tests/Hexalith.EventStore.Contracts.Tests/Packaging/ReleasePackageManifestTests.cs": "f30ed72b844b845c04509eb3dddf07ea6cac38571c0998b57687f4b2e3568fe1",
    "tools/validate-oq8-platform-evidence.py": PRIOR_VALIDATOR_SHA256,
}
PRE_REVIEW_EXECUTION = "pre-review-execution.json"
EXPECTED_LIMITATIONS = [
    "The approved Folders design bytes are not tracked in EventStore; only design version 1.0.0 and its approved SHA-256 reference are preserved.",
    "The production-path capture used shipped Release entry assemblies with a test-only hosting startup, deterministic time, trusted intent adapter, boundary counter, and Testing environment disclosure.",
    "Raw PostgreSQL values and raw diagnostics were intentionally excluded; committed evidence contains sanitized structural projections, bounded counts, hashes, and invariant results.",
    "The original capture bound a dirty candidate against e60a3777c581d70b62f67173ccc2372b5b64a425; this closure independently binds the same 26 paths at landed commit 5e8f175b2ced4715f7c6f765386812cc1001dbb4.",
    "This is a source-only EventStore platform handoff. It grants no release, package, registry, deployment, runtime pin, external-repository, consumer-migration, or final-consumer authority.",
    "Folders retains its own final cross-repository decision and must independently verify the referenced design and EventStore source before consumption.",
]
PRE_REVIEW_TEST_DLL = "tests/Hexalith.EventStore.Contracts.Tests/bin/Release/net10.0/Hexalith.EventStore.Contracts.Tests.dll"
PRE_REVIEW_TEST_CLASS = "Hexalith.EventStore.Contracts.Tests.Packaging.Oq8PlatformClosureTests"
PRE_REVIEW_COMMAND_RESULTS = [
    {
        "name": "oq8-validator-dependency",
        "command": "python3 -m venv /tmp/hexalith-eventstore-oq8-python && /tmp/hexalith-eventstore-oq8-python/bin/python -m pip install --requirement requirements-oq8.txt && /tmp/hexalith-eventstore-oq8-python/bin/python -c \"import importlib.metadata, pathlib, yaml; distribution = importlib.metadata.distribution('PyYAML'); expected = pathlib.Path(distribution.locate_file('yaml/__init__.py')).resolve(); actual = pathlib.Path(yaml.__file__).resolve(); assert distribution.version == yaml.__version__ == '6.0.3' and actual == expected\"",
        "exitCode": 0,
        "result": "passed",
    },
    {
        "name": "validator-syntax",
        "command": "python3 -m py_compile tools/validate-oq8-platform-evidence.py",
        "exitCode": 0,
        "result": "passed",
    },
    {
        "name": "contracts-build",
        "command": "dotnet build tests/Hexalith.EventStore.Contracts.Tests/Hexalith.EventStore.Contracts.Tests.csproj --configuration Release -m:1",
        "exitCode": 0,
        "result": "passed",
        "warnings": 0,
        "errors": 0,
    },
    *[
        {
            "name": name,
            "command": f"dotnet {PRE_REVIEW_TEST_DLL} -method {PRE_REVIEW_TEST_CLASS}.{method} -noColor",
            "exitCode": 0,
            "result": "passed",
            "tests": tests,
            "passed": tests,
            "failed": 0,
            "skipped": 0,
        }
        for name, method, tests in (
            ("candidate-semantic-mutations", "CandidateSemanticMutationsFailClosed", 49),
            ("consumer-bootstrap", "SourceOnlyConsumerBootstrapInstructionsAreExactAndOrdered", 1),
            ("invalid-pyyaml-dependencies", "InvalidPyYamlDependenciesFailClosed", 4),
            ("hostile-duplicate-json", "HostileDuplicateJsonKeyIsBoundedAndRedacted", 1),
            ("candidate-limitations", "CandidateLimitationTextIsExact", 6),
            ("candidate-authority", "CandidateExternalAuthorityFailsClosed", 9),
            ("candidate-lifecycle", "RequiredSprintStatusMustBeUnique", 9),
            ("retired-lifecycle-yaml-shapes", "RetiredSprintStatusYamlShapesFailClosed", 19),
            ("supported-active-lifecycle-yaml", "SupportedActiveSprintStatusYamlPasses", 4),
            ("unsupported-active-lifecycle-yaml", "UnsupportedActiveSprintStatusYamlFailsClosed", 17),
            ("retired-lifecycle-scoping", "RetiredSprintStatusTextOutsideExactDirectEntryPasses", 12),
            ("unsupported-lifecycle-yaml", "UnsupportedSprintStatusYamlFailsClosed", 72),
            ("missing-active-lifecycle", "MissingRequiredSprintStatusFailsClosed", 1),
            ("duplicate-authority-formatting", "DuplicateAuthorityInjectorSupportsJsonFormatting", 4),
            ("final-lifecycle-review", "FinalLifecycleReviewPassesInIsolation", 1),
            ("final-lifecycle-drift", "FinalLifecycleRequiresSprintReview", 3),
            ("changed-deleted-source", "ChangedOrDeletedBoundCapabilityPathFailsClosed", 2),
            ("hidden-index-flags", "HiddenBoundCapabilityPathFailsClosed", 2),
            ("non-descendant-head", "NonDescendantHeadFailsClosed", 1),
            ("replacement-ref-isolation", "ReplacementRefCannotAlterLandedIdentityProof", 1),
            ("invalid-git-root", "InvalidGitRootFailsSafely", 1),
            ("git-timeout", "GitSubprocessTimeoutFailsSafely", 1),
            ("git-output-limit", "GitSubprocessOutputFloodFailsSafely", 1),
            ("redacted-paths", "CandidatePathFailureIsBoundedAndRedacted", 2),
            ("process-timeout", "ProcessHarnessEnforcesTimeoutWithoutRedirectDeadlock", 1),
            ("dual-stream-drain", "ProcessHarnessDrainsLargeStdoutAndStderr", 1),
            ("runtime-mode-identity", "FreshAndCommittedRuntimeModesRemainExact", 1),
            ("fresh-observation-semantics", "FreshObservationSchemaAndSemanticMutationsFailClosed", 9),
            ("pre-review-mode-exclusivity", "PreReviewModeRejectsCaptureAndSupportArguments", 2),
        )
    ],
]
PRE_REVIEW_FINAL_VALIDATION = {
    "command": f"dotnet {PRE_REVIEW_TEST_DLL} -class {PRE_REVIEW_TEST_CLASS} -noColor",
    "status": "not-run-pre-review",
    "reason": "Final-only receipt, handoff, manifest, and packet tests require three real content-bound review receipts and final assembly.",
}
EXPECTED_CAPTURE_COMMAND_RESULTS = {
    "live-sidecar-release-build": {
        "command": "dotnet build tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Hexalith.EventStore.Server.LiveSidecar.Tests.csproj --configuration Release -m:1",
        "counts": {"warnings": 0, "errors": 0},
    },
    "focused-production-matrix": {
        "command": FOCUSED_LEGACY_COMMAND,
        "counts": {"passed": 1, "failed": 0, "skipped": 0},
    },
    "explicit-deterministic-support-oracles": {
        "command": SUPPORT_LEGACY_COMMAND,
        "counts": {"methods": 21, "passed": 33, "failed": 0, "skipped": 0},
    },
    "deterministic-support-lane": {
        "command": "dotnet test tests/Hexalith.EventStore.Server.Tests/Hexalith.EventStore.Server.Tests.csproj --configuration Release --no-build -m:1",
        "counts": {"passed": 3103, "failed": 0, "preExistingSkipped": 25},
    },
    "fresh-capture-validator": {
        "command": "python3 tools/validate-oq8-platform-evidence.py --capture-directory runner-temp/oq8-story-4-14 --ctrf runner-temp/oq8-results.json --support-ctrf runner-temp/oq8-support-results.json",
        "counts": {"validationErrors": 0},
    },
    "committed-packet-validator": {
        "command": "python3 tools/validate-oq8-platform-evidence.py",
        "counts": {"validationErrors": 0},
    },
    "solution-release-build": {
        "command": "dotnet build Hexalith.EventStore.slnx --configuration Release -m:1",
        "counts": {"warnings": 0, "errors": 0},
    },
    "diff-whitespace-gate": {
        "command": "git diff --check",
        "counts": {"errors": 0},
    },
}
EXPECTED_CONSUMER_INSTRUCTIONS = {
    "mode": "source-only",
    "installCommand": "python3 -m venv .oq8-python && .oq8-python/bin/python -m pip install --requirement requirements-oq8.txt",
    "verifyCommand": ".oq8-python/bin/python tools/validate-oq8-platform-evidence.py",
    "designBytesRequiredFromFolders": True,
    "sourcePathRule": "Use only the exact landed EventStore commit after the closure validator passes against unchanged capability paths.",
}
EXPECTED_CROSSWALK_EVIDENCE_HASHES = {
    "_bmad-output/implementation-artifacts/4-8-durable-tenant-scoped-idempotency-admission-and-expired-key-precedence.md": "bc19803ebc7e1f1b6b0e0353560d0e6f9c72815b7e0afba35e94d60a1046d904",
    "_bmad-output/implementation-artifacts/spec-4-11-admission-state-machine-and-current-fence-enforcement.md": "a26090a034e60851b2a518fed6c41de7caf1ecff4931147ff954e4fe45310624",
    "_bmad-output/implementation-artifacts/spec-4-12-expiry-compaction-and-tombstone-retention.md": "df48f3bc6dfef608190a640193182e07c514798676abd0f50967207f219e652a",
    "_bmad-output/implementation-artifacts/spec-4-13-legacy-admission-migration-and-fail-closed-reconciliation.md": "7afed1dcb5f0fdf8e7aba12ae3a52c2a4dd13f438d05f85a500e5ee6e0441128",
    "_bmad-output/implementation-artifacts/spec-4-14-oq8-multi-host-production-evidence.md": "4e6956baac6ff79c9032d57e46ce08f6f0217c63f51523e942c5eb62d9439c8e",
    f"{EVIDENCE_DIRECTORY}/deterministic-support.json": "de2f76f2662574595c2db3b23af51bd06a7f1f9d194553e748cb1e2c83a238ae",
    f"{EVIDENCE_DIRECTORY}/environment.json": "2981c6dd2ad81fbde6ece41f2b9dcf1c9f3f26a84d6a7e128a0c50a94e8c7254",
    f"{EVIDENCE_DIRECTORY}/evidence-sha256.txt": "02c3f50778e4b6b2cc2ea422ad00c149884955a113cd6bce9f8c80b24dc1d1fc",
    f"{EVIDENCE_DIRECTORY}/observations.json": "7444f4a696e52bbb49a624f576fad8d577a630ca32b57b0fb6eb35b618e7c701",
}
EXPECTED_CROSSWALK_INVARIANTS = [
    {
        "id": "OQ8-1",
        "name": "trusted-admission",
        "stories": ["4.9"],
        "evidence": [
            "_bmad-output/implementation-artifacts/4-8-durable-tenant-scoped-idempotency-admission-and-expired-key-precedence.md",
            f"{EVIDENCE_DIRECTORY}/deterministic-support.json",
        ],
        "result": "approved",
    },
    {
        "id": "OQ8-2",
        "name": "tenant-key-identity",
        "stories": ["4.9", "4.10"],
        "evidence": [
            "_bmad-output/implementation-artifacts/4-8-durable-tenant-scoped-idempotency-admission-and-expired-key-precedence.md",
            f"{EVIDENCE_DIRECTORY}/observations.json",
        ],
        "result": "approved",
    },
    {
        "id": "OQ8-3",
        "name": "protected-key-handling",
        "stories": ["4.9", "4.14"],
        "evidence": [f"{EVIDENCE_DIRECTORY}/observations.json", f"{EVIDENCE_DIRECTORY}/environment.json"],
        "result": "approved",
    },
    {
        "id": "OQ8-4",
        "name": "atomic-state-and-current-fence",
        "stories": ["4.10", "4.11", "4.14"],
        "evidence": [
            "_bmad-output/implementation-artifacts/spec-4-11-admission-state-machine-and-current-fence-enforcement.md",
            f"{EVIDENCE_DIRECTORY}/deterministic-support.json",
            f"{EVIDENCE_DIRECTORY}/observations.json",
        ],
        "result": "approved",
    },
    {
        "id": "OQ8-5",
        "name": "replay-conflict-and-inclusive-expiry",
        "stories": ["4.11", "4.12", "4.14"],
        "evidence": [
            "_bmad-output/implementation-artifacts/spec-4-12-expiry-compaction-and-tombstone-retention.md",
            f"{EVIDENCE_DIRECTORY}/observations.json",
        ],
        "result": "approved",
    },
    {
        "id": "OQ8-6",
        "name": "retention-and-governed-tombstones",
        "stories": ["4.12", "4.14"],
        "evidence": [
            "_bmad-output/implementation-artifacts/spec-4-12-expiry-compaction-and-tombstone-retention.md",
            f"{EVIDENCE_DIRECTORY}/observations.json",
        ],
        "result": "approved",
    },
    {
        "id": "OQ8-7",
        "name": "fail-closed-recovery-and-migration",
        "stories": ["4.11", "4.13", "4.14"],
        "evidence": [
            "_bmad-output/implementation-artifacts/spec-4-13-legacy-admission-migration-and-fail-closed-reconciliation.md",
            f"{EVIDENCE_DIRECTORY}/deterministic-support.json",
        ],
        "result": "approved",
    },
    {
        "id": "OQ8-8",
        "name": "multi-host-production-evidence",
        "stories": ["4.14"],
        "evidence": [
            "_bmad-output/implementation-artifacts/spec-4-14-oq8-multi-host-production-evidence.md",
            f"{EVIDENCE_DIRECTORY}/evidence-sha256.txt",
        ],
        "result": "approved",
    },
]
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
PRIVATE_PATH_RE = re.compile(r"(?:/home/|/Users/|[A-Za-z]:[\\/]Users[\\/])")
# One source of truth for private roots used both at the token boundary and when
# separating adjacent name=<private-path> assignments.
PRIVATE_ROOT_FRAGMENT = (
    r"(?:/(?:home|users|tmp|var/tmp)/|/root(?:/|\b)|[a-z]:[\\/](?:users|temp|tmp)[\\/]"
    r"|(?:\\\\|//)[^\\/\s]+[\\/](?:[^\\/\s]+[\\/])*?(?:users|home)[\\/])"
)
PRIVATE_PATH_TOKEN_RE = re.compile(
    PRIVATE_ROOT_FRAGMENT
    + r"(?:[^\s\"<>]|[ \t](?![ \t]*[A-Za-z][A-Za-z0-9_-]*=" + PRIVATE_ROOT_FRAGMENT + r")"
    r"(?![0-9.]+[\\/][0-9.]+(?:[\s\"<>]|$))"
    r"(?=[^\s\"<>]*[\\/]))*",
    re.IGNORECASE,
)
PLACEHOLDER_RE = re.compile(r"(?:\bTBD\b|\bTODO\b|\bUNKNOWN\b|<[^>]+>)", re.IGNORECASE)
FORBIDDEN_CLAIM_RE = re.compile(
    r"(?:OQ8\s+(?:is\s+)?closed|Folders\s+OQ8\s+closure|production[- ]ready|release\s+approved)",
    re.IGNORECASE,
)
FORBIDDEN_CAPTURE_TERMS = (
    "PROTECTED-OQ8-RAW-SENTINEL",
    "Oq8EvidenceOnlySigningKey-AtLeast32Characters",
    "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
    "ZmVkY2JhOTg3NjU0MzIxMGZlZGNiYTk4NzY1NDMyMTA=",
    "POSTGRES_PASSWORD",
    "password=",
    "Bearer ",
    "eyJhbGci",
)
EXPECTED_SUPPORT_METHOD_CASES = {
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_PendingEquivalent_ReturnsFirstWriterTaskEvidenceWithoutDownstreamWork": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_Conflict_DeniesBeforeAggregateAndAdvisoryStores": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_AdmissionStoreUnavailableFailsClosedBeforeRoute": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnverifiableAdmission_ReturnsStableFailClosedOutcome": 3,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnknownOutcome_ReconcilesReadOnlyAndFinalizesExactAggregateResult": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnknownOutcomeWithoutAuthoritativeResult_RemainsFailClosed": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.Coordinator_UnsafeLegacyInventoryDoesNoLifecycleAdmissionDirectoryOrMigrationWork": 2,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.AdmitAsync_StateStoreUnavailableFailsClosedWithoutReservation": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.AdmitAsync_UnknownSchema_FailsClosedAsCorrupt": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.AdmitAsync_VerificationTagMismatchFailsClosedAsCollision": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyTenantLifecycleActorTests.MigrateLegacyAsync_RestartFromEveryDurablePhaseFinishesPinnedTarget": 6,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyTenantLifecycleActorTests.MigrateLegacyAsync_UnsafeSourceEvidenceNeverPreparesTarget": 4,
    "Hexalith.EventStore.Server.Tests.Actors.PublicationRecoveryActivationTests.OnActivate_MissingCheckpointPruned_ReleasesTheRecoverableIdempotencyRecord": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_RouteFailure_MarksUnknownOutcomeUnderSameFence": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_WriteAheadFailureAfterFenceMarksUnknownOutcome": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionExpiryTests.ValidateAuthorityAsync_PendingAcceptsOnlyExactExecuteAuthorityWithoutMutation": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionExpiryTests.ValidateAuthorityAsync_UnknownOutcomeAcceptsOnlyExactReconciliationAuthorityWithoutMutation": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_TamperedCapabilityLeavesAdmissionUnchangedBeforeBegin": 1,
    "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_ContextThatLostDurableAuthorityPerformsZeroDownstreamWork": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionDirectoryActorTests.AdvanceAsync_PromotionOrder_KeepsSourceCanonicalUntilDirectoryFlip": 1,
    "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.Coordinator_OrdinaryActivationResponseLossReprovesExactTargetBeforeAdvance": 2,
}
SUPPORT_CLASSIFICATIONS = {
    "pending": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_PendingEquivalent_ReturnsFirstWriterTaskEvidenceWithoutDownstreamWork",
    ],
    "denied": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_Conflict_DeniesBeforeAggregateAndAdvisoryStores",
    ],
    "unavailable_or_corrupt": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_AdmissionStoreUnavailableFailsClosedBeforeRoute",
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnverifiableAdmission_ReturnsStableFailClosedOutcome",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.AdmitAsync_StateStoreUnavailableFailsClosedWithoutReservation",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.AdmitAsync_UnknownSchema_FailsClosedAsCorrupt",
    ],
    "unsafe_legacy": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnverifiableAdmission_ReturnsStableFailClosedOutcome",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.Coordinator_UnsafeLegacyInventoryDoesNoLifecycleAdmissionDirectoryOrMigrationWork",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyTenantLifecycleActorTests.MigrateLegacyAsync_UnsafeSourceEvidenceNeverPreparesTarget",
    ],
    "recoverable_without_checkpoint": [
        "Hexalith.EventStore.Server.Tests.Actors.PublicationRecoveryActivationTests.OnActivate_MissingCheckpointPruned_ReleasesTheRecoverableIdempotencyRecord",
    ],
    "reconciled_unknown_read_only": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnknownOutcome_ReconcilesReadOnlyAndFinalizesExactAggregateResult",
    ],
    "unreconciled_unknown_blocked": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnknownOutcomeWithoutAuthoritativeResult_RemainsFailClosed",
    ],
    "collision": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_UnverifiableAdmission_ReturnsStableFailClosedOutcome",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.AdmitAsync_VerificationTagMismatchFailsClosedAsCollision",
    ],
    "migration": [
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyTenantLifecycleActorTests.MigrateLegacyAsync_RestartFromEveryDurablePhaseFinishesPinnedTarget",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyTenantLifecycleActorTests.MigrateLegacyAsync_UnsafeSourceEvidenceNeverPreparesTarget",
    ],
}
FAULT_SUPPORT_CLASSIFICATIONS = {
    "route_or_write_ahead_unknown_outcome": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_RouteFailure_MarksUnknownOutcomeUnderSameFence",
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_WriteAheadFailureAfterFenceMarksUnknownOutcome",
    ],
    "durable_pending_or_unknown_authority": [
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionExpiryTests.ValidateAuthorityAsync_PendingAcceptsOnlyExactExecuteAuthorityWithoutMutation",
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionExpiryTests.ValidateAuthorityAsync_UnknownOutcomeAcceptsOnlyExactReconciliationAuthorityWithoutMutation",
    ],
    "tampered_or_lost_authority_zero_work": [
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_TamperedCapabilityLeavesAdmissionUnchangedBeforeBegin",
        "Hexalith.EventStore.Server.Tests.Pipeline.SubmitCommandHandlerIdempotencyAdmissionTests.Handle_ContextThatLostDurableAuthorityPerformsZeroDownstreamWork",
    ],
    "directory_promotion_ordering": [
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionDirectoryActorTests.AdvanceAsync_PromotionOrder_KeepsSourceCanonicalUntilDirectoryFlip",
    ],
    "activation_response_loss_reproof": [
        "Hexalith.EventStore.Server.Tests.Actors.IdempotencyAdmissionActorTests.Coordinator_OrdinaryActivationResponseLossReprovesExactTargetBeforeAdvance",
    ],
}
SUPPORT_CASE_TOTAL = sum(EXPECTED_SUPPORT_METHOD_CASES.values())
DIAGNOSTIC_FORBIDDEN_CLASSES = [
    "protected-input",
    "protected-result",
    "request-identifier",
    "test-key-material",
    "bearer-token",
    "database-credential",
    "private-path",
]


class EvidenceError(RuntimeError):
    """Raised when a fail-closed evidence rule is violated."""


def fail(message: str) -> None:
    raise EvidenceError(message)


def display_path(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        return path.name


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError):
        fail(f"Cannot read evidence path {display_path(path)}")


def reject_duplicate_json_fields(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    document: dict[str, Any] = {}
    for name, value in pairs:
        require(name not in document, "Duplicate JSON field")
        document[name] = value
    return document


def reject_non_finite_json_constant(value: str) -> Any:
    fail(f"Non-finite JSON constant is forbidden: {value}")


def load_json(path: Path) -> Any:
    try:
        with path.open("r", encoding="utf-8") as stream:
            return json.load(
                stream,
                object_pairs_hook=reject_duplicate_json_fields,
                parse_constant=reject_non_finite_json_constant,
            )
    except (OSError, UnicodeError, json.JSONDecodeError):
        fail(f"Cannot load JSON evidence {display_path(path)}")


def load_json_bytes(value: bytes, label: str) -> Any:
    try:
        return json.loads(
            value.decode("utf-8"),
            object_pairs_hook=reject_duplicate_json_fields,
            parse_constant=reject_non_finite_json_constant,
        )
    except (UnicodeError, json.JSONDecodeError):
        fail(f"Cannot load JSON evidence {label}")


def scan_json_protected_content(value: Any, depth: int = 0, visited: list[int] | None = None) -> None:
    require(depth <= 64, "Candidate JSON nesting exceeds the 64-level limit")
    node_count = [0] if visited is None else visited
    node_count[0] += 1
    require(node_count[0] <= 100_000, "Candidate JSON exceeds the 100000-node limit")
    if isinstance(value, dict):
        for name, nested in value.items():
            scan_json_protected_content(name, depth + 1, node_count)
            scan_json_protected_content(nested, depth + 1, node_count)
        return
    if isinstance(value, list):
        for nested in value:
            scan_json_protected_content(nested, depth + 1, node_count)
        return
    if not isinstance(value, str):
        return
    require(PRIVATE_PATH_RE.search(value) is None, "Candidate JSON contains forbidden private-path content")
    lowered = value.lower()
    require(
        all(term.lower() not in lowered for term in FORBIDDEN_CAPTURE_TERMS),
        "Candidate JSON contains forbidden protected content",
    )
    require(PLACEHOLDER_RE.search(value) is None, "Candidate JSON contains a placeholder")
    require(FORBIDDEN_CLAIM_RE.search(value) is None, "Candidate JSON contains a forbidden closure or release claim")


def load_candidate_json(path: Path) -> Any:
    return load_candidate_json_bytes(
        read_bounded_regular_snapshot(
            path,
            MAX_CANDIDATE_JSON_BYTES,
            f"Candidate JSON {display_path(path)}",
            repository_bound=path.is_relative_to(ROOT),
        ),
        display_path(path),
    )


def load_candidate_json_bytes(value: bytes, label: str) -> Any:
    document = load_json_bytes(value, label)
    scan_json_protected_content(document)
    return document


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(f"{path.suffix}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, indent=2, sort_keys=False)
            stream.write("\n")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except OSError:
        fail(f"Cannot hash evidence path {display_path(path)}")
    return digest.hexdigest()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def run_subprocess_bounded(command: list[str], label: str) -> tuple[int, bytes, bytes]:
    try:
        process = subprocess.Popen(
            command,
            cwd=GIT_ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except OSError:
        fail(f"{label} could not start")

    selector: selectors.BaseSelector | None = None
    output = bytearray()
    errors = bytearray()
    deadline = time.monotonic() + GIT_TIMEOUT_SECONDS
    try:
        require(process.stdout is not None and process.stderr is not None, f"{label} output capture failed")
        selector = selectors.DefaultSelector()
        for stream, destination in ((process.stdout, output), (process.stderr, errors)):
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ, destination)

        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                process.kill()
                process.wait()
                fail(f"{label} timed out")
            for key, _ in selector.select(min(remaining, 0.1)):
                try:
                    chunk = os.read(key.fileobj.fileno(), 8192)
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                key.data.extend(chunk)
                if len(output) + len(errors) > GIT_OUTPUT_LIMIT_BYTES:
                    process.kill()
                    process.wait()
                    fail(f"{label} exceeded output limit")

        remaining = deadline - time.monotonic()
        if remaining <= 0:
            process.kill()
            process.wait()
            fail(f"{label} timed out")
        try:
            return_code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            fail(f"{label} timed out")
    except EvidenceError:
        if process.poll() is None:
            process.kill()
            process.wait()
        raise
    except OSError:
        if process.poll() is None:
            process.kill()
            process.wait()
        fail(f"{label} failed safely")
    except BaseException:
        if process.poll() is None:
            process.kill()
            process.wait()
        raise
    finally:
        if selector is not None:
            selector.close()
        if process.stdout is not None:
            process.stdout.close()
        if process.stderr is not None:
            process.stderr.close()

    return return_code, bytes(output), bytes(errors)


def run_git(*arguments: str) -> bytes:
    label = f"Git identity proof for {' '.join(arguments[:2])}"
    return_code, output, _ = run_subprocess_bounded(
        ["git", "--no-replace-objects", *arguments],
        label,
    )
    require(return_code == 0, f"Git identity proof failed for {' '.join(arguments[:2])}")
    return output


def git_diff_is_clean(*arguments: str) -> bool:
    return_code, _, _ = run_subprocess_bounded(
        ["git", "--no-replace-objects", *arguments],
        "Git current-bound-source proof",
    )
    require(return_code in (0, 1), "Git current-bound-source proof failed")
    return return_code == 0


def git_file(revision: str, relative: str) -> bytes:
    require(
        isinstance(relative, str)
        and relative
        and not Path(relative).is_absolute()
        and ".." not in Path(relative).parts,
        "Unsafe Git-bound path",
    )
    return run_git("show", f"{revision}:{relative}")


def sha256_git_file(revision: str, relative: str) -> str:
    require(
        isinstance(relative, str)
        and relative
        and not Path(relative).is_absolute()
        and ".." not in Path(relative).parts,
        "Unsafe Git-bound path",
    )
    label = "Git historical-blob identity proof"
    selector: selectors.BaseSelector | None = None
    try:
        process = subprocess.Popen(
            ["git", "--no-replace-objects", "show", f"{revision}:{relative}"],
            cwd=GIT_ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except OSError:
        fail(f"{label} could not start")

    try:
        require(process.stdout is not None and process.stderr is not None, f"{label} output capture failed")
        selector = selectors.DefaultSelector()
        digest = hashlib.sha256()
        errors = bytearray()
        blob_size = 0
        deadline = time.monotonic() + GIT_TIMEOUT_SECONDS
        for stream, kind in ((process.stdout, "blob"), (process.stderr, "error")):
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ, kind)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                process.kill()
                process.wait()
                fail(f"{label} timed out")
            for key, _ in selector.select(min(remaining, 0.1)):
                try:
                    chunk = os.read(key.fileobj.fileno(), 8192)
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                if key.data == "blob":
                    blob_size += len(chunk)
                    require(blob_size <= GIT_BLOB_LIMIT_BYTES, f"{label} exceeded blob limit")
                    digest.update(chunk)
                else:
                    errors.extend(chunk)
                    require(len(errors) <= GIT_OUTPUT_LIMIT_BYTES, f"{label} exceeded output limit")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            process.kill()
            process.wait()
            fail(f"{label} timed out")
        try:
            return_code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            fail(f"{label} timed out")
    except EvidenceError:
        if process.poll() is None:
            process.kill()
            process.wait()
        raise
    except OSError:
        if process.poll() is None:
            process.kill()
            process.wait()
        fail(f"{label} failed safely")
    except BaseException:
        if process.poll() is None:
            process.kill()
            process.wait()
        raise
    finally:
        if selector is not None:
            selector.close()
        if process.stdout is not None:
            process.stdout.close()
        if process.stderr is not None:
            process.stderr.close()

    require(return_code == 0, f"{label} failed")
    return digest.hexdigest()


def configure_roots(root: Path, git_root: Path, git_timeout_seconds: float = 30.0) -> None:
    global ROOT, GIT_ROOT, GIT_TIMEOUT_SECONDS, PACKET, EVIDENCE, CLOSURE, SUCCESSOR_SELECTOR, SUCCESSOR, V2_SUCCESSOR, V3_SUCCESSOR, V4_SUCCESSOR, LIFECYCLE_STATE
    require(0 < git_timeout_seconds <= 30, "Git timeout must be greater than zero and no more than 30 seconds")
    ROOT = root.resolve()
    GIT_ROOT = git_root.resolve()
    GIT_TIMEOUT_SECONDS = git_timeout_seconds
    PACKET = ROOT / "_bmad-output/implementation-artifacts/4-8-eventstore-oq8-platform-evidence.yaml"
    EVIDENCE = (
        ROOT
        / "_bmad-output/implementation-artifacts/evidence/story-4-14"
        / BASELINE
    )
    CLOSURE = (
        ROOT
        / "_bmad-output/implementation-artifacts/evidence/story-4-15"
        / LANDED_SOURCE
    )
    SUCCESSOR_SELECTOR = ROOT / SUCCESSOR_SELECTOR_PATH
    SUCCESSOR = ROOT / SUCCESSOR_DIRECTORY
    V2_SUCCESSOR = ROOT / V2_SUCCESSOR_DIRECTORY
    V3_SUCCESSOR = ROOT / V3_SUCCESSOR_DIRECTORY
    V4_SUCCESSOR = ROOT / V4_SUCCESSOR_DIRECTORY
    LIFECYCLE_STATE = ROOT / LIFECYCLE_STATE_PATH


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def scan_support_safe(path: Path) -> None:
    text = read_text(path)
    scan_support_safe_text(text, path.name)


def scan_support_safe_text(text: str, label: str) -> None:
    require(not PRIVATE_PATH_RE.search(text), f"Private path found in {label}")
    require(not PLACEHOLDER_RE.search(text), f"Placeholder found in {label}")
    require(not FORBIDDEN_CLAIM_RE.search(text), f"Closure/release claim found in {label}")
    for term in FORBIDDEN_CAPTURE_TERMS:
        require(term.lower() not in text.lower(), f"Protected or secret-like term found in {label}")


def require_no_symlink_components(path: Path, label: str) -> None:
    try:
        relative = path.relative_to(ROOT)
    except ValueError:
        fail(f"{label} is outside the repository root")
    current = ROOT
    components = [(Path("."), ROOT), *[(Path(*relative.parts[:index]), ROOT.joinpath(*relative.parts[:index])) for index in range(1, len(relative.parts) + 1)]]
    for component, candidate in components:
        try:
            require(not candidate.is_symlink(), f"{label} has a symlinked path component: {component.as_posix()}")
        except OSError:
            fail(f"{label} path component cannot be inspected: {component.as_posix()}")
        current = candidate


def read_bounded_regular_snapshot(
    path: Path,
    maximum_bytes: int,
    label: str,
    *,
    repository_bound: bool = True,
) -> bytes:
    if repository_bound:
        require_no_symlink_components(path, label)
    else:
        require(not path.is_symlink(), f"{label} must be a regular non-symlink file")
    try:
        metadata = path.stat()
    except OSError:
        fail(f"{label} is missing")
    require(
        path.is_file(),
        f"{label} is not a regular file" if repository_bound else f"{label} must be a regular non-symlink file",
    )
    require(metadata.st_size <= maximum_bytes, f"{label} exceeds the {maximum_bytes}-byte limit")
    try:
        with path.open("rb") as stream:
            value = stream.read(maximum_bytes + 1)
    except OSError:
        fail(f"Cannot read {label}")
    require(len(value) <= maximum_bytes, f"{label} exceeds the {maximum_bytes}-byte limit")
    return value


def read_bounded_raw_input(path: Path, label: str) -> bytes:
    return read_bounded_regular_snapshot(
        path,
        MAX_RAW_CTRF_BYTES,
        label,
        repository_bound=False,
    )


def decode_utf8(value: bytes, label: str) -> str:
    try:
        return value.decode("utf-8")
    except UnicodeError:
        fail(f"{label} is not UTF-8")


def read_bounded_text_snapshot(
    path: Path,
    maximum_bytes: int,
    label: str,
    *,
    repository_bound: bool = True,
) -> str:
    return decode_utf8(
        read_bounded_regular_snapshot(
            path,
            maximum_bytes,
            label,
            repository_bound=repository_bound,
        ),
        label,
    )


def load_bounded_json(
    path: Path,
    maximum_bytes: int,
    label: str,
    *,
    repository_bound: bool = True,
) -> Any:
    return load_json_bytes(
        read_bounded_regular_snapshot(
            path,
            maximum_bytes,
            label,
            repository_bound=repository_bound,
        ),
        label,
    )


def relative_tree_entries(root: Path) -> set[str]:
    entries: set[str] = set()

    def fail_enumeration(_: OSError) -> None:
        fail(f"Cannot enumerate evidence path {display_path(root)}")

    try:
        for directory, directories, files in os.walk(
            root,
            followlinks=False,
            onerror=fail_enumeration,
        ):
            current = Path(directory)
            for name in directories:
                entries.add((current / name).relative_to(root).as_posix())
            for name in files:
                entries.add((current / name).relative_to(root).as_posix())
    except OSError:
        fail(f"Cannot enumerate evidence path {display_path(root)}")
    return entries


def require_sha256(value: Any, field: str) -> str:
    require(isinstance(value, str) and SHA256_RE.fullmatch(value) is not None, f"{field} is not SHA-256")
    return value


def require_exact_integer(value: Any, expected: int, field: str) -> None:
    require(type(value) is int, f"{field} must be an exact integer")
    require(value == expected, f"{field} count drift")


def require_nonnegative_integer(value: Any, field: str) -> int:
    require(type(value) is int, f"{field} must be a non-negative integer")
    require(value >= 0, f"{field} must be a non-negative integer")
    return value


def require_exact_fields(value: Any, fields: set[str], label: str) -> dict[str, Any]:
    require(isinstance(value, dict) and set(value) == fields, f"{label} field set drift")
    return value


def validate_observations(
    path: Path,
    expected_dapr_runtime_version: str,
    expected_postgres_image: str,
    profile_identity_revision: str | None = None,
    historical: bool = False,
    expected_configuration: str = "Release",
) -> dict[str, Any]:
    require(
        re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", expected_dapr_runtime_version) is not None,
        "Expected Dapr runtime version must be one exact semantic version",
    )
    observation_bytes = read_bounded_regular_snapshot(
        path,
        MAX_CANDIDATE_JSON_BYTES,
        "observations.json",
        repository_bound=path.is_relative_to(ROOT),
    )
    scan_support_safe_text(decode_utf8(observation_bytes, "observations.json"), "observations.json")
    document = load_json_bytes(observation_bytes, "observations.json")
    require(isinstance(document, dict), "observations.json must be an object")
    require_exact_fields(
        document,
        {"schemaVersion", "captureKind", "capturedOn", "topology", "profile", "runtime", "executionConfiguration", "artifacts", "diagnostics", "observations"},
        "Observation",
    )
    require_exact_integer(document.get("schemaVersion"), 1, "Observation schemaVersion")
    require(expected_configuration in {"Debug", "Release"}, "Unsupported capture configuration")
    require(document.get("captureKind") == f"{expected_configuration.lower()}-entry-binaries-test-seams-sidecar-postgresql", "Observation capture kind drift")
    captured_on_text = document.get("capturedOn")
    require(isinstance(captured_on_text, str), "Capture date missing")
    require(
        re.fullmatch(r"\d{4}-\d{2}-\d{2}", captured_on_text) is not None,
        "Capture date must use YYYY-MM-DD",
    )
    try:
        captured_on = datetime.strptime(captured_on_text, "%Y-%m-%d").replace(tzinfo=timezone.utc)
    except ValueError:
        fail("Capture date must be a real UTC calendar date")
    require(captured_on <= datetime.now(timezone.utc), "Capture date cannot be in the future")

    topology = document.get("topology", {})
    require_exact_fields(topology, {"eventStoreProcessCount", "eventStoreSidecarCount", "sampleProcessCount", "sampleSidecarCount", "independentProcessIdentities"}, "Observation topology")
    for field in ("eventStoreProcessCount", "eventStoreSidecarCount", "sampleProcessCount", "sampleSidecarCount"):
        require_nonnegative_integer(topology.get(field), f"Observation topology {field}")
    require(topology.get("eventStoreProcessCount") == 2, "Two EventStore processes were not observed")
    require(topology.get("eventStoreSidecarCount") == 2, "Two EventStore sidecars were not observed")
    require(topology.get("sampleProcessCount") == 1, "The Sample process was not observed")
    require(topology.get("sampleSidecarCount") == 1, "The Sample sidecar was not observed")
    require(topology.get("independentProcessIdentities") is True, "Process identities were not independent")

    profile = document.get("profile", {})
    require_exact_fields(profile, {"name", "stateStoreType", "stateComponentSha256", "resiliencySha256"}, "Observation profile")
    require(profile.get("name") == PROFILE, "OQ8 profile drift")
    require(profile.get("stateStoreType") == "state.postgresql", "State store is not PostgreSQL")
    state_component_sha256 = require_sha256(
        profile.get("stateComponentSha256"),
        "state component identity",
    )
    resiliency_sha256 = require_sha256(
        profile.get("resiliencySha256"),
        "resiliency identity",
    )
    if profile_identity_revision is None:
        expected_state_component_sha256 = sha256_file(ROOT / "deploy/dapr/statestore-postgresql.yaml")
        expected_resiliency_sha256 = sha256_file(ROOT / "deploy/dapr/resiliency.yaml")
    else:
        expected_state_component_sha256 = sha256_git_file(
            profile_identity_revision,
            "deploy/dapr/statestore-postgresql.yaml",
        )
        expected_resiliency_sha256 = sha256_git_file(
            profile_identity_revision,
            "deploy/dapr/resiliency.yaml",
        )
    require(
        state_component_sha256 == expected_state_component_sha256,
        "PostgreSQL component identity drift",
    )
    require(
        resiliency_sha256 == expected_resiliency_sha256,
        "Resiliency identity drift",
    )

    runtime = document.get("runtime", {})
    require_exact_fields(runtime, {"dotnet", "dapr", "postgresImage", "postgresImageIdentity"}, "Observation runtime")
    require(isinstance(runtime.get("dotnet"), str) and runtime["dotnet"], ".NET runtime identity missing")
    require(runtime.get("dapr") == expected_dapr_runtime_version, "Dapr runtime identity drift")
    require(runtime.get("postgresImage") == expected_postgres_image, "PostgreSQL image identity drift")
    require(
        isinstance(runtime.get("postgresImageIdentity"), str)
        and re.fullmatch(r"sha256:[0-9a-f]{64}", runtime["postgresImageIdentity"]) is not None,
        "PostgreSQL immutable identity must be an exact sha256 digest",
    )

    execution_configuration = document.get("executionConfiguration", {})
    require(
        execution_configuration == {
            "shippedReleaseEntryAssemblies": expected_configuration == "Release",
            "shadowCopiedBeforeLaunch": True,
            "environmentName": "Testing",
            "testOnlyHostingStartup": True,
            "productionConfigurationUntouched": False,
            "seams": ["deterministic-time", "idempotency-intent-adapter", "boundary-counter"],
        },
        "Test-only execution configuration disclosure drift",
    )

    artifacts = document.get("artifacts", {})
    require(set(artifacts) == {
        "eventStoreSha256",
        "sampleSha256",
        "eventStoreRuntimeSetSha256",
        "sampleRuntimeSetSha256",
        "hostingStartupSha256",
        "additionalDepsSha256",
    }, "Runtime artifact identity set drift")
    for name, value in artifacts.items():
        require_sha256(value, f"runtime artifact identity:{name}")

    diagnostics = document.get("diagnostics", {})
    require_exact_fields(diagnostics, {"streamsScanned", "boundedCharacterLimitPerStream", "forbiddenTermClassesScanned", "postRedactionProtectedMatches", "rawDiagnosticsCommitted", "sanitizedProjectionSha256"}, "Observation diagnostics")
    for field in ("streamsScanned", "boundedCharacterLimitPerStream", "postRedactionProtectedMatches"):
        require_nonnegative_integer(diagnostics.get(field), f"Observation diagnostics {field}")
    require(diagnostics.get("streamsScanned") == 12, "All bounded process diagnostic streams were not scanned")
    require(diagnostics.get("boundedCharacterLimitPerStream") == 32768, "Diagnostic stream bound drift")
    require(diagnostics.get("forbiddenTermClassesScanned") == DIAGNOSTIC_FORBIDDEN_CLASSES, "Diagnostic forbidden-term class coverage drift")
    require(diagnostics.get("postRedactionProtectedMatches") == 0, "Protected diagnostics remain after redaction")
    require(diagnostics.get("rawDiagnosticsCommitted") is False, "Raw diagnostics were committed")
    require_sha256(diagnostics.get("sanitizedProjectionSha256"), "Sanitized diagnostic projection identity")

    observations = document.get("observations", {})
    require(set(observations) == {"writers_failover", "expiry_compaction", "authority_change", "capture"}, "Observation matrix is incomplete")
    writers = observations["writers_failover"]
    require_exact_fields(writers, {"concurrentRequests", "canonicalExecutionIdentities", "durableFencePositive", "sampleExecutions", "ownerStoppedAtTerminalBoundary", "failoverAttempts", "failoverReplayExact", "restartedNodeReplayExact", "conflictStatus", "crossTargetConflictStatus", "nonExecuteAdditionalWork"}, "Writer/failover observation")
    for field in ("concurrentRequests", "canonicalExecutionIdentities", "sampleExecutions", "failoverAttempts", "nonExecuteAdditionalWork"):
        require_nonnegative_integer(writers.get(field), f"Writer/failover observation {field}")
    require(writers.get("concurrentRequests", 0) >= 2, "Concurrent writer count is insufficient")
    require(writers.get("canonicalExecutionIdentities") == 1, "Canonical execution identity count is not one")
    require(writers.get("durableFencePositive") is True, "Durable positive fence was not observed")
    require(writers.get("sampleExecutions") == 1, "Sample execution count is not one")
    require(writers.get("ownerStoppedAtTerminalBoundary") is True, "Known owner failover was not observed")
    require(writers.get("failoverAttempts", 0) >= 1, "Failover request was not observed")
    require(writers.get("failoverReplayExact") is True, "Failover replay content was not exact")
    require(writers.get("restartedNodeReplayExact") is True, "Restart replay was not exact")
    require_exact_integer(writers.get("conflictStatus"), 409, "Writer/failover observation conflictStatus")
    require_exact_integer(writers.get("crossTargetConflictStatus"), 409, "Writer/failover observation crossTargetConflictStatus")
    require(writers.get("nonExecuteAdditionalWork") == 0, "Writer non-execute path performed work")

    expiry = observations["expiry_compaction"]
    require_exact_fields(expiry, {"oneTickBefore", "oneTickBeforeReplayExact", "inclusiveBoundary", "oneTickAfter", "terminalBecameMinimalTombstone", "equivalentAndDifferentReuseShareOutcome", "nonExecuteAdditionalWork"}, "Expiry observation")
    require_nonnegative_integer(expiry.get("nonExecuteAdditionalWork"), "Expiry observation nonExecuteAdditionalWork")
    require_exact_integer(expiry.get("oneTickBefore"), 202, "Expiry observation oneTickBefore")
    require(expiry.get("oneTickBeforeReplayExact") is True, "T-1 replay content was not exact")
    require_exact_integer(expiry.get("inclusiveBoundary"), 409, "Expiry observation inclusiveBoundary")
    require_exact_integer(expiry.get("oneTickAfter"), 409, "Expiry observation oneTickAfter")
    require(expiry.get("terminalBecameMinimalTombstone") is True, "Expiry did not compact atomically")
    require(expiry.get("equivalentAndDifferentReuseShareOutcome") is True, "Expired reuse outcomes diverged")
    require(expiry.get("nonExecuteAdditionalWork") == 0, "Expiry non-execute path performed work")

    authority = observations["authority_change"]
    require_exact_fields(authority, {"rotationReplayExact", "canonicalAuthorityCount", "retiredReaderReplayExact", "legalHoldState", "releasedState", "failClosedStatuses", "sampleExecutions", "nonExecuteAdditionalWork", "deterministicSupportOracles"}, "Authority-change observation")
    for field in ("canonicalAuthorityCount", "sampleExecutions", "nonExecuteAdditionalWork"):
        require_nonnegative_integer(authority.get(field), f"Authority-change observation {field}")
    require(authority.get("rotationReplayExact") is True, "Rotation replay was not exact")
    require(authority.get("canonicalAuthorityCount") == 1, "Rotated canonical authority count is not one")
    require(authority.get("retiredReaderReplayExact") is True, "Retired-reader replay was not exact")
    require(authority.get("legalHoldState") == "LegalHold", "Legal hold was not serialized")
    require(authority.get("releasedState") == "Retaining", "Hold release did not restore retaining state")
    fail_closed_statuses = authority.get("failClosedStatuses")
    require(
        isinstance(fail_closed_statuses, list)
        and all(type(status) is int for status in fail_closed_statuses)
        and fail_closed_statuses == [503, 503],
        "Governance unavailable states did not fail closed",
    )
    require(authority.get("sampleExecutions") == 2, "Authority-change eligible execution count is not two")
    require(authority.get("nonExecuteAdditionalWork") == 0, "Authority non-execute path performed work")
    require(
        authority.get("deterministicSupportOracles") == list(EXPECTED_SUPPORT_METHOD_CASES),
        "Deterministic support oracle identities or order drifted",
    )

    capture = observations["capture"]
    require_exact_fields(capture, {"before", "after", "protectedSentinelMatches", "committedProjectionContainsIdentifiers", "closureClaimed"}, "Capture observation")
    before = capture.get("before", {})
    after = capture.get("after", {})
    capture_snapshot_fields = {"stage", "schemaSha256", "projectionSha256", "totalRows", "admissionRows", "terminalRows", "tombstoneRows", "minimalTombstoneRows", "directoryRows", "lifecycleRows", "aggregateMetadataRows", "aggregateEventRows", "aggregateSequenceTotal", "protectedSentinelMatches"}
    require_exact_fields(before, capture_snapshot_fields, "Before capture snapshot")
    require_exact_fields(after, capture_snapshot_fields, "After capture snapshot")
    capture_snapshot_counters = capture_snapshot_fields - {"stage", "schemaSha256", "projectionSha256"}
    for label, snapshot in (("Before", before), ("After", after)):
        for field in capture_snapshot_counters:
            require_nonnegative_integer(snapshot.get(field), f"{label} capture snapshot {field}")
    require_nonnegative_integer(capture.get("protectedSentinelMatches"), "Capture observation protectedSentinelMatches")
    require(before.get("stage") == "before" and after.get("stage") == "after", "Before/after snapshot labels drifted")
    require(before.get("schemaSha256") == after.get("schemaSha256"), "PostgreSQL schema changed during capture")
    require_sha256(before.get("schemaSha256"), "PostgreSQL schema identity")
    require_sha256(before.get("projectionSha256"), "Before projection identity")
    require_sha256(after.get("projectionSha256"), "After projection identity")
    if not historical:
        for label, snapshot in (("Before", before), ("After", after)):
            require(
                snapshot.get("terminalRows", 0) <= snapshot.get("admissionRows", 0),
                f"{label} capture snapshot terminalRows exceeds admissionRows",
            )
            require(
                snapshot.get("minimalTombstoneRows", 0) <= snapshot.get("tombstoneRows", 0),
                f"{label} capture snapshot minimalTombstoneRows exceeds tombstoneRows",
            )
    require(after.get("aggregateSequenceTotal") == before.get("aggregateSequenceTotal", 0) + 4, "Eligible execution count is not four")
    require(after.get("admissionRows") == before.get("admissionRows", 0) + 4, "Admission row delta is not exactly four")
    require(after.get("terminalRows") == before.get("terminalRows", 0) + 4, "Terminal row delta is not exactly four")
    require(after.get("tombstoneRows", 0) >= before.get("tombstoneRows", 0) + 1, "Tombstone row delta missing")
    require(after.get("totalRows", 0) > before.get("totalRows", 0), "Total PostgreSQL row count did not increase")
    require(after.get("aggregateMetadataRows") == before.get("aggregateMetadataRows", 0) + 4, "Aggregate metadata row delta is not exactly four")
    require(after.get("aggregateEventRows") == before.get("aggregateEventRows", 0) + 4, "Aggregate event row delta is not exactly four")
    require(after.get("minimalTombstoneRows", 0) >= before.get("minimalTombstoneRows", 0) + 1, "Minimal tombstone delta missing")
    require(after.get("directoryRows", 0) > before.get("directoryRows", 0), "Digest directory state missing")
    require(after.get("lifecycleRows", 0) > before.get("lifecycleRows", 0), "Tenant lifecycle state missing")
    for label, snapshot in (("Before", before), ("After", after)):
        disjoint_rows = sum(
            snapshot.get(field, 0)
            for field in (
                "admissionRows",
                "tombstoneRows",
                "directoryRows",
                "lifecycleRows",
                "aggregateMetadataRows",
                "aggregateEventRows",
            )
        )
        require(
            snapshot.get("totalRows", 0) >= disjoint_rows,
            f"{label} capture snapshot totalRows is smaller than its disjoint row counts",
        )
    require(
        before.get("protectedSentinelMatches") == 0
        and after.get("protectedSentinelMatches") == 0
        and capture.get("protectedSentinelMatches") == 0,
        "Protected sentinel leakage detected",
    )
    require(capture.get("committedProjectionContainsIdentifiers") is False, "Committed projection contains identifiers")
    require(capture.get("closureClaimed") is False, "Capture claims closure")
    return document


def sanitize_ctrf(ctrf_path: Path, destination: Path, expected_command: str = FOCUSED_CURRENT_COMMAND) -> dict[str, Any]:
    ctrf = load_json_bytes(read_bounded_raw_input(ctrf_path, "Raw focused CTRF"), "raw focused CTRF")
    require(isinstance(ctrf, dict), "Focused CTRF must be an object")
    results = ctrf.get("results")
    require(isinstance(results, dict), "Focused CTRF results must be an object")
    summary = results.get("summary")
    require(isinstance(summary, dict), "Focused CTRF summary must be an object")
    tests = results.get("tests")
    require_exact_integer(summary.get("tests"), 1, "Focused CTRF summary tests")
    require_exact_integer(summary.get("passed"), 1, "Focused CTRF summary passed")
    require_exact_integer(summary.get("failed"), 0, "Focused CTRF summary failed")
    require_exact_integer(summary.get("skipped"), 0, "Focused CTRF summary skipped")
    require(isinstance(tests, list) and len(tests) == 1, "Focused CTRF test record is incomplete")
    test = tests[0]
    require(isinstance(test, dict), "Focused CTRF contains an invalid test record")
    require(test.get("name") == FOCUSED_METHOD, "Focused CTRF test identity drift")
    require(test.get("labels") == FOCUSED_LABELS, "Focused CTRF xUnit 4 Category/Profile labels drift")
    require(test.get("tags") == ["LiveSidecar"], "Focused CTRF xUnit 4 Category tag drift")
    portable = {
        "schemaVersion": 1,
        "runner": "xUnit.net v3",
        "command": expected_command,
        "summary": {
            "tests": 1,
            "passed": 1,
            "failed": 0,
            "skipped": 0,
        },
        "test": {
            "name": test.get("name"),
            "status": test.get("status"),
            "durationMilliseconds": test.get("duration"),
            "traits": {name: [value] for name, value in test.get("labels", {}).items()},
        },
    }
    validate_focused_document(portable, expected_command)
    write_json(destination, portable)
    scan_support_safe(destination)
    return portable


def expected_support_classifications() -> dict[str, Any]:
    classifications = {
        classification: {
            "commandExecutionWork": 0,
            "methods": methods,
        }
        for classification, methods in SUPPORT_CLASSIFICATIONS.items()
    }
    classifications.update({
        classification: {
            "evidenceRole": "deterministic-fault-or-fence-support",
            "methods": methods,
        }
        for classification, methods in FAULT_SUPPORT_CLASSIFICATIONS.items()
    })
    return classifications


def validate_focused_document(document: Any, expected_command: str = FOCUSED_LEGACY_COMMAND) -> dict[str, Any]:
    require(isinstance(document, dict), "test-results.json must be an object")
    require_exact_fields(document, {"schemaVersion", "runner", "command", "summary", "test"}, "Focused result")
    require_exact_integer(document.get("schemaVersion"), 1, "Focused result schemaVersion")
    require(document.get("runner") == "xUnit.net v3", "Focused result runner drift")
    require(
        document.get("command") == expected_command,
        "Focused result command identity drift",
    )
    summary = require_exact_fields(document.get("summary"), {"tests", "passed", "failed", "skipped"}, "Focused result summary")
    require_exact_integer(summary.get("tests"), 1, "Focused result summary tests")
    require_exact_integer(summary.get("passed"), 1, "Focused result summary passed")
    require_exact_integer(summary.get("failed"), 0, "Focused result summary failed")
    require_exact_integer(summary.get("skipped"), 0, "Focused result summary skipped")
    test = document.get("test", {})
    require(isinstance(test, dict), "Focused result test must be an object")
    require_exact_fields(test, {"name", "status", "durationMilliseconds", "traits"}, "Focused result test")
    require(test.get("name") == FOCUSED_METHOD, "Focused result test identity drift")
    require(test.get("status") == "passed", "Focused test status is not passed")
    require(test.get("traits") == FOCUSED_TRAITS, "Focused result Category/Profile traits drift")
    duration = test.get("durationMilliseconds")
    require(
        (type(duration) is int and duration >= 0)
        or (type(duration) is float and math.isfinite(duration) and duration >= 0),
        "Focused result duration must be a finite non-negative number",
    )
    return document


def validate_support_document(document: Any, expected_command: str = SUPPORT_LEGACY_COMMAND) -> dict[str, Any]:
    require(isinstance(document, dict), "deterministic-support.json must be an object")
    require_exact_fields(document, {"schemaVersion", "runner", "command", "selectors", "summary", "methods", "classifications"}, "Deterministic support")
    require_exact_integer(document.get("schemaVersion"), 1, "Deterministic support schemaVersion")
    require(document.get("runner") == "xUnit.net v3", "Deterministic support runner drift")
    require(
        document.get("command") == expected_command,
        "Deterministic support command identity drift",
    )
    require(document.get("selectors") == list(EXPECTED_SUPPORT_METHOD_CASES), "Deterministic support selectors drift")
    summary = require_exact_fields(document.get("summary"), {"tests", "passed", "failed", "skipped"}, "Deterministic support summary")
    require_exact_integer(summary.get("tests"), SUPPORT_CASE_TOTAL, "Deterministic support summary tests")
    require_exact_integer(summary.get("passed"), SUPPORT_CASE_TOTAL, "Deterministic support summary passed")
    require_exact_integer(summary.get("failed"), 0, "Deterministic support summary failed")
    require_exact_integer(summary.get("skipped"), 0, "Deterministic support summary skipped")
    expected_methods = [
        {
            "identity": identity,
            "expectedCases": expected_cases,
            "observedCases": expected_cases,
            "passedCases": expected_cases,
        }
        for identity, expected_cases in EXPECTED_SUPPORT_METHOD_CASES.items()
    ]
    require(document.get("methods") == expected_methods, "Deterministic support method identities or exact case counts drifted")
    require(
        document.get("classifications") == expected_support_classifications(),
        "Deterministic support non-execute classification coverage drifted",
    )
    return document


def sanitize_support_ctrf(ctrf_path: Path, destination: Path, expected_command: str = SUPPORT_CURRENT_COMMAND) -> dict[str, Any]:
    ctrf = load_json_bytes(read_bounded_raw_input(ctrf_path, "Raw support CTRF"), "raw support CTRF")
    require(isinstance(ctrf, dict), "Deterministic support CTRF must be an object")
    results = ctrf.get("results")
    require(isinstance(results, dict), "Deterministic support CTRF results must be an object")
    summary = results.get("summary")
    require(isinstance(summary, dict), "Deterministic support CTRF summary must be an object")
    tests = results.get("tests")
    require_exact_integer(summary.get("tests"), SUPPORT_CASE_TOTAL, "Deterministic support CTRF summary tests")
    require_exact_integer(summary.get("passed"), SUPPORT_CASE_TOTAL, "Deterministic support CTRF summary passed")
    require_exact_integer(summary.get("failed"), 0, "Deterministic support CTRF summary failed")
    require_exact_integer(summary.get("skipped"), 0, "Deterministic support CTRF summary skipped")
    require(isinstance(tests, list) and len(tests) == SUPPORT_CASE_TOTAL, "Deterministic support CTRF records are incomplete")

    observed = {identity: 0 for identity in EXPECTED_SUPPORT_METHOD_CASES}
    passed = {identity: 0 for identity in EXPECTED_SUPPORT_METHOD_CASES}
    observed_names: set[str] = set()
    for test in tests:
        require(isinstance(test, dict), "Deterministic support CTRF contains an invalid test record")
        name = test.get("name")
        require(isinstance(name, str), "Deterministic support CTRF test identity is missing")
        require(name not in observed_names, f"Duplicate deterministic support test case: {name}")
        observed_names.add(name)
        matches = [identity for identity in EXPECTED_SUPPORT_METHOD_CASES if name == identity or name.startswith(f"{identity}(")]
        require(len(matches) == 1, f"Unexpected or ambiguous deterministic support test: {name}")
        identity = matches[0]
        observed[identity] += 1
        require(test.get("status") == "passed", f"Deterministic support case did not pass: {identity}")
        passed[identity] += 1

    for identity, expected_cases in EXPECTED_SUPPORT_METHOD_CASES.items():
        require(observed[identity] == expected_cases, f"Deterministic support case count drifted: {identity}")
        require(passed[identity] == expected_cases, f"Deterministic support pass count drifted: {identity}")

    portable = {
        "schemaVersion": 1,
        "runner": "xUnit.net v3",
        "command": expected_command,
        "selectors": list(EXPECTED_SUPPORT_METHOD_CASES),
        "summary": {
            "tests": SUPPORT_CASE_TOTAL,
            "passed": SUPPORT_CASE_TOTAL,
            "failed": 0,
            "skipped": 0,
        },
        "methods": [
            {
                "identity": identity,
                "expectedCases": expected_cases,
                "observedCases": observed[identity],
                "passedCases": passed[identity],
            }
            for identity, expected_cases in EXPECTED_SUPPORT_METHOD_CASES.items()
        ],
        "classifications": expected_support_classifications(),
    }
    validate_support_document(portable, expected_command)
    write_json(destination, portable)
    scan_support_safe(destination)
    return portable


def validate_capture(
    capture_directory: Path,
    ctrf_path: Path,
    support_ctrf_path: Path,
    expected_dapr_runtime_version: str,
    expected_configuration: str = "Release",
) -> None:
    require(capture_directory.is_dir(), "Capture directory is missing")
    require(not ctrf_path.is_relative_to(capture_directory), "Raw focused CTRF must remain outside the capture directory")
    require(not support_ctrf_path.is_relative_to(capture_directory), "Raw support CTRF must remain outside the capture directory")
    require(
        {path.name for path in capture_directory.iterdir()} == {"observations.json"},
        "Fresh capture directory contains unexpected pre-existing files",
    )
    observations_path = capture_directory / "observations.json"
    require(observations_path.is_file(), "Capture observations.json is missing")
    validate_observations(observations_path, expected_dapr_runtime_version, POSTGRES_IMAGE, expected_configuration=expected_configuration)
    sanitize_ctrf(ctrf_path, capture_directory / "test-results.json", FOCUSED_CURRENT_COMMAND.replace("/Release/", f"/{expected_configuration}/"))
    sanitize_support_ctrf(support_ctrf_path, capture_directory / "deterministic-support.json", SUPPORT_CURRENT_COMMAND.replace("/Release/", f"/{expected_configuration}/"))
    receipt = {
        "schemaVersion": 1,
        "validation": "passed",
        "observationsSha256": sha256_file(observations_path),
        "testResultsSha256": sha256_file(capture_directory / "test-results.json"),
        "deterministicSupportSha256": sha256_file(capture_directory / "deterministic-support.json"),
    }
    write_json(capture_directory / "capture-validation.json", receipt)
    scan_support_safe(capture_directory / "capture-validation.json")
    require(
        {path.name for path in capture_directory.iterdir()}
        == {"observations.json", "test-results.json", "deterministic-support.json", "capture-validation.json"},
        "Sanitized capture output file set drift",
    )


def validate_manifest() -> dict[str, str]:
    manifest_path = EVIDENCE / "evidence-sha256.txt"
    manifest_text = read_bounded_text_snapshot(
        manifest_path,
        MAX_HISTORICAL_MANIFEST_BYTES,
        "Story 4.14 evidence manifest",
    )
    scan_support_safe_text(manifest_text, "evidence-sha256.txt")
    manifest: dict[str, str] = {}
    for line in manifest_text.splitlines():
        parts = line.split("  ", 1)
        require(len(parts) == 2 and SHA256_RE.fullmatch(parts[0]) is not None, "Malformed evidence manifest line")
        digest, name = parts
        require("/" not in name and "\\" not in name and name not in manifest, "Unsafe or duplicate manifest name")
        manifest[name] = digest
    require(set(manifest) == REQUIRED_FILES, "Evidence manifest file set is incomplete or contains extras")
    require(
        relative_tree_entries(EVIDENCE) == REQUIRED_FILES | {"evidence-sha256.txt"},
        "Evidence directory file set drift",
    )
    for name, expected in manifest.items():
        path = EVIDENCE / name
        artifact_bytes = read_bounded_regular_snapshot(
            path,
            MAX_CANDIDATE_JSON_BYTES,
            f"Story 4.14 evidence artifact {name}",
        )
        require(sha256_bytes(artifact_bytes) == expected, f"Evidence checksum mismatch: {name}")
        scan_support_safe_text(
            decode_utf8(artifact_bytes, f"Story 4.14 evidence artifact {name}"),
            name,
        )
    return manifest


def validate_successor_source_identity() -> dict[str, Any]:
    require_no_symlink_components(SUCCESSOR, "Story 4.15 historical successor directory")
    require(SUCCESSOR.is_dir(), "Story 4.15 successor directory is missing")

    identity_path = SUCCESSOR / "source-artifact-identity.json"
    require(identity_path.is_file(), "Story 4.15 successor source identity is missing")
    identity = load_candidate_json(identity_path)
    require(isinstance(identity, dict), "Story 4.15 successor source identity must be an object")
    require(
        set(identity)
        == {
            "schema",
            "reviewedOn",
            "repository",
            "reviewedBaseCommit",
            "priorLandedSource",
            "reason",
            "bindingRule",
            "replacedPriorBoundPaths",
            "boundPaths",
            "validation",
        },
        "Story 4.15 successor source identity field set drift",
    )
    require(
        identity.get("schema") == "hexalith.eventstore.story-4-15-successor-source-identity/v1",
        "Story 4.15 successor source identity schema drift",
    )
    require(identity.get("reviewedOn") == "2026-08-29", "Story 4.15 successor review date drift")
    require(identity.get("repository") == "Hexalith/Hexalith.EventStore", "Story 4.15 successor repository drift")
    require(identity.get("reviewedBaseCommit") == SUCCESSOR_REVIEW_BASE, "Story 4.15 successor review base drift")
    require(identity.get("priorLandedSource") == LANDED_SOURCE, "Story 4.15 successor prior source drift")
    require(
        identity.get("reason")
        == "SDK 10.0.400 requires Microsoft.Testing.Platform, xUnit 4 serialization metadata, and Linux-safe OQ8 control-plane discovery.",
        "Story 4.15 successor reason drift",
    )
    require(
        identity.get("bindingRule")
        == "Every listed current worktree source path must exist as a regular file and match its reviewed SHA-256; reviewedBaseCommit is an ancestry base only and does not claim those bound worktree bytes exist in that commit.",
        "Story 4.15 successor source binding rule drift",
    )
    require(
        identity.get("replacedPriorBoundPaths") == sorted(REPLACED_PRIOR_BOUND_PATHS),
        "Story 4.15 successor replaced-path declaration drift",
    )
    bound_paths = identity.get("boundPaths")
    require(
        isinstance(bound_paths, dict) and set(bound_paths) == SUCCESSOR_SOURCE_PATHS,
        "Story 4.15 successor current source path set drift",
    )
    for relative, expected in bound_paths.items():
        path = Path(relative)
        require(
            not path.is_absolute() and ".." not in path.parts and path.as_posix() == relative,
            "Story 4.15 successor source path is unsafe",
        )
        require_sha256(expected, f"Story 4.15 successor source:{relative}")
        require(
            sha256_git_file(LEGACY_SUCCESSOR_SNAPSHOT_COMMIT, relative) == expected,
            f"Story 4.15 historical successor source identity drift: {relative}",
        )
    require(
        identity.get("validation")
        == {
            "sdk": "10.0.400",
            "xunitParallelization": "ParallelMode.None",
            "dockerPublishedControlPlanePorts": True,
            "nugetPackageCacheProbing": True,
            "focusedPortResolverTests": {"total": 9, "passed": 9, "failed": 0, "skipped": 0},
            "focusedProductionOq8": {"total": 1, "passed": 1, "failed": 0, "skipped": 0},
        },
        "Story 4.15 successor validation record drift",
    )
    run_git("merge-base", "--is-ancestor", SUCCESSOR_REVIEW_BASE, LEGACY_SUCCESSOR_SNAPSHOT_COMMIT)
    return identity


def validate_successor_manifest() -> dict[str, str]:
    require_no_symlink_components(SUCCESSOR, "Story 4.15 historical successor directory")
    require(SUCCESSOR.is_dir(), "Story 4.15 successor directory is missing")
    require(
        relative_tree_entries(SUCCESSOR)
        == SUCCESSOR_FILES | {"successor-sha256.txt", "reviews"},
        "Story 4.15 successor directory file set drift",
    )
    manifest_path = SUCCESSOR / "successor-sha256.txt"
    manifest_bytes = read_bounded_regular_snapshot(
        manifest_path,
        MAX_HISTORICAL_MANIFEST_BYTES,
        "Story 4.15 historical successor manifest",
    )
    require(
        sha256_bytes(manifest_bytes) == SDK_SUCCESSOR_MANIFEST_SHA256,
        "Story 4.15 historical successor manifest identity drift",
    )
    require(
        sha256_git_file(LEGACY_SUCCESSOR_SNAPSHOT_COMMIT, f"{SUCCESSOR_DIRECTORY}/successor-sha256.txt")
        == SDK_SUCCESSOR_MANIFEST_SHA256,
        "Story 4.15 historical successor Git manifest identity drift",
    )
    manifest_text = decode_utf8(manifest_bytes, "Story 4.15 historical successor manifest")
    scan_support_safe_text(manifest_text, "successor-sha256.txt")
    manifest: dict[str, str] = {}
    lines = manifest_text.splitlines()
    require(lines == sorted(lines, key=lambda line: line.split("  ", 1)[-1]), "Story 4.15 successor manifest is not path-sorted")
    for line in lines:
        parts = line.split("  ", 1)
        require(len(parts) == 2 and SHA256_RE.fullmatch(parts[0]) is not None, "Malformed Story 4.15 successor manifest line")
        digest, relative = parts
        path = Path(relative)
        require(
            relative not in manifest
            and not path.is_absolute()
            and ".." not in path.parts
            and path.as_posix() == relative,
            "Unsafe or duplicate Story 4.15 successor manifest path",
        )
        manifest[relative] = digest
    require(set(manifest) == SUCCESSOR_FILES, "Story 4.15 successor manifest file set drift")
    for relative, expected in manifest.items():
        artifact = SUCCESSOR / relative
        artifact_bytes = read_bounded_regular_snapshot(
            artifact,
            MAX_V2_ARTIFACT_BYTES,
            f"Story 4.15 historical successor artifact {relative}",
        )
        require(
            sha256_bytes(artifact_bytes) == expected,
            f"Story 4.15 successor checksum mismatch: {relative}",
        )
        scan_support_safe_text(
            decode_utf8(artifact_bytes, f"Story 4.15 historical successor artifact {relative}"),
            relative,
        )
    return manifest


def validate_successor_review_subject(subject: Any, identity: dict[str, Any]) -> str:
    require(isinstance(subject, dict), "Story 4.15 successor review subject must be an object")
    require(
        set(subject)
        == {
            "schema",
            "reviewedOn",
            "reason",
            "priorSeal",
            "sourceIdentity",
            "validation",
            "requiredReviews",
            "authority",
        },
        "Story 4.15 successor review subject field set drift",
    )
    require(
        subject.get("schema") == "hexalith.eventstore.story-4-15-successor-review-subject/v1",
        "Story 4.15 successor review subject schema drift",
    )
    require(subject.get("reviewedOn") == "2026-08-29", "Story 4.15 successor review subject date drift")
    require(subject.get("reason") == identity.get("reason"), "Story 4.15 successor review subject reason drift")
    require(
        subject.get("priorSeal")
        == {
            "packetPath": "_bmad-output/implementation-artifacts/4-8-eventstore-oq8-platform-evidence.yaml",
            "packetSha256": PRIOR_PACKET_SHA256,
            "closureDirectory": CLOSURE_DIRECTORY,
            "closureManifestSha256": PRIOR_CLOSURE_MANIFEST_SHA256,
        },
        "Story 4.15 successor prior-seal binding drift",
    )
    require(
        subject.get("sourceIdentity")
        == {
            "path": "source-artifact-identity.json",
            "sha256": sha256_file(SUCCESSOR / "source-artifact-identity.json"),
            "reviewedBaseCommit": SUCCESSOR_REVIEW_BASE,
            "boundPathCount": len(SUCCESSOR_SOURCE_PATHS),
        },
        "Story 4.15 successor review source identity drift",
    )
    require(subject.get("validation") == identity.get("validation"), "Story 4.15 successor reviewed validation drift")
    require(
        subject.get("requiredReviews")
        == [
            {
                "role": role,
                "reviewer": REVIEW_ROSTER[role],
                "scope": SUCCESSOR_REVIEW_SCOPES[role],
                "status": "required",
            }
            for role in ("architecture", "security", "test")
        ],
        "Story 4.15 successor required review roster or scope drift",
    )
    validate_authority(subject.get("authority"))
    return sha256_file(SUCCESSOR / "review-subject.json")


def validate_successor_reviews(subject_sha256: str) -> dict[str, str]:
    receipts: dict[str, str] = {}
    for role, reviewer in REVIEW_ROSTER.items():
        path = SUCCESSOR / "reviews" / f"{role}.json"
        document = load_candidate_json(path)
        require(isinstance(document, dict), f"Story 4.15 successor {role} review must be an object")
        require(
            set(document)
            == {
                "schema",
                "role",
                "reviewer",
                "reviewedOn",
                "decision",
                "subjectSha256",
                "acceptedScope",
                "findings",
                "authority",
            },
            f"Story 4.15 successor {role} review field set drift",
        )
        require(
            document.get("schema") == "hexalith.eventstore.story-4-15-successor-review-receipt/v1",
            f"Story 4.15 successor {role} review schema drift",
        )
        require(document.get("role") == role, f"Story 4.15 successor {role} review role drift")
        require(document.get("reviewer") == reviewer, f"Story 4.15 successor {role} reviewer identity drift")
        require(document.get("reviewedOn") == "2026-08-29", f"Story 4.15 successor {role} review date drift")
        require(document.get("decision") == "approved", f"Story 4.15 successor {role} review is not approved")
        require(document.get("subjectSha256") == subject_sha256, f"Story 4.15 successor {role} review subject drift")
        require(document.get("acceptedScope") == SUCCESSOR_REVIEW_SCOPES[role], f"Story 4.15 successor {role} review scope drift")
        require(document.get("findings") == SUCCESSOR_REVIEW_FINDINGS[role], f"Story 4.15 successor {role} review findings drift")
        validate_authority(document.get("authority"))
        receipts[role] = sha256_file(path)
    return receipts


def validate_successor_handoff(
    document: Any,
    subject_sha256: str,
    identity_sha256: str,
    receipts: dict[str, str],
) -> str:
    require(isinstance(document, dict), "Story 4.15 successor handoff must be an object")
    require(
        set(document)
        == {
            "schema",
            "story",
            "selectedSuccessorDirectory",
            "reviewedBaseCommit",
            "reviewSubjectSha256",
            "sourceIdentitySha256",
            "reviewReceipts",
            "consumerInstructions",
            "priorSealRetained",
            "authority",
        },
        "Story 4.15 successor handoff field set drift",
    )
    require(
        document.get("schema") == "hexalith.eventstore.story-4-15-successor-source-only-handoff/v1",
        "Story 4.15 successor handoff schema drift",
    )
    require(document.get("story") == "4.15", "Story 4.15 successor handoff story drift")
    require(document.get("selectedSuccessorDirectory") == SUCCESSOR_DIRECTORY, "Story 4.15 successor handoff directory drift")
    require(document.get("reviewedBaseCommit") == SUCCESSOR_REVIEW_BASE, "Story 4.15 successor handoff review base drift")
    require(document.get("reviewSubjectSha256") == subject_sha256, "Story 4.15 successor handoff subject drift")
    require(document.get("sourceIdentitySha256") == identity_sha256, "Story 4.15 successor handoff source identity drift")
    require(document.get("reviewReceipts") == receipts, "Story 4.15 successor handoff receipt set drift")
    require(
        document.get("consumerInstructions")
        == {
            "mode": "source-only",
            "verifyCommand": ".oq8-python/bin/python tools/validate-oq8-platform-evidence.py",
            "sourcePathRule": "Use only current files that match the selected successor source identity after the validator passes.",
            "priorSealRule": "Retain the original Story 4.15 packet, closure manifest, and every sealed artifact byte unchanged.",
        },
        "Story 4.15 successor consumer instructions drift",
    )
    require(document.get("priorSealRetained") is True, "Story 4.15 prior seal retention is not recorded")
    validate_authority(document.get("authority"))
    return sha256_file(SUCCESSOR / "source-only-handoff.json")


def load_successor_selector() -> dict[str, Any]:
    require(
        SUCCESSOR_SELECTOR.is_file() and not SUCCESSOR_SELECTOR.is_symlink(),
        "Story 4.15 successor selector must be a regular non-symlink file",
    )
    selector = load_candidate_json_bytes(
        read_bounded_regular_snapshot(SUCCESSOR_SELECTOR, MAX_V2_ARTIFACT_BYTES, "Story 4.15 successor selector"),
        "Story 4.15 successor selector",
    )
    require(isinstance(selector, dict), "Story 4.15 successor selector must be an object")
    require(
        set(selector) == {"schema", "selectedOn", "reason", "historical", "successor", "authority"},
        "Story 4.15 successor selector field set drift",
    )
    schema = selector.get("schema")
    require(
        schema in {
            "hexalith.eventstore.story-4-15-successor-selection/v3",
            "hexalith.eventstore.story-4-15-successor-selection/v4",
        },
        "Story 4.15 successor selector schema drift",
    )
    if schema.endswith("/v3"):
        require(selector.get("selectedOn") == V4_SELECTION_DATE, "Story 4.15 successor selection date drift")
        require(selector.get("reason") == V4_SELECTION_REASON, "Story 4.15 successor selection reason drift")
    else:
        require(
            selector.get("reason") == V5_SELECTION_REASON
            and selector.get("successor", {}).get("directory") == V5_SUCCESSOR_DIRECTORY,
            "Story 4.15 v5 successor selection reason or directory drift",
        )
        selected_on = selector.get("selectedOn")
        require(
            isinstance(selected_on, str)
            and re.fullmatch(r"\d{4}-\d{2}-\d{2}", selected_on) is not None,
            "Story 4.15 v5 successor selection date drift",
        )
        require(
            datetime.strptime(selected_on, "%Y-%m-%d").date() <= datetime.now(timezone.utc).date(),
            "Story 4.15 v5 successor selection date is in the future",
        )
    return selector


def validate_successor_selector_historical(
    selector: dict[str, Any],
    prior_manifest: dict[str, str],
    historical_successor_manifest: dict[str, str],
) -> None:
    expected_historical = {
            "v1Closure": {
                "packetPath": "_bmad-output/implementation-artifacts/4-8-eventstore-oq8-platform-evidence.yaml",
                "packetSha256": PRIOR_PACKET_SHA256,
                "directory": CLOSURE_DIRECTORY,
                "manifestSha256": PRIOR_CLOSURE_MANIFEST_SHA256,
                "files": prior_manifest,
            },
            "sdkSuccessor": {
                "directory": SUCCESSOR_DIRECTORY,
                "manifestSha256": SDK_SUCCESSOR_MANIFEST_SHA256,
                "files": historical_successor_manifest,
            },
            "v2Successor": {
                "directory": V2_SUCCESSOR_DIRECTORY,
                "manifestSha256": V2_CLOSURE_MANIFEST_SHA256,
            },
            "v3Successor": {
                "directory": V3_SUCCESSOR_DIRECTORY,
                "completedClosureCommit": COMPLETED_V3_CLOSURE_COMMIT,
                "manifestSha256": V3_CLOSURE_MANIFEST_SHA256,
                "sourceIdentitySha256": V3_SOURCE_IDENTITY_SHA256,
                "reviewSubjectSha256": V3_REVIEW_SUBJECT_SHA256,
                "handoffSha256": V3_HANDOFF_SHA256,
            },
        }
    if selector.get("schema") == "hexalith.eventstore.story-4-15-successor-selection/v4":
        archived_selector = load_json_bytes(
            git_file("30b279bd841c671932b51fad6bcf78b147079d21", SUCCESSOR_SELECTOR_PATH),
            "Story 4.15 archived v4 selector",
        )
        require(
            sha256_bytes(git_file("30b279bd841c671932b51fad6bcf78b147079d21", SUCCESSOR_SELECTOR_PATH))
            == "b61c8112a281b6dc74511b8392684c8e62c67b285580f82ce331b6bcb189fd8c",
            "Story 4.15 archived v4 selector identity drift",
        )
        expected_historical["v4Successor"] = archived_selector["successor"]
    require(selector.get("historical") == expected_historical, "Story 4.15 successor historical selection drift")
    require(sha256_file(PACKET) == PRIOR_PACKET_SHA256, "Story 4.15 prior packet byte identity drift")
    require(
        sha256_file(CLOSURE / "closure-sha256.txt") == PRIOR_CLOSURE_MANIFEST_SHA256,
        "Story 4.15 prior closure manifest byte identity drift",
    )
    validate_authority(selector.get("authority"))


def validate_successor_selector(
    selector: dict[str, Any],
    v4_manifest: dict[str, str],
    v4_manifest_sha256: str,
    subject_sha256: str,
    identity_sha256: str,
    handoff_sha256: str,
) -> None:
    require(
        selector.get("successor")
        == {
            "directory": V4_SUCCESSOR_DIRECTORY,
            "manifestSha256": v4_manifest_sha256,
            "files": v4_manifest,
            "sourceIdentitySha256": identity_sha256,
            "reviewSubjectSha256": subject_sha256,
            "handoffSha256": handoff_sha256,
        },
        "Story 4.15 successor selection drift",
    )


def validate_successor_closure() -> dict[str, str]:
    identity = validate_successor_source_identity()
    identity_sha256 = sha256_file(SUCCESSOR / "source-artifact-identity.json")
    successor_manifest = validate_successor_manifest()
    subject_sha256 = validate_successor_review_subject(
        load_candidate_json(SUCCESSOR / "review-subject.json"),
        identity,
    )
    receipts = validate_successor_reviews(subject_sha256)
    handoff_sha256 = validate_successor_handoff(
        load_candidate_json(SUCCESSOR / "source-only-handoff.json"),
        subject_sha256,
        identity_sha256,
        receipts,
    )
    return successor_manifest


def validate_source_state(document: dict[str, Any], identity: dict[str, Any]) -> None:
    validate_successor_source_identity()
    require(isinstance(document, dict), "Captured source-state must be an object")
    require(isinstance(identity, dict), "Source identity must be an object")
    require(
        set(document) == {
            "schemaVersion",
            "baselineCommit",
            "dirtySourceCaptured",
            "candidateDiffAlgorithm",
            "candidateDiffSha256",
            "candidateFiles",
            "sourceInputs",
        },
        "Captured source-state field set drift",
    )
    require(document.get("baselineCommit") == BASELINE, "Baseline commit drift")
    candidate_files = document.get("candidateFiles", {})
    source_inputs = document.get("sourceInputs", {})
    require(isinstance(candidate_files, dict) and candidate_files, "Candidate file identities missing")
    require(isinstance(source_inputs, dict) and source_inputs, "Source input identities missing")
    require(set(candidate_files) == EXPECTED_CANDIDATE_FILES, "Pinned candidate-file identity set drift")
    require(set(source_inputs) == EXPECTED_SOURCE_INPUTS, "Pinned source-input identity set drift")
    for collection_name, collection in (("candidateFiles", candidate_files), ("sourceInputs", source_inputs)):
        for relative, expected in collection.items():
            require(isinstance(relative, str) and not Path(relative).is_absolute() and ".." not in Path(relative).parts, f"Unsafe {collection_name} path")
            require_sha256(expected, f"{collection_name}:{relative}")
    lines = [f"{relative}:{candidate_files[relative]}" for relative in sorted(candidate_files)]
    candidate_digest = hashlib.sha256(("\n".join(lines) + "\n").encode("utf-8")).hexdigest()
    require(document.get("candidateDiffSha256") == candidate_digest, "Candidate diff identity drift")
    require(
        document.get("candidateDiffAlgorithm")
        == "sha256(sorted complete changed source/config/tool relative-path:file-sha256 lines)",
        "Candidate diff algorithm drift",
    )
    require(document.get("dirtySourceCaptured") is True, "Dirty-source condition was not recorded")

    landed = identity.get("landedSource", {})
    current = identity.get("currentVerification", {})
    capture_paths = identity.get("captureWorktreePaths", {})
    landed_overrides = identity.get("landedGitByteOverrides", {})
    expected_paths = candidate_files | source_inputs
    require(identity.get("schema") == "hexalith.eventstore.story-4-15-source-artifact-identity/v1", "Source identity schema drift")
    require(
        set(identity) == {
            "schema",
            "repository",
            "landedSource",
            "capturedPathSets",
            "currentVerification",
            "captureWorktreePaths",
            "landedGitByteOverrides",
            "capture",
            "runtimeArtifacts",
        },
        "Source identity field set drift",
    )
    require(identity.get("repository") == "Hexalith/Hexalith.EventStore", "Source repository identity drift")
    require(
        set(landed) == {"commit", "tree", "pathCount", "pathSet"},
        "Landed source field set drift",
    )
    require(landed.get("commit") == LANDED_SOURCE, "Landed source commit drift")
    require(landed.get("tree") == LANDED_TREE, "Landed source tree declaration drift")
    require(landed.get("pathCount") == len(expected_paths) == 26, "Landed source path count drift")
    require(landed.get("pathSet") == "union of Story 4.14 candidateFiles and sourceInputs", "Landed source path-set rule drift")
    require(
        identity.get("capturedPathSets") == {
            "candidateFiles": sorted(EXPECTED_CANDIDATE_FILES),
            "sourceInputs": sorted(EXPECTED_SOURCE_INPUTS),
        },
        "Captured candidate/source path sets drift",
    )
    require(capture_paths == expected_paths, "Capture worktree path/hash set drift")
    require(
        run_git("rev-parse", f"{LANDED_SOURCE}^{{tree}}").decode("ascii").strip() == LANDED_TREE,
        "Landed source Git tree drift",
    )

    evolved = current.get("closureEvolvedPaths", {})
    require(
        evolved == {
            ".github/workflows/integration.yml":
                "The workflow orchestrates evidence capture and may evolve independently; its landed bytes remain historical evidence rather than current capability source.",
            "tools/validate-oq8-platform-evidence.py":
                "Story 4.15 evolves the closure validator; its new bytes are bound by validator-sha256.txt rather than treated as unchanged Story 4.14 capability source.",
        },
        "Closure-evolved path declaration drift",
    )
    require(
        landed_overrides == {
            ".github/workflows/integration.yml": {
                "captureWorktreeSha256": "afb28e703e9b9d51b5144e10c0368cf6ac94ab01cf5e456ef2090a6d444e3205",
                "landedGitSha256": "343163fd164bb49252ad2ec67c7fbc90aa2f3aaecafa4d4d51640ccc39e7b777",
                "reason": "Story 4.14 capture hashed the original integration workflow; landed commit 5e8f175b retains later CI history-fetch and Dapr runtime-pin wiring as historical evidence for the evolved orchestration path.",
            },
            "src/Hexalith.EventStore/Program.cs": {
                "captureWorktreeSha256": "245f79cc04998118da9caec70cdf290d67fb23e71a91100af46c4a019af5be7f",
                "landedGitSha256": "7203089f0035e3a45cf7ccf6c6fffdc120e4b2dc2d9fd53b8031ed87e7ad83e9",
                "reason": "The capture hashed CRLF working-tree bytes while the landed Git blob stores LF bytes under tracked text normalization.",
            },
            "tests/Hexalith.EventStore.Server.Tests/Actors/PublicationRecoveryActivationTests.cs": {
                "captureWorktreeSha256": "7aff4137378e9f2529b1770dd81c6638e8159277b4e92a5653ffc61a65ad2eb4",
                "landedGitSha256": "bfc4c85147df8d468d8ed33a9e00c4c372ce18d2512f07913095190527b15b6d",
                "reason": "Committed publication-recovery tests advanced before landed commit 5e8f175b after Story 4.14 capture; the override bridges capture worktree bytes to the intentional landed Git capability source.",
            },
            "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/DaprTestContainerFixture.cs": {
                "captureWorktreeSha256": "0da109af79cb0ded1c9e7377c4140a561ef682f1cf23b7c0fbb6284b7401c216",
                "landedGitSha256": "28a89849a864014f4e18ab0bd791c4e905cc662799c8a58fce7e9627762dde9a",
                "reason": "Story 4.5 fixture hardening advanced at landed commit 5e8f175b after Story 4.14 capture; the override bridges capture worktree bytes to the intentional landed Git capability source.",
            },
            "tools/validate-oq8-platform-evidence.py": {
                "captureWorktreeSha256": "0e9a352e7757f452dbc1f41dbd7036d76088f56be732a03c9b48ba4d6ab1c8b1",
                "landedGitSha256": "585e4d8634f6862a99a3431da37634a23d3aac5886a83b76f0bc4a7f0d309726",
                "reason": "Story 4.14 capture hashed the original validator; landed commit 5e8f175b retains the later closure-validator evolution, while current validator bytes remain bound by validator-sha256.txt.",
            },
        },
        "Landed Git-byte override declaration drift",
    )
    capability_paths = set(capture_paths) - set(evolved)
    require(capability_paths == EXPECTED_CURRENT_BOUND_PATHS, "Current bound source path set drift")
    require(
        set(current) == {
            "source",
            "rule",
            "pathCount",
            "boundPaths",
            "headMustDescendFromLandedSource",
            "sourceAuthorityBoundary",
            "closureEvolvedPaths",
            "unboundLaterPathsAllowed",
        },
        "Current source verification field set drift",
    )
    require(current.get("source") == "current HEAD Git tree", "Current source proof mode drift")
    require(
        current.get("rule")
        == "every non-evolved capability path must exist in HEAD, remain byte-equivalent to the landed source, and have no index or semantic working-tree change",
        "Current source proof rule drift",
    )
    require(current.get("pathCount") == len(capability_paths) == 24, "Current capability path count drift")
    require(current.get("boundPaths") == sorted(EXPECTED_CURRENT_BOUND_PATHS), "Current bound source path declaration drift")
    require(current.get("headMustDescendFromLandedSource") is True, "Current source ancestry requirement drift")
    require(
        current.get("sourceAuthorityBoundary")
        == "The exact landed Git tree is the complete source-only authority; current verification is limited to the 24 non-evolved captured capability paths and does not claim that every production path remains unchanged.",
        "Source-only authority boundary drift",
    )
    require(current.get("unboundLaterPathsAllowed") is True, "Later unbound paths are not explicitly allowed")
    run_git("merge-base", "--is-ancestor", LANDED_SOURCE, COMPLETED_V1_CLOSURE_COMMIT)

    retained_paths = capability_paths - REPLACED_PRIOR_BOUND_PATHS
    require(
        REPLACED_PRIOR_BOUND_PATHS <= capability_paths,
        "Story 4.15 successor replaced paths are not prior current-bound paths",
    )
    for relative, capture_expected in capture_paths.items():
        override = landed_overrides.get(relative, {})
        landed_expected = override.get("landedGitSha256", capture_expected)
        if override:
            require(override.get("captureWorktreeSha256") == capture_expected, f"Capture/Git override drift: {relative}")
        require_sha256(landed_expected, f"landedGitPaths:{relative}")
        require(sha256_bytes(git_file(LANDED_SOURCE, relative)) == landed_expected, f"Landed source identity drift: {relative}")
        if relative in retained_paths:
            require(
                sha256_bytes(git_file(COMPLETED_V1_CLOSURE_COMMIT, relative)) == landed_expected,
                f"Historical v1 source identity drift: {relative}",
            )

    capture = identity.get("capture", {})
    require_exact_fields(capture, {"packetV1Path", "packetV1Sha256", "evidenceDirectory", "manifestSha256", "artifactCount"}, "Source identity capture")
    require(capture.get("packetV1Path") == "capture-packet-v1.json", "Capture packet snapshot path drift")
    require_sha256(capture.get("packetV1Sha256"), "Capture packet snapshot identity")
    require(capture.get("packetV1Sha256") == sha256_file(CLOSURE / "capture-packet-v1.json"), "Capture packet snapshot drift")
    require(capture.get("evidenceDirectory") == EVIDENCE_DIRECTORY, "Capture evidence directory identity drift")
    require(capture.get("manifestSha256") == sha256_file(EVIDENCE / "evidence-sha256.txt"), "Capture manifest identity drift")
    require(capture.get("artifactCount") == len(REQUIRED_FILES), "Capture artifact count drift")


def validate_closure_manifest() -> dict[str, str]:
    require_no_symlink_components(CLOSURE, "Story 4.15 v1 closure directory")
    require(CLOSURE.is_dir(), "Story 4.15 closure directory is missing")
    manifest_path = CLOSURE / "closure-sha256.txt"
    manifest_text = read_bounded_text_snapshot(
        manifest_path,
        MAX_HISTORICAL_MANIFEST_BYTES,
        "Story 4.15 v1 closure manifest",
    )
    scan_support_safe_text(manifest_text, "closure-sha256.txt")
    manifest: dict[str, str] = {}
    lines = manifest_text.splitlines()
    require(lines == sorted(lines, key=lambda line: line.split("  ", 1)[-1]), "Closure manifest is not path-sorted")
    for line in lines:
        parts = line.split("  ", 1)
        require(len(parts) == 2 and SHA256_RE.fullmatch(parts[0]) is not None, "Malformed closure manifest line")
        digest, relative = parts
        path = Path(relative)
        require(
            relative not in manifest
            and not path.is_absolute()
            and ".." not in path.parts
            and path.as_posix() == relative,
            "Unsafe or duplicate closure manifest path",
        )
        manifest[relative] = digest
    require(set(manifest) == CLOSURE_FILES, "Closure manifest file set drift")
    require(
        relative_tree_entries(CLOSURE) == CLOSURE_FILES | {"closure-sha256.txt", "reviews"},
        "Closure directory file set drift",
    )
    for relative, expected in manifest.items():
        path = CLOSURE / relative
        artifact_bytes = read_bounded_regular_snapshot(
            path,
            MAX_CANDIDATE_JSON_BYTES,
            f"Story 4.15 v1 closure artifact {relative}",
        )
        require(sha256_bytes(artifact_bytes) == expected, f"Closure checksum mismatch: {relative}")
        scan_support_safe_text(
            decode_utf8(artifact_bytes, f"Story 4.15 v1 closure artifact {relative}"),
            relative,
        )

    validate_validator_identity()
    return manifest


def validate_validator_identity() -> str:
    validator_record = read_bounded_text_snapshot(
        CLOSURE / "validator-sha256.txt",
        MAX_CANDIDATE_JSON_BYTES,
        "Story 4.15 v1 validator identity",
    ).splitlines()
    require(len(validator_record) == 1, "Closure validator identity record is malformed")
    validator_parts = validator_record[0].split("  ", 1)
    require(
        len(validator_parts) == 2
        and validator_parts[1] == "tools/validate-oq8-platform-evidence.py"
        and validator_parts[0] == PRIOR_VALIDATOR_SHA256,
        "Closure validator identity drift",
    )
    require(
        sha256_git_file(COMPLETED_V1_CLOSURE_COMMIT, validator_parts[1]) == PRIOR_VALIDATOR_SHA256,
        "Closure validator historical identity drift",
    )
    return validator_parts[0]


def validate_crosswalk(document: dict[str, Any]) -> None:
    require(isinstance(document, dict), "Closure crosswalk must be an object")
    require(
        set(document) == {"schema", "story", "design", "invariants", "evidenceBindings", "storyEvidence", "verification"},
        "Closure crosswalk field set drift",
    )
    require(document.get("schema") == "hexalith.eventstore.story-4-15-closure-crosswalk/v1", "Closure crosswalk schema drift")
    require(document.get("story") == "4.15", "Closure crosswalk story drift")
    require(
        document.get("design") == {
            "version": DESIGN_VERSION,
            "sha256": DESIGN_SHA256,
            "bytesAvailableInEventStore": False,
        },
        "Closure crosswalk design reference drift",
    )
    invariants = document.get("invariants", [])
    require(invariants == EXPECTED_CROSSWALK_INVARIANTS, "Closure invariant-to-story/evidence mapping drift")
    referenced_evidence = {relative for invariant in invariants for relative in invariant["evidence"]}
    require(referenced_evidence == set(EXPECTED_CROSSWALK_EVIDENCE_HASHES), "Closure invariant evidence path set drift")
    evidence_bindings = document.get("evidenceBindings")
    require(evidence_bindings == EXPECTED_CROSSWALK_EVIDENCE_HASHES, "Closure evidence binding set or identity drift")
    for relative, expected in EXPECTED_CROSSWALK_EVIDENCE_HASHES.items():
        path = Path(relative)
        require(not path.is_absolute() and ".." not in path.parts and path.as_posix() == relative, "Unsafe crosswalk evidence path")
        require(sha256_file(ROOT / path) == expected, f"Crosswalk evidence body drift: {relative}")
    require(
        document.get("storyEvidence") == {story: "approved" for story in ("4.9", "4.10", "4.11", "4.12", "4.13", "4.14")},
        "Closure story result crosswalk drift",
    )
    verification = document.get("verification", {})
    expected_verification = {
        "commandsFile": f"{EVIDENCE_DIRECTORY}/commands.json",
        "commandsSha256": "29b488e3779192191340f868a4c3f5be3622af51dfa25a26663bb5f49727bd9c",
        "recordedCommands": 8,
        "successfulCommands": 8,
        "focusedProductionCases": 1,
        "focusedProductionPassed": 1,
        "deterministicMethods": 21,
        "deterministicCases": 33,
        "deterministicPassed": 33,
        "focusedSkipped": 0,
        "deterministicSupportSkipped": 0,
        "broadLanePreExistingSkipped": 25,
    }
    require(isinstance(verification, dict) and set(verification) == set(expected_verification), "Closure verification field set drift")
    for field, expected in expected_verification.items():
        if type(expected) is int:
            require_exact_integer(verification.get(field), expected, f"Closure verification {field}")
        else:
            require(verification.get(field) == expected, f"Closure verification {field} drift")
    require(verification["commandsSha256"] == sha256_file(ROOT / verification["commandsFile"]), "Closure commands identity drift")


def validate_authority(authority: Any) -> None:
    require(isinstance(authority, dict), "Closure authority record is missing")
    require(authority.get("eventStorePlatformComplete") is True, "EventStore platform completion is not recorded")
    require(authority.get("handoffMode") == "source-only", "OQ8 handoff is not source-only")
    require(set(authority) == {"eventStorePlatformComplete", "handoffMode", *EXTERNAL_AUTHORITY_FIELDS}, "Closure authority field set drift")
    for field in EXTERNAL_AUTHORITY_FIELDS:
        require(authority.get(field) is False, f"External authority overstated: {field}")


def validate_successor_timestamp(value: Any, field: str, now: datetime, version: str) -> datetime:
    require(
        isinstance(value, str)
        and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z", value) is not None,
        f"Story 4.15 {version} {field} timestamp is not an exact UTC second",
    )
    try:
        parsed = datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError:
        fail(f"Story 4.15 {version} {field} timestamp is not an exact UTC second")
    require(parsed <= now, f"Story 4.15 {version} {field} timestamp is later than current UTC")
    return parsed


def validate_v2_timestamp(value: Any, field: str, now: datetime) -> datetime:
    return validate_successor_timestamp(value, field, now, "v2")


def expected_v2_predecessor() -> dict[str, str]:
    return {
        "landedSourceCommit": LANDED_SOURCE,
        "reviewSubjectSha256": V1_REVIEW_SUBJECT_SHA256,
        "completedV1ClosureSnapshotCommit": COMPLETED_V1_CLOSURE_COMMIT,
    }


def expected_v2_reviewed_image() -> dict[str, Any]:
    return {
        "upstreamTag": POSTGRES_TAG,
        "reference": POSTGRES_IMAGE,
        "kind": "multi-platform-index",
        "amd64ChildManifest": POSTGRES_AMD64_CHILD,
        "amd64ChildIsAuthority": False,
    }


def v2_snapshot_key(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        fail("Story 4.15 v2 snapshot path is outside the repository root")


def v2_snapshot(snapshots: dict[str, bytes], path: Path) -> bytes:
    key = v2_snapshot_key(path)
    require(key in snapshots, f"Story 4.15 v2 snapshot is missing: {key}")
    return snapshots[key]


def v2_snapshot_text(snapshots: dict[str, bytes], path: Path) -> str:
    try:
        return v2_snapshot(snapshots, path).decode("utf-8")
    except UnicodeError:
        fail(f"Story 4.15 v2 snapshot is not UTF-8: {v2_snapshot_key(path)}")


def v2_snapshot_json(snapshots: dict[str, bytes], relative: str) -> Any:
    path = V2_SUCCESSOR / relative
    return load_candidate_json_bytes(v2_snapshot(snapshots, path), relative)


def capture_v2_snapshots() -> dict[str, bytes]:
    require(
        V2_SUCCESSOR.exists(),
        "Story 4.15 v2 successor directory is missing or symlinked",
    )
    require_no_symlink_components(V2_SUCCESSOR, "Story 4.15 v2 successor directory")
    require(V2_SUCCESSOR.is_dir(), "Story 4.15 v2 successor directory is missing or symlinked")
    expected_entries = V2_SUCCESSOR_FILES | {"closure-sha256.txt", "reviews"}
    actual_entries = relative_tree_entries(V2_SUCCESSOR)
    require(actual_entries == expected_entries, "Story 4.15 v2 successor file set drift")
    for relative in actual_entries:
        path = V2_SUCCESSOR / relative
        require_no_symlink_components(path, f"Story 4.15 v2 artifact {relative}")
        if relative == "reviews":
            require(path.is_dir(), "Story 4.15 v2 reviews path is not a directory")
        else:
            require(path.is_file(), f"Story 4.15 v2 artifact missing: {relative}")

    snapshots: dict[str, bytes] = {}
    for relative in V2_SUCCESSOR_FILES | {"closure-sha256.txt"}:
        path = V2_SUCCESSOR / relative
        snapshots[v2_snapshot_key(path)] = read_bounded_regular_snapshot(
            path,
            MAX_V2_ARTIFACT_BYTES,
            f"Story 4.15 v2 artifact {relative}",
        )
    return snapshots


def validate_v2_manifest(snapshots: dict[str, bytes]) -> dict[str, str]:

    manifest_path = V2_SUCCESSOR / "closure-sha256.txt"
    lines = v2_snapshot_text(snapshots, manifest_path).splitlines()
    require(lines == sorted(lines, key=lambda line: line.split("  ", 1)[-1]), "Story 4.15 v2 closure manifest is not path-sorted")
    manifest: dict[str, str] = {}
    for line in lines:
        parts = line.split("  ", 1)
        require(len(parts) == 2 and SHA256_RE.fullmatch(parts[0]) is not None, "Malformed Story 4.15 v2 closure manifest line")
        digest, relative = parts
        path = Path(relative)
        require(
            relative not in manifest
            and not path.is_absolute()
            and ".." not in path.parts
            and path.as_posix() == relative,
            "Unsafe or duplicate Story 4.15 v2 closure manifest path",
        )
        manifest[relative] = digest
    require(set(manifest) == V2_SUCCESSOR_FILES, "Story 4.15 v2 closure manifest file set drift")
    for relative, expected in manifest.items():
        artifact = V2_SUCCESSOR / relative
        artifact_bytes = v2_snapshot(snapshots, artifact)
        require(sha256_bytes(artifact_bytes) == expected, f"Story 4.15 v2 checksum mismatch: {relative}")
        try:
            artifact_text = artifact_bytes.decode("utf-8")
        except UnicodeError:
            fail(f"Story 4.15 v2 artifact is not UTF-8: {relative}")
        scan_support_safe_text(artifact_text, relative)
    return manifest


def extract_v2_workflow_image(source: str) -> str:
    steps = list(re.finditer(
        r"(?ms)^(?P<indent>[ \t]*)-[ \t]+name:[ \t]*(?:\"|')?Pull PostgreSQL container image(?:\"|')?[ \t]*(?:#[^\r\n]*)?\r?\n(?P<body>.*?)(?=^(?P=indent)-[ \t]+|\Z)",
        source,
    ))
    require(len(steps) == 1, f"Story 4.15 v2 workflow PostgreSQL authority declaration count drift: found {len(steps)}")
    pulls = list(re.finditer(
        r"(?m)^[ \t]*command:[ \t]*(?P<quote>[\"']?)docker[ \t]+pull[ \t]+(?P<image>[^\s\"'#]+)(?P=quote)[ \t]*(?:#[^\r\n]*)?\r?$",
        steps[0].group("body"),
    ))
    require(len(pulls) == 1, f"Story 4.15 v2 workflow PostgreSQL pull declaration count drift: found {len(pulls)}")
    return pulls[0].group("image")


def extract_v2_fixture_image(source: str) -> str:
    declarations = list(re.finditer(
        r'(?m)^[ \t]*private[ \t]+const[ \t]+string[ \t]+PostgresImage[ \t]*=[ \t]*"(?P<image>[^"\r\n]+)"[ \t]*;[ \t]*(?://[^\r\n]*)?\r?$',
        source,
    ))
    require(len(declarations) == 1, f"Story 4.15 v2 fixture PostgreSQL authority declaration count drift: found {len(declarations)}")
    return declarations[0].group("image")


def validate_v2_source_identity(snapshots: dict[str, bytes]) -> dict[str, Any]:
    path = V2_SUCCESSOR / "source-artifact-identity.json"
    identity = v2_snapshot_json(snapshots, "source-artifact-identity.json")
    require(isinstance(identity, dict), "Story 4.15 v2 source identity must be an object")
    require(
        set(identity)
        == {
            "schema",
            "reviewedOn",
            "repository",
            "predecessor",
            "reviewedImage",
            "sourceTransitions",
            "gateInputs",
            "bindingRule",
        },
        "Story 4.15 v2 source identity field set drift",
    )
    require(
        identity.get("schema") == "hexalith.eventstore.story-4-15-successor-source-identity/v2",
        "Story 4.15 v2 source identity schema drift",
    )
    require(identity.get("reviewedOn") == "2026-08-30", "Story 4.15 v2 source identity review date drift")
    require(identity.get("repository") == "Hexalith/Hexalith.EventStore", "Story 4.15 v2 source identity repository drift")
    require(identity.get("predecessor") == expected_v2_predecessor(), "Story 4.15 v2 predecessor link drift")
    reviewed_image = identity.get("reviewedImage")
    require(reviewed_image == expected_v2_reviewed_image(), "Story 4.15 v2 reviewed index drift")
    require(
        isinstance(reviewed_image, dict) and reviewed_image.get("amd64ChildIsAuthority") is False,
        "Story 4.15 v2 reviewed index authority type drift",
    )
    require(
        identity.get("bindingRule")
        == "V1 source and validator bindings resolve only against immutable historical Git snapshots; v2 source transitions and gate inputs resolve only against current regular non-symlink candidate files.",
        "Story 4.15 v2 source binding rule drift",
    )

    transitions = identity.get("sourceTransitions")
    require(isinstance(transitions, dict) and set(transitions) == set(V2_SOURCE_PATHS), "Story 4.15 v2 source transition path set drift")
    for relative, historical in V2_SOURCE_PATHS.items():
        transition = transitions.get(relative)
        require(
            isinstance(transition, dict)
            and set(transition)
            == {"predecessorSha256", "predecessorImage", "successorSha256", "successorImage"},
            f"Story 4.15 v2 source transition field set drift: {relative}",
        )
        require(
            transition.get("predecessorSha256") == historical["predecessorSha256"]
            and transition.get("predecessorImage") == historical["predecessorImage"],
            f"Story 4.15 v2 predecessor source identity drift: {relative}",
        )
        require(
            sha256_git_file(COMPLETED_V1_CLOSURE_COMMIT, relative) == historical["predecessorSha256"],
            f"Story 4.15 v1 snapshot source identity drift: {relative}",
        )
        successor_bytes = git_file(COMPLETED_V2_CLOSURE_COMMIT, relative)
        require(
            transition.get("successorSha256") == sha256_bytes(successor_bytes),
            f"Story 4.15 v2 historical source identity drift: {relative}",
        )
        require(
            transition.get("successorImage") == POSTGRES_IMAGE,
            f"Story 4.15 v2 current source image drift: {relative}",
        )
        try:
            successor_text = successor_bytes.decode("utf-8")
        except UnicodeError:
            fail(f"Story 4.15 v2 historical source is not UTF-8: {relative}")
        declared_image = (
            extract_v2_workflow_image(successor_text)
            if relative == ".github/workflows/integration.yml"
            else extract_v2_fixture_image(successor_text)
        )
        require(
            declared_image == POSTGRES_IMAGE,
            f"Story 4.15 v2 historical source PostgreSQL image drift: {relative}; found {declared_image}",
        )

    gate_inputs = identity.get("gateInputs")
    require(isinstance(gate_inputs, dict) and set(gate_inputs) == V2_GATE_INPUT_PATHS, "Story 4.15 v2 gate-input path set drift")
    for relative, expected in gate_inputs.items():
        require_sha256(expected, f"Story 4.15 v2 gate input:{relative}")
        require(
            sha256_git_file(COMPLETED_V2_CLOSURE_COMMIT, relative) == expected,
            f"Story 4.15 v2 historical gate-input identity drift: {relative}",
        )
    return identity


def validate_v2_limitations(snapshots: dict[str, bytes]) -> str:
    path = V2_SUCCESSOR / "limitations.json"
    document = v2_snapshot_json(snapshots, "limitations.json")
    require(
        document
        == {
            "schema": "hexalith.eventstore.story-4-15-successor-limitations/v2",
            "limitations": V2_LIMITATIONS,
        },
        "Story 4.15 v2 limitation text or order drift",
    )
    return sha256_bytes(v2_snapshot(snapshots, path))


def validate_v2_validator_identity(snapshots: dict[str, bytes]) -> str:
    path = V2_SUCCESSOR / "validator-sha256.txt"
    lines = v2_snapshot_text(snapshots, path).splitlines()
    require(len(lines) == 1, "Story 4.15 v2 validator identity record is malformed")
    parts = lines[0].split("  ", 1)
    require(
        len(parts) == 2
        and parts[1] == "tools/validate-oq8-platform-evidence.py"
        and SHA256_RE.fullmatch(parts[0]) is not None,
        "Story 4.15 v2 validator identity record is malformed",
    )
    require(
        parts[0] == sha256_git_file(COMPLETED_V2_CLOSURE_COMMIT, parts[1]),
        "Story 4.15 v2 historical validator identity drift",
    )
    return parts[0]


def validate_v2_passed_command(command: Any, expected: tuple[str, str, int], label: str) -> None:
    validate_successor_passed_command(command, expected, label, "v2")


def validate_successor_passed_command(
    command: Any,
    expected: tuple[str, str, int],
    label: str,
    version: str,
) -> None:
    name, invocation, expected_tests = expected
    require(
        isinstance(command, dict)
        and set(command) == {"name", "command", "exitCode", "result", "tests", "passed", "failed", "skipped"},
        f"Story 4.15 {version} {label} command field set drift: {name}",
    )
    require(command.get("name") == name, f"Story 4.15 {version} {label} command name drift: {name}")
    require(command.get("command") == invocation, f"Story 4.15 {version} {label} command identity drift: {name}")
    require_exact_integer(command.get("exitCode"), 0, f"Story 4.15 {version} {label} {name}:exitCode")
    require(command.get("result") == "passed", f"Story 4.15 {version} {label} command did not pass: {name}")
    require_exact_integer(command.get("tests"), expected_tests, f"Story 4.15 {version} {label} {name}:tests")
    require_exact_integer(command.get("passed"), expected_tests, f"Story 4.15 {version} {label} {name}:passed")
    require_exact_integer(command.get("failed"), 0, f"Story 4.15 {version} {label} {name}:failed")
    require_exact_integer(command.get("skipped"), 0, f"Story 4.15 {version} {label} {name}:skipped")


def validate_v2_pre_review_execution(
    document: Any,
    identity: dict[str, Any],
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[str, datetime]:
    require(isinstance(document, dict), "Story 4.15 v2 pre-review execution must be an object")
    require(
        set(document) == {"schema", "executedAt", "scope", "candidateInputs", "commands", "summary", "authority"},
        "Story 4.15 v2 pre-review execution field set drift",
    )
    require(
        document.get("schema") == "hexalith.eventstore.story-4-15-successor-pre-review-execution/v2",
        "Story 4.15 v2 pre-review execution schema drift",
    )
    executed_at = validate_v2_timestamp(document.get("executedAt"), "pre-review execution", now)
    require(document.get("scope") == "receipt-independent-candidate", "Story 4.15 v2 pre-review execution scope drift")
    require(
        document.get("candidateInputs")
        == {
            "sourceTransitions": identity.get("sourceTransitions"),
            "gateInputs": identity.get("gateInputs"),
        },
        "Story 4.15 v2 pre-review candidate-input binding drift",
    )
    authority = document.get("authority")
    require(
        isinstance(authority, dict)
        and set(authority)
        == {"reviewSubjectFrozen", "reviewReceiptsValidated", "finalHandoffValidated", "externalAuthorityClaimed"}
        and all(authority.get(field) is False for field in authority),
        "Story 4.15 v2 pre-review authority disclosure drift",
    )
    commands = document.get("commands")
    require(
        isinstance(commands, list) and len(commands) == len(V2_PRE_REVIEW_COMMANDS),
        "Story 4.15 v2 pre-review canonical command set drift",
    )
    for command, expected in zip(commands, V2_PRE_REVIEW_COMMANDS, strict=True):
        validate_v2_passed_command(command, expected, "pre-review")
    tests = sum(expected[2] for expected in V2_PRE_REVIEW_COMMANDS)
    expected_summary = {
        "commands": len(V2_PRE_REVIEW_COMMANDS),
        "successfulCommands": len(V2_PRE_REVIEW_COMMANDS),
        "tests": tests,
        "passed": tests,
        "failed": 0,
        "skipped": 0,
    }
    summary = document.get("summary")
    require(isinstance(summary, dict) and set(summary) == set(expected_summary), "Story 4.15 v2 pre-review summary field set drift")
    for field, expected in expected_summary.items():
        require_exact_integer(summary.get(field), expected, f"Story 4.15 v2 pre-review summary {field}")
    return sha256_bytes(v2_snapshot(snapshots, V2_SUCCESSOR / "pre-review-execution.json")), executed_at


def validate_v2_review_subject(
    subject: Any,
    identity: dict[str, Any],
    limitations_sha256: str,
    validator_sha256: str,
    execution_sha256: str,
    execution_at: datetime,
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[str, datetime]:
    require(isinstance(subject, dict), "Story 4.15 v2 review subject must be an object")
    require(
        set(subject)
        == {
            "schema",
            "frozenAt",
            "proposedDecision",
            "predecessor",
            "reviewedImage",
            "sourceTransitions",
            "gateInputs",
            "bindings",
            "requiredReviews",
            "authority",
        },
        "Story 4.15 v2 review subject field set drift",
    )
    require(
        subject.get("schema") == "hexalith.eventstore.story-4-15-successor-review-subject/v2",
        "Story 4.15 v2 review subject schema drift",
    )
    frozen_at = validate_v2_timestamp(subject.get("frozenAt"), "review-subject freeze", now)
    require(execution_at < frozen_at, "Story 4.15 v2 pre-review execution is not strictly before subject freeze")
    require(subject.get("proposedDecision") == "current-source-closure-complete", "Story 4.15 v2 proposed decision drift")
    require(subject.get("predecessor") == expected_v2_predecessor(), "Story 4.15 v2 review predecessor drift")
    subject_reviewed_image = subject.get("reviewedImage")
    require(subject_reviewed_image == expected_v2_reviewed_image(), "Story 4.15 v2 review index drift")
    require(
        isinstance(subject_reviewed_image, dict) and subject_reviewed_image.get("amd64ChildIsAuthority") is False,
        "Story 4.15 v2 review index authority type drift",
    )
    require(subject.get("sourceTransitions") == identity.get("sourceTransitions"), "Story 4.15 v2 review source transition drift")
    require(subject.get("gateInputs") == identity.get("gateInputs"), "Story 4.15 v2 review gate-input drift")
    require(
        subject.get("bindings")
        == {
            "sourceIdentity": {
                "path": "source-artifact-identity.json",
                "sha256": sha256_bytes(v2_snapshot(snapshots, V2_SUCCESSOR / "source-artifact-identity.json")),
            },
            "limitations": {"path": "limitations.json", "sha256": limitations_sha256},
            "validatorRecord": {
                "path": "validator-sha256.txt",
                "sha256": sha256_bytes(v2_snapshot(snapshots, V2_SUCCESSOR / "validator-sha256.txt")),
                "validatorPath": "tools/validate-oq8-platform-evidence.py",
                "validatorSha256": validator_sha256,
            },
            "preReviewExecution": {"path": "pre-review-execution.json", "sha256": execution_sha256},
        },
        "Story 4.15 v2 review candidate binding drift",
    )
    require(
        subject.get("requiredReviews")
        == [
            {
                "role": role,
                "reviewer": REVIEW_ROSTER[role],
                "scope": V2_REVIEW_SCOPES[role],
                "status": "required",
            }
            for role in ("architecture", "security", "test")
        ],
        "Story 4.15 v2 required review roster or scope drift",
    )
    validate_authority(subject.get("authority"))
    return sha256_bytes(v2_snapshot(snapshots, V2_SUCCESSOR / "review-subject.json")), frozen_at


def validate_v2_test_receipt_verification(value: Any) -> None:
    require(
        isinstance(value, list) and len(value) == len(V2_TEST_RECEIPT_VERIFICATION),
        "Story 4.15 v2 test review verification set drift",
    )
    for command, expected in zip(value, V2_TEST_RECEIPT_VERIFICATION, strict=True):
        validate_v2_passed_command(command, expected, "test review verification")


def validate_v2_reviews(
    subject_sha256: str,
    limitations_sha256: str,
    frozen_at: datetime,
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[dict[str, str], list[datetime]]:
    receipts: dict[str, str] = {}
    issued_at_values: list[datetime] = []
    for role, reviewer in REVIEW_ROSTER.items():
        path = V2_SUCCESSOR / "reviews" / f"{role}.json"
        document = v2_snapshot_json(snapshots, f"reviews/{role}.json")
        require(isinstance(document, dict), f"Story 4.15 v2 {role} review must be an object")
        require(
            set(document)
            == ({
                "schema",
                "role",
                "reviewer",
                "issuedAt",
                "decision",
                "subjectSha256",
                "limitationsSha256",
                "acceptedScope",
                "findings",
                "authority",
            } | ({"verification"} if role == "test" else set())),
            f"Story 4.15 v2 {role} review field set drift",
        )
        require(
            document.get("schema") == "hexalith.eventstore.story-4-15-successor-review-receipt/v2",
            f"Story 4.15 v2 {role} review schema drift",
        )
        require(document.get("role") == role, f"Story 4.15 v2 {role} review role drift")
        require(document.get("reviewer") == reviewer, f"Story 4.15 v2 {role} reviewer identity drift")
        issued_at = validate_v2_timestamp(document.get("issuedAt"), f"{role} receipt", now)
        require(issued_at > frozen_at, f"Story 4.15 v2 {role} review predates the frozen subject")
        issued_at_values.append(issued_at)
        require(document.get("decision") == "approved", f"Story 4.15 v2 {role} review is not approved")
        require(document.get("subjectSha256") == subject_sha256, f"Story 4.15 v2 {role} review subject drift")
        require(document.get("limitationsSha256") == limitations_sha256, f"Story 4.15 v2 {role} review limitations drift")
        require(document.get("acceptedScope") == V2_REVIEW_SCOPES[role], f"Story 4.15 v2 {role} review scope drift")
        findings = document.get("findings")
        require(
            isinstance(findings, list)
            and findings
            and all(isinstance(finding, str) and finding.strip() for finding in findings),
            f"Story 4.15 v2 {role} review findings are missing or blank",
        )
        validate_authority(document.get("authority"))
        if role == "test":
            validate_v2_test_receipt_verification(document.get("verification"))
        receipts[role] = sha256_bytes(v2_snapshot(snapshots, path))
    return receipts, issued_at_values


def validate_v2_handoff(
    document: Any,
    subject_sha256: str,
    limitations_sha256: str,
    receipts: dict[str, str],
    receipt_times: list[datetime],
    now: datetime,
) -> None:
    require(isinstance(document, dict), "Story 4.15 v2 handoff must be an object")
    require(
        set(document)
        == {
            "schema",
            "assembledAt",
            "story",
            "predecessor",
            "reviewSubjectSha256",
            "limitationsSha256",
            "reviewReceipts",
            "consumerInstructions",
            "authority",
        },
        "Story 4.15 v2 handoff field set drift",
    )
    require(
        document.get("schema") == "hexalith.eventstore.story-4-15-successor-source-only-handoff/v2",
        "Story 4.15 v2 handoff schema drift",
    )
    assembled_at = validate_v2_timestamp(document.get("assembledAt"), "handoff assembly", now)
    require(receipt_times and assembled_at > max(receipt_times), "Story 4.15 v2 handoff predates a review receipt")
    require(document.get("story") == "4.15", "Story 4.15 v2 handoff story drift")
    require(document.get("predecessor") == expected_v2_predecessor(), "Story 4.15 v2 handoff predecessor drift")
    require(document.get("reviewSubjectSha256") == subject_sha256, "Story 4.15 v2 handoff subject drift")
    require(document.get("limitationsSha256") == limitations_sha256, "Story 4.15 v2 handoff limitations drift")
    require(document.get("reviewReceipts") == receipts, "Story 4.15 v2 handoff receipt set drift")
    require(
        document.get("consumerInstructions")
        == {
            "mode": "source-only",
            "verifyCommand": "python3 tools/validate-oq8-platform-evidence.py",
            "historicalRule": "Validate Story 4.15 v1 only against its immutable historical artifacts and completed-v1 closure snapshot.",
            "currentRule": "Treat current source as closed only when this complete v2 successor validates against the current candidate bytes.",
        },
        "Story 4.15 v2 consumer instructions drift",
    )
    validate_authority(document.get("authority"))


def validate_v2_successor(
    snapshots: dict[str, bytes] | None = None,
    now: datetime | None = None,
) -> None:
    current_snapshots = capture_v2_snapshots() if snapshots is None else snapshots
    current_utc = datetime.now(timezone.utc) if now is None else now
    require(current_utc.tzinfo is not None, "Story 4.15 v2 current UTC must be timezone-aware")
    validate_v2_manifest(current_snapshots)
    identity = validate_v2_source_identity(current_snapshots)
    limitations_sha256 = validate_v2_limitations(current_snapshots)
    validator_sha256 = validate_v2_validator_identity(current_snapshots)
    execution_sha256, execution_at = validate_v2_pre_review_execution(
        v2_snapshot_json(current_snapshots, "pre-review-execution.json"),
        identity,
        current_snapshots,
        current_utc,
    )
    subject_sha256, frozen_at = validate_v2_review_subject(
        v2_snapshot_json(current_snapshots, "review-subject.json"),
        identity,
        limitations_sha256,
        validator_sha256,
        execution_sha256,
        execution_at,
        current_snapshots,
        current_utc,
    )
    receipts, receipt_times = validate_v2_reviews(
        subject_sha256,
        limitations_sha256,
        frozen_at,
        current_snapshots,
        current_utc,
    )
    validate_v2_handoff(
        v2_snapshot_json(current_snapshots, "source-only-handoff.json"),
        subject_sha256,
        limitations_sha256,
        receipts,
        receipt_times,
        current_utc,
    )
    require(
        sha256_bytes(v2_snapshot(current_snapshots, V2_SUCCESSOR / "closure-sha256.txt"))
        == V2_CLOSURE_MANIFEST_SHA256,
        "Story 4.15 v2 historical closure manifest identity drift",
    )


def expected_v3_predecessor() -> dict[str, str]:
    return {
        "completedV2ClosureCommit": COMPLETED_V2_CLOSURE_COMMIT,
        "reviewSubjectSha256": V2_REVIEW_SUBJECT_SHA256,
        "closureManifestSha256": V2_CLOSURE_MANIFEST_SHA256,
    }


def v3_snapshot_key(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        fail("Story 4.15 v3 snapshot path is outside the repository root")


def v3_snapshot(snapshots: dict[str, bytes], path: Path) -> bytes:
    key = v3_snapshot_key(path)
    require(key in snapshots, f"Story 4.15 v3 snapshot is missing: {key}")
    return snapshots[key]


def v3_snapshot_text(snapshots: dict[str, bytes], path: Path) -> str:
    try:
        return v3_snapshot(snapshots, path).decode("utf-8")
    except UnicodeError:
        fail(f"Story 4.15 v3 snapshot is not UTF-8: {v3_snapshot_key(path)}")


def v3_snapshot_json(snapshots: dict[str, bytes], relative: str) -> Any:
    return load_candidate_json_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / relative), relative)


def capture_v3_snapshots() -> dict[str, bytes]:
    require(V3_SUCCESSOR.exists(), "Story 4.15 v3 successor directory is missing or symlinked")
    require_no_symlink_components(V3_SUCCESSOR, "Story 4.15 v3 successor directory")
    require(V3_SUCCESSOR.is_dir(), "Story 4.15 v3 successor directory is missing or symlinked")
    expected_entries = V3_SUCCESSOR_FILES | {"closure-sha256.txt", "reviews"}
    actual_entries = relative_tree_entries(V3_SUCCESSOR)
    require(actual_entries == expected_entries, "Story 4.15 v3 successor file set drift")
    for relative in actual_entries:
        path = V3_SUCCESSOR / relative
        require_no_symlink_components(path, f"Story 4.15 v3 artifact {relative}")
        if relative == "reviews":
            require(path.is_dir(), "Story 4.15 v3 reviews path is not a directory")
        else:
            require(path.is_file(), f"Story 4.15 v3 artifact missing: {relative}")

    snapshots: dict[str, bytes] = {}
    for relative in V3_SUCCESSOR_FILES | {"closure-sha256.txt"}:
        path = V3_SUCCESSOR / relative
        snapshots[v3_snapshot_key(path)] = read_bounded_regular_snapshot(
            path,
            MAX_V2_ARTIFACT_BYTES,
            f"Story 4.15 v3 artifact {relative}",
        )
    for relative in sorted(V3_GATE_INPUT_PATHS):
        path = ROOT / relative
        snapshots[v3_snapshot_key(path)] = read_bounded_regular_snapshot(
            path,
            MAX_V2_BOUND_SOURCE_BYTES,
            f"Story 4.15 v3 bound source {relative}",
        )
    return snapshots


def validate_v3_manifest(snapshots: dict[str, bytes]) -> dict[str, str]:
    manifest_path = V3_SUCCESSOR / "closure-sha256.txt"
    lines = v3_snapshot_text(snapshots, manifest_path).splitlines()
    require(lines == sorted(lines, key=lambda line: line.split("  ", 1)[-1]), "Story 4.15 v3 closure manifest is not path-sorted")
    manifest: dict[str, str] = {}
    for line in lines:
        parts = line.split("  ", 1)
        require(len(parts) == 2 and SHA256_RE.fullmatch(parts[0]) is not None, "Malformed Story 4.15 v3 closure manifest line")
        digest, relative = parts
        path = Path(relative)
        require(
            relative not in manifest
            and not path.is_absolute()
            and ".." not in path.parts
            and path.as_posix() == relative,
            "Unsafe or duplicate Story 4.15 v3 closure manifest path",
        )
        manifest[relative] = digest
    require(set(manifest) == V3_SUCCESSOR_FILES, "Story 4.15 v3 closure manifest file set drift")
    for relative, expected in manifest.items():
        artifact_bytes = v3_snapshot(snapshots, V3_SUCCESSOR / relative)
        require(sha256_bytes(artifact_bytes) == expected, f"Story 4.15 v3 checksum mismatch: {relative}")
        try:
            artifact_text = artifact_bytes.decode("utf-8")
        except UnicodeError:
            fail(f"Story 4.15 v3 artifact is not UTF-8: {relative}")
        scan_support_safe_text(artifact_text, relative)
    return manifest


def validate_v3_source_identity(snapshots: dict[str, bytes]) -> dict[str, Any]:
    identity = v3_snapshot_json(snapshots, "source-artifact-identity.json")
    require(isinstance(identity, dict), "Story 4.15 v3 source identity must be an object")
    require(
        set(identity)
        == {
            "schema",
            "reviewedOn",
            "repository",
            "predecessor",
            "historicalSdkSuccessor",
            "landedSource",
            "headAncestry",
            "sourceTransitions",
            "gateInputs",
            "bindingRule",
        },
        "Story 4.15 v3 source identity field set drift",
    )
    require(
        identity.get("schema") == "hexalith.eventstore.story-4-15-successor-source-identity/v3",
        "Story 4.15 v3 source identity schema drift",
    )
    require(identity.get("reviewedOn") == V3_REVIEW_DATE, "Story 4.15 v3 source identity review date drift")
    require(identity.get("repository") == "Hexalith/Hexalith.EventStore", "Story 4.15 v3 source identity repository drift")
    require(identity.get("predecessor") == expected_v3_predecessor(), "Story 4.15 v3 predecessor link drift")
    require(
        identity.get("historicalSdkSuccessor")
        == {
            "directory": SUCCESSOR_DIRECTORY,
            "manifestSha256": SDK_SUCCESSOR_MANIFEST_SHA256,
        },
        "Story 4.15 v3 historical SDK successor link drift",
    )
    require(
        identity.get("landedSource") == {"commit": LANDED_SOURCE, "tree": LANDED_TREE},
        "Story 4.15 v3 landed source identity drift",
    )
    require(
        identity.get("headAncestry") == {"baseCommit": LANDED_SOURCE, "required": True},
        "Story 4.15 v3 HEAD ancestry declaration drift",
    )
    landed_commit = run_git("rev-parse", "--verify", f"{LANDED_SOURCE}^{{commit}}").decode("ascii").strip()
    landed_tree = run_git("rev-parse", "--verify", f"{LANDED_SOURCE}^{{tree}}").decode("ascii").strip()
    require(landed_commit == LANDED_SOURCE, "Story 4.15 v3 landed commit Git identity drift")
    require(landed_tree == LANDED_TREE, "Story 4.15 v3 landed tree Git identity drift")
    run_git("merge-base", "--is-ancestor", LANDED_SOURCE, "HEAD")
    require(
        identity.get("bindingRule") == V3_BINDING_RULE,
        "Story 4.15 v3 source binding rule drift",
    )

    transitions = identity.get("sourceTransitions")
    require(isinstance(transitions, dict) and set(transitions) == V3_SOURCE_PATHS, "Story 4.15 v3 source transition path set drift")
    for relative in V3_SOURCE_PATHS:
        transition = transitions.get(relative)
        require(
            isinstance(transition, dict) and set(transition) == {"predecessorSha256", "successorSha256"},
            f"Story 4.15 v3 source transition field set drift: {relative}",
        )
        require(
            transition.get("predecessorSha256") == sha256_git_file(COMPLETED_V2_CLOSURE_COMMIT, relative),
            f"Story 4.15 v3 predecessor source identity drift: {relative}",
        )
        require(
            transition.get("successorSha256") == sha256_bytes(v3_snapshot(snapshots, ROOT / relative)),
            f"Story 4.15 v3 current source identity drift: {relative}",
        )

    gate_inputs = identity.get("gateInputs")
    require(isinstance(gate_inputs, dict) and set(gate_inputs) == V3_GATE_INPUT_PATHS, "Story 4.15 v3 gate-input path set drift")
    for relative, expected in gate_inputs.items():
        require_sha256(expected, f"Story 4.15 v3 gate input:{relative}")
        current_bytes = v3_snapshot(snapshots, ROOT / relative)
        require(sha256_bytes(current_bytes) == expected, f"Story 4.15 v3 gate-input identity drift: {relative}")
        if relative == ".github/workflows/integration.yml":
            try:
                source = current_bytes.decode("utf-8")
            except UnicodeError:
                fail(f"Story 4.15 v3 current source is not UTF-8: {relative}")
            require(extract_v2_workflow_image(source) == POSTGRES_IMAGE, f"Story 4.15 v3 current source PostgreSQL image drift: {relative}")
        elif relative == "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlFixture.cs":
            try:
                source = current_bytes.decode("utf-8")
            except UnicodeError:
                fail(f"Story 4.15 v3 current source is not UTF-8: {relative}")
            require(extract_v2_fixture_image(source) == POSTGRES_IMAGE, f"Story 4.15 v3 current source PostgreSQL image drift: {relative}")
    return identity


def validate_v3_limitations(snapshots: dict[str, bytes]) -> str:
    document = v3_snapshot_json(snapshots, "limitations.json")
    require(
        document == {"schema": "hexalith.eventstore.story-4-15-successor-limitations/v3", "limitations": V3_LIMITATIONS},
        "Story 4.15 v3 limitation text or order drift",
    )
    return sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "limitations.json"))


def validate_v3_validator_identity(snapshots: dict[str, bytes]) -> str:
    lines = v3_snapshot_text(snapshots, V3_SUCCESSOR / "validator-sha256.txt").splitlines()
    require(len(lines) == 1, "Story 4.15 v3 validator identity record is malformed")
    parts = lines[0].split("  ", 1)
    require(
        len(parts) == 2
        and parts[1] == "tools/validate-oq8-platform-evidence.py"
        and SHA256_RE.fullmatch(parts[0]) is not None,
        "Story 4.15 v3 validator identity record is malformed",
    )
    require(
        parts[0] == sha256_bytes(v3_snapshot(snapshots, ROOT / parts[1])),
        "Story 4.15 v3 current validator identity drift",
    )
    return parts[0]


def validate_v3_pre_review_execution(
    document: Any,
    identity: dict[str, Any],
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[str, datetime]:
    require(isinstance(document, dict), "Story 4.15 v3 pre-review execution must be an object")
    require(
        set(document) == {"schema", "executedAt", "scope", "candidateInputs", "commands", "summary", "authority"},
        "Story 4.15 v3 pre-review execution field set drift",
    )
    require(
        document.get("schema") == "hexalith.eventstore.story-4-15-successor-pre-review-execution/v3",
        "Story 4.15 v3 pre-review execution schema drift",
    )
    executed_at = validate_successor_timestamp(document.get("executedAt"), "pre-review execution", now, "v3")
    require(document.get("scope") == "receipt-independent-candidate", "Story 4.15 v3 pre-review execution scope drift")
    require(
        document.get("candidateInputs")
        == {
            "historicalSdkSuccessor": identity.get("historicalSdkSuccessor"),
            "landedSource": identity.get("landedSource"),
            "headAncestry": identity.get("headAncestry"),
            "sourceTransitions": identity.get("sourceTransitions"),
            "gateInputs": identity.get("gateInputs"),
        },
        "Story 4.15 v3 pre-review candidate-input binding drift",
    )
    authority = document.get("authority")
    require(
        isinstance(authority, dict)
        and set(authority) == {"reviewSubjectFrozen", "reviewReceiptsValidated", "finalHandoffValidated", "externalAuthorityClaimed"}
        and all(authority.get(field) is False for field in authority),
        "Story 4.15 v3 pre-review authority disclosure drift",
    )
    commands = document.get("commands")
    require(isinstance(commands, list) and len(commands) == len(V3_PRE_REVIEW_COMMANDS), "Story 4.15 v3 pre-review canonical command set drift")
    for command, expected in zip(commands, V3_PRE_REVIEW_COMMANDS, strict=True):
        validate_successor_passed_command(command, expected, "pre-review", "v3")
    tests = sum(expected[2] for expected in V3_PRE_REVIEW_COMMANDS)
    expected_summary = {
        "commands": len(V3_PRE_REVIEW_COMMANDS),
        "successfulCommands": len(V3_PRE_REVIEW_COMMANDS),
        "tests": tests,
        "passed": tests,
        "failed": 0,
        "skipped": 0,
    }
    summary = document.get("summary")
    require(isinstance(summary, dict) and set(summary) == set(expected_summary), "Story 4.15 v3 pre-review summary field set drift")
    for field, expected in expected_summary.items():
        require_exact_integer(summary.get(field), expected, f"Story 4.15 v3 pre-review summary {field}")
    return sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "pre-review-execution.json")), executed_at


def validate_v3_review_subject(
    subject: Any,
    identity: dict[str, Any],
    limitations_sha256: str,
    validator_sha256: str,
    execution_sha256: str,
    execution_at: datetime,
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[str, datetime]:
    require(isinstance(subject, dict), "Story 4.15 v3 review subject must be an object")
    require(
        set(subject)
        == {
            "schema",
            "frozenAt",
            "proposedDecision",
            "predecessor",
            "historicalSdkSuccessor",
            "landedSource",
            "headAncestry",
            "sourceTransitions",
            "gateInputs",
            "bindings",
            "requiredReviews",
            "authority",
        },
        "Story 4.15 v3 review subject field set drift",
    )
    require(subject.get("schema") == "hexalith.eventstore.story-4-15-successor-review-subject/v3", "Story 4.15 v3 review subject schema drift")
    frozen_at = validate_successor_timestamp(subject.get("frozenAt"), "review-subject freeze", now, "v3")
    require(execution_at < frozen_at, "Story 4.15 v3 pre-review execution is not strictly before subject freeze")
    require(subject.get("proposedDecision") == "active-current-source-closure-complete", "Story 4.15 v3 proposed decision drift")
    require(subject.get("predecessor") == expected_v3_predecessor(), "Story 4.15 v3 review predecessor drift")
    require(
        subject.get("historicalSdkSuccessor") == identity.get("historicalSdkSuccessor"),
        "Story 4.15 v3 review historical SDK successor drift",
    )
    require(subject.get("landedSource") == identity.get("landedSource"), "Story 4.15 v3 review landed source drift")
    require(subject.get("headAncestry") == identity.get("headAncestry"), "Story 4.15 v3 review HEAD ancestry drift")
    require(subject.get("sourceTransitions") == identity.get("sourceTransitions"), "Story 4.15 v3 review source transition drift")
    require(subject.get("gateInputs") == identity.get("gateInputs"), "Story 4.15 v3 review gate-input drift")
    require(
        subject.get("bindings")
        == {
            "sourceIdentity": {"path": "source-artifact-identity.json", "sha256": sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "source-artifact-identity.json"))},
            "limitations": {"path": "limitations.json", "sha256": limitations_sha256},
            "validatorRecord": {
                "path": "validator-sha256.txt",
                "sha256": sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "validator-sha256.txt")),
                "validatorPath": "tools/validate-oq8-platform-evidence.py",
                "validatorSha256": validator_sha256,
            },
            "preReviewExecution": {"path": "pre-review-execution.json", "sha256": execution_sha256},
        },
        "Story 4.15 v3 review candidate binding drift",
    )
    require(
        subject.get("requiredReviews")
        == [
            {"role": role, "reviewer": REVIEW_ROSTER[role], "scope": V3_REVIEW_SCOPES[role], "status": "required"}
            for role in ("architecture", "security", "test")
        ],
        "Story 4.15 v3 required review roster or scope drift",
    )
    validate_authority(subject.get("authority"))
    return sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "review-subject.json")), frozen_at


def validate_v3_reviews(
    subject_sha256: str,
    limitations_sha256: str,
    frozen_at: datetime,
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[dict[str, str], list[datetime]]:
    receipts: dict[str, str] = {}
    issued_at_values: list[datetime] = []
    for role, reviewer in REVIEW_ROSTER.items():
        path = V3_SUCCESSOR / "reviews" / f"{role}.json"
        document = v3_snapshot_json(snapshots, f"reviews/{role}.json")
        require(isinstance(document, dict), f"Story 4.15 v3 {role} review must be an object")
        require(
            set(document)
            == ({"schema", "role", "reviewer", "issuedAt", "decision", "subjectSha256", "limitationsSha256", "acceptedScope", "findings", "authority"} | ({"verification"} if role == "test" else set())),
            f"Story 4.15 v3 {role} review field set drift",
        )
        require(document.get("schema") == "hexalith.eventstore.story-4-15-successor-review-receipt/v3", f"Story 4.15 v3 {role} review schema drift")
        require(document.get("role") == role, f"Story 4.15 v3 {role} review role drift")
        require(document.get("reviewer") == reviewer, f"Story 4.15 v3 {role} reviewer identity drift")
        issued_at = validate_successor_timestamp(document.get("issuedAt"), f"{role} receipt", now, "v3")
        require(issued_at > frozen_at, f"Story 4.15 v3 {role} review predates the frozen subject")
        issued_at_values.append(issued_at)
        require(document.get("decision") == "approved", f"Story 4.15 v3 {role} review is not approved")
        require(document.get("subjectSha256") == subject_sha256, f"Story 4.15 v3 {role} review subject drift")
        require(document.get("limitationsSha256") == limitations_sha256, f"Story 4.15 v3 {role} review limitations drift")
        require(document.get("acceptedScope") == V3_REVIEW_SCOPES[role], f"Story 4.15 v3 {role} review scope drift")
        findings = document.get("findings")
        require(
            isinstance(findings, list) and findings and all(isinstance(finding, str) and finding.strip() for finding in findings),
            f"Story 4.15 v3 {role} review findings are missing or blank",
        )
        validate_authority(document.get("authority"))
        if role == "test":
            verification = document.get("verification")
            require(
                isinstance(verification, list) and len(verification) == len(V3_TEST_RECEIPT_VERIFICATION),
                "Story 4.15 v3 test review verification set drift",
            )
            for command, expected in zip(verification, V3_TEST_RECEIPT_VERIFICATION, strict=True):
                validate_successor_passed_command(command, expected, "test review verification", "v3")
        receipts[role] = sha256_bytes(v3_snapshot(snapshots, path))
    return receipts, issued_at_values


def validate_v3_handoff(
    document: Any,
    snapshots: dict[str, bytes],
    subject_sha256: str,
    limitations_sha256: str,
    receipts: dict[str, str],
    receipt_times: list[datetime],
    now: datetime,
) -> str:
    require(isinstance(document, dict), "Story 4.15 v3 handoff must be an object")
    require(
        set(document) == {"schema", "assembledAt", "story", "predecessor", "reviewSubjectSha256", "limitationsSha256", "reviewReceipts", "consumerInstructions", "authority"},
        "Story 4.15 v3 handoff field set drift",
    )
    require(document.get("schema") == "hexalith.eventstore.story-4-15-successor-source-only-handoff/v3", "Story 4.15 v3 handoff schema drift")
    assembled_at = validate_successor_timestamp(document.get("assembledAt"), "handoff assembly", now, "v3")
    require(receipt_times and assembled_at > max(receipt_times), "Story 4.15 v3 handoff predates a review receipt")
    require(document.get("story") == "4.15", "Story 4.15 v3 handoff story drift")
    require(document.get("predecessor") == expected_v3_predecessor(), "Story 4.15 v3 handoff predecessor drift")
    require(document.get("reviewSubjectSha256") == subject_sha256, "Story 4.15 v3 handoff subject drift")
    require(document.get("limitationsSha256") == limitations_sha256, "Story 4.15 v3 handoff limitations drift")
    require(document.get("reviewReceipts") == receipts, "Story 4.15 v3 handoff receipt set drift")
    require(
        document.get("consumerInstructions")
        == {
            "mode": "source-only",
            "installCommand": V3_CONSUMER_INSTALL_COMMAND,
            "verifyCommand": ".oq8-python/bin/python tools/validate-oq8-platform-evidence.py",
            "historicalRule": V3_CONSUMER_HISTORICAL_RULE,
            "currentRule": V3_CONSUMER_CURRENT_RULE,
        },
        "Story 4.15 v3 consumer instructions drift",
    )
    validate_authority(document.get("authority"))
    return sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "source-only-handoff.json"))


def validate_v3_successor(
    snapshots: dict[str, bytes] | None = None,
    now: datetime | None = None,
) -> tuple[dict[str, str], str, str, str, str]:
    current_snapshots = capture_v3_snapshots() if snapshots is None else snapshots
    current_utc = datetime.now(timezone.utc) if now is None else now
    require(current_utc.tzinfo is not None, "Story 4.15 v3 current UTC must be timezone-aware")
    manifest = validate_v3_manifest(current_snapshots)
    manifest_sha256 = sha256_bytes(v3_snapshot(current_snapshots, V3_SUCCESSOR / "closure-sha256.txt"))
    identity = validate_v3_source_identity(current_snapshots)
    identity_sha256 = sha256_bytes(v3_snapshot(current_snapshots, V3_SUCCESSOR / "source-artifact-identity.json"))
    limitations_sha256 = validate_v3_limitations(current_snapshots)
    validator_sha256 = validate_v3_validator_identity(current_snapshots)
    execution_sha256, execution_at = validate_v3_pre_review_execution(
        v3_snapshot_json(current_snapshots, "pre-review-execution.json"),
        identity,
        current_snapshots,
        current_utc,
    )
    subject_sha256, frozen_at = validate_v3_review_subject(
        v3_snapshot_json(current_snapshots, "review-subject.json"),
        identity,
        limitations_sha256,
        validator_sha256,
        execution_sha256,
        execution_at,
        current_snapshots,
        current_utc,
    )
    receipts, receipt_times = validate_v3_reviews(
        subject_sha256,
        limitations_sha256,
        frozen_at,
        current_snapshots,
        current_utc,
    )
    handoff_sha256 = validate_v3_handoff(
        v3_snapshot_json(current_snapshots, "source-only-handoff.json"),
        current_snapshots,
        subject_sha256,
        limitations_sha256,
        receipts,
        receipt_times,
        current_utc,
    )
    return manifest, manifest_sha256, identity_sha256, subject_sha256, handoff_sha256


def capture_historical_v3_snapshots() -> dict[str, bytes]:
    require(V3_SUCCESSOR.exists(), "Story 4.15 v3 historical successor directory is missing or symlinked")
    require_no_symlink_components(V3_SUCCESSOR, "Story 4.15 v3 historical successor directory")
    require(V3_SUCCESSOR.is_dir(), "Story 4.15 v3 historical successor directory is missing or symlinked")
    expected_entries = V3_SUCCESSOR_FILES | {"closure-sha256.txt", "reviews"}
    actual_entries = relative_tree_entries(V3_SUCCESSOR)
    require(actual_entries == expected_entries, "Story 4.15 v3 historical successor file set drift")
    snapshots: dict[str, bytes] = {}
    for relative in V3_SUCCESSOR_FILES | {"closure-sha256.txt"}:
        path = V3_SUCCESSOR / relative
        require_no_symlink_components(path, f"Story 4.15 v3 historical artifact {relative}")
        current = read_bounded_regular_snapshot(
            path,
            MAX_V2_ARTIFACT_BYTES,
            f"Story 4.15 v3 historical artifact {relative}",
        )
        historical = git_file(COMPLETED_V3_CLOSURE_COMMIT, f"{V3_SUCCESSOR_DIRECTORY}/{relative}")
        require(current == historical, f"Story 4.15 v3 historical artifact drift: {relative}")
        snapshots[v3_snapshot_key(path)] = current
    return snapshots


def validate_historical_v3_successor() -> tuple[dict[str, str], str, str, str, str]:
    snapshots = capture_historical_v3_snapshots()
    manifest = validate_v3_manifest(snapshots)
    manifest_sha256 = sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "closure-sha256.txt"))
    identity_sha256 = sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "source-artifact-identity.json"))
    subject_sha256 = sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "review-subject.json"))
    handoff_sha256 = sha256_bytes(v3_snapshot(snapshots, V3_SUCCESSOR / "source-only-handoff.json"))
    require(manifest_sha256 == V3_CLOSURE_MANIFEST_SHA256, "Story 4.15 v3 historical manifest identity drift")
    require(identity_sha256 == V3_SOURCE_IDENTITY_SHA256, "Story 4.15 v3 historical source identity drift")
    require(subject_sha256 == V3_REVIEW_SUBJECT_SHA256, "Story 4.15 v3 historical review subject drift")
    require(handoff_sha256 == V3_HANDOFF_SHA256, "Story 4.15 v3 historical handoff drift")
    return manifest, manifest_sha256, identity_sha256, subject_sha256, handoff_sha256


def expected_v4_predecessor() -> dict[str, str]:
    return {
        "completedV3ClosureCommit": COMPLETED_V3_CLOSURE_COMMIT,
        "reviewSubjectSha256": V3_REVIEW_SUBJECT_SHA256,
        "closureManifestSha256": V3_CLOSURE_MANIFEST_SHA256,
    }


def v4_snapshot_key(path: Path) -> str:
    try:
        return path.relative_to(ROOT).as_posix()
    except ValueError:
        fail("Story 4.15 v4 snapshot path is outside the repository root")


def v4_snapshot(snapshots: dict[str, bytes], path: Path) -> bytes:
    key = v4_snapshot_key(path)
    require(key in snapshots, f"Story 4.15 v4 snapshot is missing: {key}")
    return snapshots[key]


def v4_snapshot_text(snapshots: dict[str, bytes], path: Path) -> str:
    try:
        return v4_snapshot(snapshots, path).decode("utf-8")
    except UnicodeError:
        fail(f"Story 4.15 v4 snapshot is not UTF-8: {v4_snapshot_key(path)}")


def v4_snapshot_json(snapshots: dict[str, bytes], relative: str) -> Any:
    return load_candidate_json_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / relative), relative)


def capture_v4_snapshots() -> dict[str, bytes]:
    require(V4_SUCCESSOR.exists(), "Story 4.15 v4 successor directory is missing or symlinked")
    require_no_symlink_components(V4_SUCCESSOR, "Story 4.15 v4 successor directory")
    require(V4_SUCCESSOR.is_dir(), "Story 4.15 v4 successor directory is missing or symlinked")
    expected_entries = V4_SUCCESSOR_FILES | {"closure-sha256.txt", "reviews"}
    actual_entries = relative_tree_entries(V4_SUCCESSOR)
    require(actual_entries == expected_entries, "Story 4.15 v4 successor file set drift")
    for relative in actual_entries:
        path = V4_SUCCESSOR / relative
        require_no_symlink_components(path, f"Story 4.15 v4 artifact {relative}")
        if relative == "reviews":
            require(path.is_dir(), "Story 4.15 v4 reviews path is not a directory")
        else:
            require(path.is_file(), f"Story 4.15 v4 artifact missing: {relative}")

    snapshots: dict[str, bytes] = {}
    for relative in V4_SUCCESSOR_FILES | {"closure-sha256.txt"}:
        path = V4_SUCCESSOR / relative
        snapshots[v4_snapshot_key(path)] = read_bounded_regular_snapshot(
            path,
            MAX_V2_ARTIFACT_BYTES,
            f"Story 4.15 v4 artifact {relative}",
        )
    for relative in sorted(V4_GATE_INPUT_PATHS):
        path = ROOT / relative
        snapshots[v4_snapshot_key(path)] = read_bounded_regular_snapshot(
            path,
            MAX_V2_BOUND_SOURCE_BYTES,
            f"Story 4.15 v4 bound source {relative}",
        )
    return snapshots


def validate_v4_manifest(snapshots: dict[str, bytes]) -> dict[str, str]:
    lines = v4_snapshot_text(snapshots, V4_SUCCESSOR / "closure-sha256.txt").splitlines()
    require(lines == sorted(lines, key=lambda line: line.split("  ", 1)[-1]), "Story 4.15 v4 closure manifest is not path-sorted")
    manifest: dict[str, str] = {}
    for line in lines:
        parts = line.split("  ", 1)
        require(len(parts) == 2 and SHA256_RE.fullmatch(parts[0]) is not None, "Malformed Story 4.15 v4 closure manifest line")
        digest, relative = parts
        path = Path(relative)
        require(
            relative not in manifest
            and not path.is_absolute()
            and ".." not in path.parts
            and path.as_posix() == relative,
            "Unsafe or duplicate Story 4.15 v4 closure manifest path",
        )
        manifest[relative] = digest
    require(set(manifest) == V4_SUCCESSOR_FILES, "Story 4.15 v4 closure manifest file set drift")
    for relative, expected in manifest.items():
        artifact_bytes = v4_snapshot(snapshots, V4_SUCCESSOR / relative)
        require(sha256_bytes(artifact_bytes) == expected, f"Story 4.15 v4 checksum mismatch: {relative}")
        try:
            artifact_text = artifact_bytes.decode("utf-8")
        except UnicodeError:
            fail(f"Story 4.15 v4 artifact is not UTF-8: {relative}")
        scan_support_safe_text(artifact_text, relative)
    return manifest


def validate_v4_source_identity(snapshots: dict[str, bytes]) -> dict[str, Any]:
    identity = v4_snapshot_json(snapshots, "source-artifact-identity.json")
    require(isinstance(identity, dict), "Story 4.15 v4 source identity must be an object")
    require(
        set(identity)
        == {
            "schema",
            "reviewedOn",
            "repository",
            "predecessor",
            "headAncestry",
            "sourceTransitions",
            "gateInputs",
            "bindingRule",
        },
        "Story 4.15 v4 source identity field set drift",
    )
    require(identity.get("schema") == "hexalith.eventstore.story-4-15-successor-source-identity/v4", "Story 4.15 v4 source identity schema drift")
    require(identity.get("reviewedOn") == V4_REVIEW_DATE, "Story 4.15 v4 source identity review date drift")
    require(identity.get("repository") == "Hexalith/Hexalith.EventStore", "Story 4.15 v4 source identity repository drift")
    require(identity.get("predecessor") == expected_v4_predecessor(), "Story 4.15 v4 predecessor link drift")
    require(
        identity.get("headAncestry") == {"baseCommit": COMPLETED_V3_CLOSURE_COMMIT, "required": True},
        "Story 4.15 v4 HEAD ancestry declaration drift",
    )
    run_git("merge-base", "--is-ancestor", COMPLETED_V3_CLOSURE_COMMIT, "HEAD")
    require(identity.get("bindingRule") == V4_BINDING_RULE, "Story 4.15 v4 source binding rule drift")

    transitions = identity.get("sourceTransitions")
    require(isinstance(transitions, dict) and set(transitions) == V4_SOURCE_PATHS, "Story 4.15 v4 source transition path set drift")
    for relative in V4_SOURCE_PATHS:
        transition = transitions.get(relative)
        require(
            isinstance(transition, dict) and set(transition) == {"predecessorSha256", "successorSha256"},
            f"Story 4.15 v4 source transition field set drift: {relative}",
        )
        require(
            transition.get("predecessorSha256") == sha256_git_file(COMPLETED_V3_CLOSURE_COMMIT, relative),
            f"Story 4.15 v4 predecessor source identity drift: {relative}",
        )
        require(
            transition.get("successorSha256") == sha256_bytes(v4_snapshot(snapshots, ROOT / relative)),
            f"Story 4.15 v4 current source identity drift: {relative}",
        )

    gate_inputs = identity.get("gateInputs")
    require(isinstance(gate_inputs, dict) and set(gate_inputs) == V4_GATE_INPUT_PATHS, "Story 4.15 v4 gate-input path set drift")
    for relative, expected in gate_inputs.items():
        require_sha256(expected, f"Story 4.15 v4 gate input:{relative}")
        current_bytes = v4_snapshot(snapshots, ROOT / relative)
        require(sha256_bytes(current_bytes) == expected, f"Story 4.15 v4 gate-input identity drift: {relative}")
        if relative == ".github/workflows/integration.yml":
            require(extract_v2_workflow_image(current_bytes.decode("utf-8")) == POSTGRES_IMAGE, f"Story 4.15 v4 current source PostgreSQL image drift: {relative}")
        elif relative == "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Fixtures/Oq8PostgresqlFixture.cs":
            require(extract_v2_fixture_image(current_bytes.decode("utf-8")) == POSTGRES_IMAGE, f"Story 4.15 v4 current source PostgreSQL image drift: {relative}")
    return identity


def validate_v4_limitations(snapshots: dict[str, bytes]) -> str:
    document = v4_snapshot_json(snapshots, "limitations.json")
    require(
        document == {"schema": "hexalith.eventstore.story-4-15-successor-limitations/v4", "limitations": V4_LIMITATIONS},
        "Story 4.15 v4 limitation text or order drift",
    )
    return sha256_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / "limitations.json"))


def validate_v4_validator_identity(snapshots: dict[str, bytes]) -> str:
    lines = v4_snapshot_text(snapshots, V4_SUCCESSOR / "validator-sha256.txt").splitlines()
    require(len(lines) == 1, "Story 4.15 v4 validator identity record is malformed")
    parts = lines[0].split("  ", 1)
    require(
        len(parts) == 2
        and parts[1] == "tools/validate-oq8-platform-evidence.py"
        and SHA256_RE.fullmatch(parts[0]) is not None,
        "Story 4.15 v4 validator identity record is malformed",
    )
    require(parts[0] == sha256_bytes(v4_snapshot(snapshots, ROOT / parts[1])), "Story 4.15 v4 current validator identity drift")
    return parts[0]


def validate_v4_pre_review_execution(
    document: Any,
    identity: dict[str, Any],
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[str, datetime]:
    require(isinstance(document, dict), "Story 4.15 v4 pre-review execution must be an object")
    require(
        set(document) == {"schema", "executedAt", "scope", "candidateInputs", "commands", "summary", "authority"},
        "Story 4.15 v4 pre-review execution field set drift",
    )
    require(document.get("schema") == "hexalith.eventstore.story-4-15-successor-pre-review-execution/v4", "Story 4.15 v4 pre-review execution schema drift")
    executed_at = validate_successor_timestamp(document.get("executedAt"), "pre-review execution", now, "v4")
    require(document.get("scope") == "receipt-independent-candidate", "Story 4.15 v4 pre-review execution scope drift")
    require(
        document.get("candidateInputs")
        == {
            "predecessor": identity.get("predecessor"),
            "headAncestry": identity.get("headAncestry"),
            "sourceTransitions": identity.get("sourceTransitions"),
            "gateInputs": identity.get("gateInputs"),
        },
        "Story 4.15 v4 pre-review candidate-input binding drift",
    )
    authority = document.get("authority")
    require(
        isinstance(authority, dict)
        and set(authority) == {"reviewSubjectFrozen", "reviewReceiptsValidated", "finalHandoffValidated", "externalAuthorityClaimed"}
        and all(authority.get(field) is False for field in authority),
        "Story 4.15 v4 pre-review authority disclosure drift",
    )
    commands = document.get("commands")
    require(isinstance(commands, list) and len(commands) == len(V4_PRE_REVIEW_COMMANDS), "Story 4.15 v4 pre-review canonical command set drift")
    for command, expected in zip(commands, V4_PRE_REVIEW_COMMANDS, strict=True):
        validate_successor_passed_command(command, expected, "pre-review", "v4")
    tests = sum(expected[2] for expected in V4_PRE_REVIEW_COMMANDS)
    expected_summary = {
        "commands": len(V4_PRE_REVIEW_COMMANDS),
        "successfulCommands": len(V4_PRE_REVIEW_COMMANDS),
        "tests": tests,
        "passed": tests,
        "failed": 0,
        "skipped": 0,
    }
    summary = document.get("summary")
    require(isinstance(summary, dict) and set(summary) == set(expected_summary), "Story 4.15 v4 pre-review summary field set drift")
    for field, expected in expected_summary.items():
        require_exact_integer(summary.get(field), expected, f"Story 4.15 v4 pre-review summary {field}")
    return sha256_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / "pre-review-execution.json")), executed_at


def validate_v4_review_subject(
    subject: Any,
    identity: dict[str, Any],
    limitations_sha256: str,
    validator_sha256: str,
    execution_sha256: str,
    execution_at: datetime,
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[str, datetime]:
    require(isinstance(subject, dict), "Story 4.15 v4 review subject must be an object")
    require(
        set(subject)
        == {
            "schema",
            "frozenAt",
            "proposedDecision",
            "predecessor",
            "headAncestry",
            "sourceTransitions",
            "gateInputs",
            "bindings",
            "requiredReviews",
            "authority",
        },
        "Story 4.15 v4 review subject field set drift",
    )
    require(subject.get("schema") == "hexalith.eventstore.story-4-15-successor-review-subject/v4", "Story 4.15 v4 review subject schema drift")
    frozen_at = validate_successor_timestamp(subject.get("frozenAt"), "review-subject freeze", now, "v4")
    require(execution_at < frozen_at, "Story 4.15 v4 pre-review execution is not strictly before subject freeze")
    require(subject.get("proposedDecision") == "active-current-source-evidence-approved", "Story 4.15 v4 proposed decision drift")
    require(subject.get("predecessor") == expected_v4_predecessor(), "Story 4.15 v4 review predecessor drift")
    require(subject.get("headAncestry") == identity.get("headAncestry"), "Story 4.15 v4 review HEAD ancestry drift")
    require(subject.get("sourceTransitions") == identity.get("sourceTransitions"), "Story 4.15 v4 review source transition drift")
    require(subject.get("gateInputs") == identity.get("gateInputs"), "Story 4.15 v4 review gate-input drift")
    require(
        subject.get("bindings")
        == {
            "sourceIdentity": {"path": "source-artifact-identity.json", "sha256": sha256_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / "source-artifact-identity.json"))},
            "limitations": {"path": "limitations.json", "sha256": limitations_sha256},
            "validatorRecord": {
                "path": "validator-sha256.txt",
                "sha256": sha256_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / "validator-sha256.txt")),
                "validatorPath": "tools/validate-oq8-platform-evidence.py",
                "validatorSha256": validator_sha256,
            },
            "preReviewExecution": {"path": "pre-review-execution.json", "sha256": execution_sha256},
        },
        "Story 4.15 v4 review candidate binding drift",
    )
    require(
        subject.get("requiredReviews")
        == [
            {"role": role, "reviewer": REVIEW_ROSTER[role], "scope": V4_REVIEW_SCOPES[role], "status": "required"}
            for role in ("architecture", "security", "test")
        ],
        "Story 4.15 v4 required review roster or scope drift",
    )
    validate_authority(subject.get("authority"))
    return sha256_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / "review-subject.json")), frozen_at


def validate_v4_reviews(
    subject_sha256: str,
    limitations_sha256: str,
    frozen_at: datetime,
    snapshots: dict[str, bytes],
    now: datetime,
) -> tuple[dict[str, str], list[datetime]]:
    receipts: dict[str, str] = {}
    issued_at_values: list[datetime] = []
    for role, reviewer in REVIEW_ROSTER.items():
        path = V4_SUCCESSOR / "reviews" / f"{role}.json"
        document = v4_snapshot_json(snapshots, f"reviews/{role}.json")
        require(isinstance(document, dict), f"Story 4.15 v4 {role} review must be an object")
        require(
            set(document)
            == ({"schema", "role", "reviewer", "issuedAt", "decision", "subjectSha256", "limitationsSha256", "acceptedScope", "findings", "authority"} | ({"verification"} if role == "test" else set())),
            f"Story 4.15 v4 {role} review field set drift",
        )
        require(document.get("schema") == "hexalith.eventstore.story-4-15-successor-review-receipt/v4", f"Story 4.15 v4 {role} review schema drift")
        require(document.get("role") == role, f"Story 4.15 v4 {role} review role drift")
        require(document.get("reviewer") == reviewer, f"Story 4.15 v4 {role} reviewer identity drift")
        issued_at = validate_successor_timestamp(document.get("issuedAt"), f"{role} receipt", now, "v4")
        require(issued_at > frozen_at, f"Story 4.15 v4 {role} review predates the frozen subject")
        issued_at_values.append(issued_at)
        require(document.get("decision") == "approved", f"Story 4.15 v4 {role} review is not approved")
        require(document.get("subjectSha256") == subject_sha256, f"Story 4.15 v4 {role} review subject drift")
        require(document.get("limitationsSha256") == limitations_sha256, f"Story 4.15 v4 {role} review limitations drift")
        require(document.get("acceptedScope") == V4_REVIEW_SCOPES[role], f"Story 4.15 v4 {role} review scope drift")
        findings = document.get("findings")
        require(
            isinstance(findings, list) and findings and all(isinstance(finding, str) and finding.strip() for finding in findings),
            f"Story 4.15 v4 {role} review findings are missing or blank",
        )
        validate_authority(document.get("authority"))
        if role == "test":
            verification = document.get("verification")
            require(
                isinstance(verification, list) and len(verification) == len(V4_TEST_RECEIPT_VERIFICATION),
                "Story 4.15 v4 test review verification set drift",
            )
            for command, expected in zip(verification, V4_TEST_RECEIPT_VERIFICATION, strict=True):
                validate_successor_passed_command(command, expected, "test review verification", "v4")
        receipts[role] = sha256_bytes(v4_snapshot(snapshots, path))
    return receipts, issued_at_values


def validate_v4_handoff(
    document: Any,
    snapshots: dict[str, bytes],
    subject_sha256: str,
    limitations_sha256: str,
    receipts: dict[str, str],
    receipt_times: list[datetime],
    now: datetime,
) -> str:
    require(isinstance(document, dict), "Story 4.15 v4 handoff must be an object")
    require(
        set(document) == {"schema", "assembledAt", "story", "predecessor", "reviewSubjectSha256", "limitationsSha256", "reviewReceipts", "consumerInstructions", "authority"},
        "Story 4.15 v4 handoff field set drift",
    )
    require(document.get("schema") == "hexalith.eventstore.story-4-15-successor-source-only-handoff/v4", "Story 4.15 v4 handoff schema drift")
    assembled_at = validate_successor_timestamp(document.get("assembledAt"), "handoff assembly", now, "v4")
    require(receipt_times and assembled_at > max(receipt_times), "Story 4.15 v4 handoff predates a review receipt")
    require(document.get("story") == "4.15", "Story 4.15 v4 handoff story drift")
    require(document.get("predecessor") == expected_v4_predecessor(), "Story 4.15 v4 handoff predecessor drift")
    require(document.get("reviewSubjectSha256") == subject_sha256, "Story 4.15 v4 handoff subject drift")
    require(document.get("limitationsSha256") == limitations_sha256, "Story 4.15 v4 handoff limitations drift")
    require(document.get("reviewReceipts") == receipts, "Story 4.15 v4 handoff receipt set drift")
    require(
        document.get("consumerInstructions")
        == {
            "mode": "source-only",
            "installCommand": V4_CONSUMER_INSTALL_COMMAND,
            "verifyCommand": ".oq8-python/bin/python tools/validate-oq8-platform-evidence.py",
            "historicalRule": V4_CONSUMER_HISTORICAL_RULE,
            "currentRule": V4_CONSUMER_CURRENT_RULE,
        },
        "Story 4.15 v4 consumer instructions drift",
    )
    validate_authority(document.get("authority"))
    return sha256_bytes(v4_snapshot(snapshots, V4_SUCCESSOR / "source-only-handoff.json"))


def validate_v4_successor(
    snapshots: dict[str, bytes] | None = None,
    now: datetime | None = None,
) -> tuple[dict[str, str], str, str, str, str]:
    current_snapshots = capture_v4_snapshots() if snapshots is None else snapshots
    current_utc = datetime.now(timezone.utc) if now is None else now
    require(current_utc.tzinfo is not None, "Story 4.15 v4 current UTC must be timezone-aware")
    manifest = validate_v4_manifest(current_snapshots)
    manifest_sha256 = sha256_bytes(v4_snapshot(current_snapshots, V4_SUCCESSOR / "closure-sha256.txt"))
    identity = validate_v4_source_identity(current_snapshots)
    identity_sha256 = sha256_bytes(v4_snapshot(current_snapshots, V4_SUCCESSOR / "source-artifact-identity.json"))
    limitations_sha256 = validate_v4_limitations(current_snapshots)
    validator_sha256 = validate_v4_validator_identity(current_snapshots)
    execution_sha256, execution_at = validate_v4_pre_review_execution(
        v4_snapshot_json(current_snapshots, "pre-review-execution.json"),
        identity,
        current_snapshots,
        current_utc,
    )
    subject_sha256, frozen_at = validate_v4_review_subject(
        v4_snapshot_json(current_snapshots, "review-subject.json"),
        identity,
        limitations_sha256,
        validator_sha256,
        execution_sha256,
        execution_at,
        current_snapshots,
        current_utc,
    )
    receipts, receipt_times = validate_v4_reviews(
        subject_sha256,
        limitations_sha256,
        frozen_at,
        current_snapshots,
        current_utc,
    )
    handoff_sha256 = validate_v4_handoff(
        v4_snapshot_json(current_snapshots, "source-only-handoff.json"),
        current_snapshots,
        subject_sha256,
        limitations_sha256,
        receipts,
        receipt_times,
        current_utc,
    )
    return manifest, manifest_sha256, identity_sha256, subject_sha256, handoff_sha256


def validate_limitations(document: Any) -> dict[str, Any]:
    require(isinstance(document, dict), "Closure limitations must be an object")
    require(
        document == {
            "schema": "hexalith.eventstore.story-4-15-limitations/v1",
            "limitations": EXPECTED_LIMITATIONS,
        },
        "Closure limitation text or order drift",
    )
    return document


def validate_review_subject(subject: dict[str, Any], crosswalk: dict[str, Any], identity: dict[str, Any], limitations: dict[str, Any]) -> str:
    require(isinstance(subject, dict), "Review subject must be an object")
    require(
        set(subject) == {
            "schema",
            "createdOn",
            "proposedDecision",
            "design",
            "bindings",
            "identity",
            "reviewedPublicDocs",
            "handoff",
            "limitations",
            "requiredReviews",
            "authority",
        },
        "Review subject field set drift",
    )
    require(subject.get("schema") == "hexalith.eventstore.story-4-15-review-subject/v1", "Review subject schema drift")
    require(subject.get("createdOn") == CURRENT_REVIEW_DATE, "Review subject date drift")
    require(subject.get("proposedDecision") == "eventstore-platform-complete", "Review subject decision drift")
    require(subject.get("design") == crosswalk.get("design"), "Review subject design binding drift")
    bindings = subject.get("bindings", {})
    expected_bindings = {
        "capturePacketV1": ("capture-packet-v1.json", CLOSURE / "capture-packet-v1.json"),
        "closureCrosswalk": ("closure-crosswalk.json", CLOSURE / "closure-crosswalk.json"),
        "sourceArtifactIdentity": ("source-artifact-identity.json", CLOSURE / "source-artifact-identity.json"),
        "limitations": ("limitations.json", CLOSURE / "limitations.json"),
        "closureValidator": ("validator-sha256.txt", CLOSURE / "validator-sha256.txt"),
        "validatorRequirements": ("requirements-oq8.txt", None),
        "ciWorkflow": (".github/workflows/ci.yml", None),
        "integrationWorkflow": (".github/workflows/integration.yml", None),
        "workflowGuardrailTests": (
            "tests/Hexalith.EventStore.Contracts.Tests/Packaging/ReleasePackageManifestTests.cs",
            None,
        ),
        "closureTests": (CLOSURE_TEST_SOURCE, None),
        "preReviewExecution": (PRE_REVIEW_EXECUTION, CLOSURE / PRE_REVIEW_EXECUTION),
        "captureManifest": (f"{EVIDENCE_DIRECTORY}/evidence-sha256.txt", EVIDENCE / "evidence-sha256.txt"),
    }
    require(set(bindings) == set(expected_bindings), "Review subject binding set drift")
    run_git("merge-base", "--is-ancestor", LANDED_SOURCE, COMPLETED_V1_CLOSURE_COMMIT)
    for name, (relative, path) in expected_bindings.items():
        if relative in PRIOR_ROOT_BINDING_HASHES:
            expected_sha256 = PRIOR_ROOT_BINDING_HASHES[relative]
            require(
                sha256_git_file(COMPLETED_V1_CLOSURE_COMMIT, relative) == expected_sha256,
                f"Review subject historical binding drift: {name}",
            )
        else:
            require(path is not None, f"Review subject binding source is missing: {name}")
            expected_sha256 = sha256_file(path)
        require(bindings.get(name) == {"path": relative, "sha256": expected_sha256}, f"Review subject binding drift: {name}")
    require(
        subject.get("identity") == {
            "repository": "Hexalith/Hexalith.EventStore",
            "landedSourceCommit": LANDED_SOURCE,
            "landedSourceTree": LANDED_TREE,
            "boundPathCount": 26,
            "captureArtifactCount": 7,
        },
        "Review subject identity drift",
    )
    for relative, expected in EXPECTED_DOCUMENT_HASHES.items():
        require(
            sha256_git_file(COMPLETED_V1_CLOSURE_COMMIT, relative) == expected,
            f"Reviewed public document historical identity drift: {relative}",
        )
    require(subject.get("reviewedPublicDocs") == EXPECTED_DOCUMENT_HASHES, "Review subject public-document binding drift")
    require(
        subject.get("handoff") == {
            "schema": "hexalith.eventstore.story-4-15-source-only-handoff/v1",
            "story": "4.15",
            "landedSourceCommit": LANDED_SOURCE,
            "consumerInstructions": EXPECTED_CONSUMER_INSTRUCTIONS,
        },
        "Review subject handoff semantics drift",
    )
    require(subject.get("limitations") == limitations.get("limitations"), "Review subject limitations drift")
    reviews = subject.get("requiredReviews", [])
    require(
        reviews == [
            {"role": role, "reviewer": REVIEW_ROSTER[role], "scope": REVIEW_SCOPES[role], "status": "required"}
            for role in ("architecture", "security", "test")
        ],
        "Required reviewer roster or scope drift",
    )
    validate_authority(subject.get("authority"))
    return sha256_file(CLOSURE / "review-subject.json")


def validate_reviews(subject_sha256: str, limitations_sha256: str) -> dict[str, str]:
    receipts: dict[str, str] = {}
    for role, reviewer in REVIEW_ROSTER.items():
        path = CLOSURE / "reviews" / f"{role}.json"
        document = load_candidate_json(path)
        require(isinstance(document, dict), f"{role} review must be an object")
        require(
            set(document) == {
                "schema",
                "role",
                "reviewer",
                "reviewedOn",
                "decision",
                "subjectSha256",
                "acceptedScope",
                "acceptedLimitationsSha256",
                "findings",
                "authority",
            },
            f"{role} review field set drift",
        )
        require(document.get("schema") == "hexalith.eventstore.story-4-15-review-receipt/v1", f"{role} review schema drift")
        require(document.get("role") == role, f"{role} review role drift")
        require(document.get("reviewer") == reviewer, f"{role} reviewer identity drift")
        require(document.get("reviewedOn") == CURRENT_REVIEW_DATE, f"{role} review date drift")
        require(document.get("decision") == "approved", f"{role} review is not approved")
        require(document.get("subjectSha256") == subject_sha256, f"{role} review subject drift")
        require(document.get("acceptedScope") == REVIEW_SCOPES[role], f"{role} accepted scope drift")
        require(document.get("acceptedLimitationsSha256") == limitations_sha256, f"{role} limitations acceptance drift")
        findings = document.get("findings", [])
        require(
            isinstance(findings, list)
            and findings
            and all(isinstance(item, str) and item.strip() for item in findings),
            f"{role} review findings missing or blank",
        )
        validate_authority(document.get("authority"))
        receipts[role] = sha256_file(path)
    return receipts


def validate_handoff(
    document: dict[str, Any],
    subject: dict[str, Any],
    subject_sha256: str,
    receipts: dict[str, str],
    limitations_sha256: str,
) -> None:
    require(isinstance(document, dict), "Source-only handoff must be an object")
    require(
        set(document) == {
            "schema",
            "story",
            "landedSourceCommit",
            "reviewSubjectSha256",
            "limitationsSha256",
            "reviewReceipts",
            "consumerInstructions",
            "authority",
        },
        "Source-only handoff field set drift",
    )
    require(document.get("schema") == "hexalith.eventstore.story-4-15-source-only-handoff/v1", "Source-only handoff schema drift")
    require(document.get("story") == "4.15", "Source-only handoff story drift")
    require(document.get("landedSourceCommit") == LANDED_SOURCE, "Source-only handoff commit drift")
    require(document.get("reviewSubjectSha256") == subject_sha256, "Source-only handoff subject drift")
    require(document.get("limitationsSha256") == limitations_sha256, "Source-only handoff limitations drift")
    require(document.get("reviewReceipts") == receipts, "Source-only handoff receipt set drift")
    handoff_subject = subject.get("handoff", {})
    require(document.get("schema") == handoff_subject.get("schema"), "Source-only handoff reviewed schema drift")
    require(document.get("story") == handoff_subject.get("story"), "Source-only handoff reviewed story drift")
    require(document.get("landedSourceCommit") == handoff_subject.get("landedSourceCommit"), "Source-only handoff reviewed commit drift")
    require(document.get("consumerInstructions") == handoff_subject.get("consumerInstructions"), "Consumer instruction set or value drift")
    require(document.get("authority") == subject.get("authority"), "Source-only handoff reviewed authority drift")
    validate_authority(document.get("authority"))


def validate_pyyaml_dependency() -> None:
    dependency_valid = False
    try:
        distribution = importlib.metadata.distribution("PyYAML")
        expected_module = Path(distribution.locate_file("yaml/__init__.py")).resolve(strict=True)
        actual_module = Path(getattr(yaml, "__file__", "")).resolve(strict=True)
        dependency_valid = (
            distribution.version == PINNED_PYYAML_VERSION
            and getattr(yaml, "__version__", None) == PINNED_PYYAML_VERSION
            and actual_module == expected_module
        )
    except Exception:
        dependency_valid = False
    require(
        dependency_valid,
        f"Pinned PyYAML {PINNED_PYYAML_VERSION} dependency is unavailable or untrusted",
    )

    requirement_path = ROOT / "requirements-oq8.txt"
    expected_requirement_lines = [f"{PINNED_PYYAML_REQUIREMENT} \\"]
    for index, digest in enumerate(PINNED_PYYAML_HASHES):
        continuation = " \\" if index < len(PINNED_PYYAML_HASHES) - 1 else ""
        expected_requirement_lines.append(f"    --hash=sha256:{digest}{continuation}")
    requirement = read_bounded_text_snapshot(
        requirement_path,
        MAX_V2_BOUND_SOURCE_BYTES,
        "OQ8 validator dependency requirement",
    )
    require(requirement.splitlines() == expected_requirement_lines, "OQ8 validator dependency requirement drift")
    required_workflow_fragments = (
        'python3 -m venv "${RUNNER_TEMP}/oq8-python"',
        '"${RUNNER_TEMP}/oq8-python/bin/python" -m pip install --require-hashes --no-deps --only-binary=:all: --requirement requirements-oq8.txt',
        'echo "${RUNNER_TEMP}/oq8-python/bin" >> "$GITHUB_PATH"',
    )
    for relative in (".github/workflows/ci.yml", ".github/workflows/integration.yml"):
        workflow = read_bounded_text_snapshot(
            ROOT / relative,
            MAX_V2_BOUND_SOURCE_BYTES,
            f"OQ8 validator dependency bootstrap {relative}",
        )
        require(
            "\n          ".join(required_workflow_fragments) in workflow,
            f"OQ8 validator dependency bootstrap drift: {relative}",
        )


def parse_development_status(document: str) -> dict[str, str]:
    validate_pyyaml_dependency()
    require(
        len(document.encode("utf-8")) <= MAX_SPRINT_STATUS_BYTES,
        "Sprint-status YAML source exceeds the bounded size limit",
    )
    if document.startswith("\ufeff"):
        document = document[1:]
    require("\ufeff" not in document, "Sprint-status BOM is only permitted at stream start")
    require(
        yaml.reader.Reader.NON_PRINTABLE.search(document) is None,
        "Sprint-status YAML source contains forbidden characters",
    )

    class SprintStatusSafeLoader(yaml.SafeLoader):
        def __init__(self, stream: str) -> None:
            super().__init__(stream)
            self.node_properties: dict[int, tuple[bool, bool]] = {}
            self.aliased_nodes: set[int] = set()

        def compose_node(self, parent: Any, index: Any) -> Any:
            is_alias = self.check_event(yaml.events.AliasEvent)
            event = self.peek_event()
            node = super().compose_node(parent, index)
            if is_alias:
                self.aliased_nodes.add(id(node))
            else:
                self.node_properties[id(node)] = (
                    event.anchor is not None,
                    event.tag is not None,
                )
            return node

    loader = SprintStatusSafeLoader(document)
    try:
        root = loader.get_single_node()
    except yaml.composer.ComposerError as error:
        if error.context == "expected a single document in the stream":
            fail("Sprint-status YAML stream must contain exactly one document")
        fail("Unsupported sprint-status mapping structure")
    except yaml.YAMLError:
        fail("Unsupported sprint-status mapping structure")
    finally:
        loader.dispose()

    mapping_tag = yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG
    scalar_tag = yaml.resolver.BaseResolver.DEFAULT_SCALAR_TAG
    require(
        isinstance(root, yaml.nodes.MappingNode) and root.tag == mapping_tag,
        "Unsupported sprint-status mapping structure",
    )

    def has_properties(node: Any) -> bool:
        anchored, explicitly_tagged = loader.node_properties.get(id(node), (False, False))
        return anchored or explicitly_tagged or id(node) in loader.aliased_nodes

    require(not has_properties(root), "Unsupported sprint-status mapping structure")

    def contains_nested_lifecycle_mapping(node: Any, visited: set[int]) -> bool:
        if id(node) in visited:
            return False
        visited.add(id(node))
        if isinstance(node, yaml.nodes.MappingNode):
            for key_node, value_node in node.value:
                if isinstance(key_node, yaml.nodes.ScalarNode) and key_node.value == "development_status":
                    return True
                if contains_nested_lifecycle_mapping(key_node, visited):
                    return True
                if contains_nested_lifecycle_mapping(value_node, visited):
                    return True
        elif isinstance(node, yaml.nodes.SequenceNode):
            return any(contains_nested_lifecycle_mapping(item, visited) for item in node.value)
        return False

    root_entries: dict[str, tuple[Any, Any]] = {}
    lifecycle_entries: list[tuple[Any, Any]] = []
    for key_node, value_node in root.value:
        require(isinstance(key_node, yaml.nodes.ScalarNode), "Unsupported sprint-status mapping structure")
        key = key_node.value
        require(key_node.tag != "tag:yaml.org,2002:merge", "Sprint-status merge keys are forbidden")
        if key in root_entries:
            if key == "development_status":
                fail("Lifecycle development_status mapping is missing or ambiguous")
            fail("Sprint-status root mapping contains a duplicate key")
        root_entries[key] = (key_node, value_node)
        if key == "development_status":
            lifecycle_entries.append((key_node, value_node))
    for key, (_, value_node) in root_entries.items():
        if key != "development_status":
            require(
                not contains_nested_lifecycle_mapping(value_node, set()),
                "Unsupported sprint-status mapping structure",
            )

    require(len(lifecycle_entries) == 1, "Lifecycle development_status mapping is missing or ambiguous")
    lifecycle_key, lifecycle_mapping = lifecycle_entries[0]
    require(
        lifecycle_key.tag == scalar_tag and not has_properties(lifecycle_key),
        "Unsupported sprint-status mapping structure",
    )
    require(
        isinstance(lifecycle_mapping, yaml.nodes.MappingNode)
        and lifecycle_mapping.tag == mapping_tag
        and lifecycle_mapping.flow_style is False
        and not has_properties(lifecycle_mapping),
        "Unsupported sprint-status mapping structure",
    )

    retired_key = "4-8-durable-admission-evidence-ledger"
    bounded_lifecycle_keys = {
        "epic-4",
        "4-9-trusted-admission-contract-and-protected-identity",
        "4-10-digest-directory-rotation-and-key-retirement",
        "4-11-admission-state-machine-and-current-fence-enforcement",
        "4-12-expiry-compaction-and-tombstone-retention",
        "4-13-legacy-admission-migration-and-fail-closed-reconciliation",
        "4-14-oq8-multi-host-production-evidence",
        "4-15-oq8-platform-closure-and-handoff",
    }
    for key_node, _ in lifecycle_mapping.value:
        if isinstance(key_node, yaml.nodes.ScalarNode) and key_node.value == retired_key:
            fail(f"Retired lifecycle key is forbidden: {retired_key}")
        if isinstance(key_node, yaml.nodes.ScalarNode):
            require(key_node.tag != "tag:yaml.org,2002:merge", "Sprint-status merge keys are forbidden")

    unique_keys: set[str] = set()
    for key_node, _ in lifecycle_mapping.value:
        require(
            isinstance(key_node, yaml.nodes.ScalarNode),
            "Unsupported sprint-status mapping structure",
        )
        key = key_node.value
        if key in unique_keys:
            if key in bounded_lifecycle_keys:
                fail(f"Lifecycle status is missing or ambiguous: {key}")
            fail("Lifecycle status mapping contains a duplicate key")
        unique_keys.add(key)

    entries: dict[str, str] = {}
    for key_node, value_node in lifecycle_mapping.value:
        key = key_node.value
        require(
            key_node.tag == scalar_tag and not has_properties(key_node),
            "Unsupported sprint-status mapping structure",
        )
        require(
            isinstance(value_node, yaml.nodes.ScalarNode)
            and value_node.tag == scalar_tag
            and value_node.style in (None, "'", '"')
            and not has_properties(value_node),
            "Unsupported sprint-status mapping structure",
        )
        entries[key] = value_node.value
    return entries


def require_unique_sprint_status(statuses: dict[str, str], key: str, expected: str) -> None:
    require(key in statuses, f"Lifecycle status is missing or ambiguous: {key}")
    require(statuses[key] == expected, f"Lifecycle status drift: {key}")


def parse_unique_frontmatter_status(path: Path, story: str) -> str:
    text = read_text(path)
    lines = text.splitlines()
    require(lines and lines[0] == "---", f"Malformed Story {story} frontmatter")
    closing = next((index for index, line in enumerate(lines[1:], start=1) if line == "---"), None)
    require(closing is not None, f"Malformed Story {story} frontmatter")
    matches: list[str] = []
    for line in lines[1:closing]:
        match = re.fullmatch(
            r"status:\s*(?:(?P<quote>['\"])(?P<quoted>[a-z-]+)(?P=quote)|(?P<plain>[a-z-]+))\s*",
            line,
        )
        if match is not None:
            matches.append(match.group("quoted") or match.group("plain"))
    require(len(matches) == 1, f"Story {story} frontmatter status is missing or ambiguous")
    return matches[0]


def validate_document_semantics(relative: str) -> None:
    text = read_text(ROOT / relative)
    require(text.count(EXPECTED_DOCUMENT_MARKER) == 1, f"OQ8 source-only handoff marker is missing or ambiguous: {relative}")
    for required in DOCUMENT_REQUIRED_TEXT:
        require(required in text, f"OQ8 source-only handoff semantics missing from {relative}: {required}")
    for forbidden in DOCUMENT_FORBIDDEN_TEXT:
        require(forbidden not in text, f"Stale OQ8 handoff state remains in {relative}: {forbidden}")


def validate_status_and_documents(*, phase: str) -> None:
    require(phase in {"candidate", "final", "closed"}, "Unsupported Story 4.15 lifecycle phase")
    sprint = read_bounded_text_snapshot(
        ROOT / "_bmad-output/implementation-artifacts/sprint-status.yaml",
        MAX_SPRINT_STATUS_BYTES,
        "Sprint-status YAML source",
    )
    statuses = parse_development_status(sprint)
    expected_statuses = {
        "epic-4": "in-progress",
        "4-9-trusted-admission-contract-and-protected-identity": "done",
        "4-10-digest-directory-rotation-and-key-retirement": "done",
        "4-11-admission-state-machine-and-current-fence-enforcement": "done",
        "4-12-expiry-compaction-and-tombstone-retention": "done",
        "4-13-legacy-admission-migration-and-fail-closed-reconciliation": "done",
        "4-14-oq8-multi-host-production-evidence": "done",
        "4-15-oq8-platform-closure-and-handoff": {
            "candidate": "in-progress",
            "final": "review",
            "closed": "done",
        }[phase],
    }
    for key, expected in expected_statuses.items():
        if key == "4-15-oq8-platform-closure-and-handoff":
            require(key in statuses, f"Lifecycle status is missing or ambiguous: {key}")
            require(statuses[key] == expected, "Story 4.15 lifecycle drift: sprint status does not match lifecycle state")
        else:
            require_unique_sprint_status(statuses, key, expected)

    story_specs = {
        "4.11": ROOT / "_bmad-output/implementation-artifacts/spec-4-11-admission-state-machine-and-current-fence-enforcement.md",
        "4.12": ROOT / "_bmad-output/implementation-artifacts/spec-4-12-expiry-compaction-and-tombstone-retention.md",
        "4.13": ROOT / "_bmad-output/implementation-artifacts/spec-4-13-legacy-admission-migration-and-fail-closed-reconciliation.md",
        "4.14": ROOT / "_bmad-output/implementation-artifacts/spec-4-14-oq8-multi-host-production-evidence.md",
        "4.15": ROOT / "_bmad-output/implementation-artifacts/spec-4-15-oq8-platform-closure-and-handoff.md",
    }
    for story, path in story_specs.items():
        status = parse_unique_frontmatter_status(path, story)
        expected = "done" if phase != "candidate" or story != "4.15" else "in-review"
        if story == "4.15":
            require(status == expected, "Story 4.15 lifecycle drift: spec status does not match lifecycle state")
        else:
            require(status == expected, f"Story {story} metadata status drift")

    for relative in EXPECTED_DOCUMENTS:
        validate_document_semantics(relative)


def validate_lifecycle_state(
    expected_state: str | None = None,
    *,
    selector: dict[str, Any] | None = None,
    expected_manifest_sha256: str | None = None,
    expected_subject_sha256: str | None = None,
) -> str:
    require(
        (expected_manifest_sha256 is None) == (expected_subject_sha256 is None),
        "Story 4.15 lifecycle drift: expected v4 identities must be supplied together",
    )
    require_no_symlink_components(LIFECYCLE_STATE, "Story 4.15 lifecycle state record")
    record = load_json_bytes(
        read_bounded_regular_snapshot(
            LIFECYCLE_STATE,
            MAX_V2_ARTIFACT_BYTES,
            "Story 4.15 lifecycle state record",
        ),
        "Story 4.15 lifecycle state record",
    )
    require(isinstance(record, dict), "Story 4.15 lifecycle state record must be an object")
    require(
        set(record)
        == {
            "schema",
            "story",
            "state",
            "successorDirectory",
            "successorManifestSha256",
            "reviewSubjectSha256",
        },
        "Story 4.15 lifecycle state field set drift",
    )
    require(
        record.get("schema") == "hexalith.eventstore.story-4-15-platform-lifecycle-state/v1",
        "Story 4.15 lifecycle state schema drift",
    )
    require(record.get("story") == "4.15", "Story 4.15 lifecycle state story drift")
    state = record.get("state")
    require(state in {"ready-to-close", "closed"}, "Story 4.15 lifecycle drift: unsupported lifecycle state")
    if expected_state is not None:
        require(state == expected_state, "Story 4.15 lifecycle drift: lifecycle mode does not match record state")
    current_selector = load_successor_selector() if selector is None else selector
    selected = current_selector.get("successor")
    require(isinstance(selected, dict), "Story 4.15 lifecycle drift: active successor selection is missing")
    require(
        set(selected)
        == {
            "directory",
            "manifestSha256",
            "files",
            "sourceIdentitySha256",
            "reviewSubjectSha256",
            "handoffSha256",
        },
        "Story 4.15 lifecycle drift: active successor selection field set drift",
    )
    record_manifest_sha256 = require_sha256(
        record.get("successorManifestSha256"),
        "Story 4.15 lifecycle successor manifest identity",
    )
    record_subject_sha256 = require_sha256(
        record.get("reviewSubjectSha256"),
        "Story 4.15 lifecycle review subject identity",
    )
    selected_manifest_sha256 = require_sha256(
        selected.get("manifestSha256"),
        "Story 4.15 selected successor manifest identity",
    )
    selected_subject_sha256 = require_sha256(
        selected.get("reviewSubjectSha256"),
        "Story 4.15 selected successor review subject identity",
    )
    require(
        record.get("successorDirectory") == selected.get("directory")
        and selected.get("directory") in {V4_SUCCESSOR_DIRECTORY, V5_SUCCESSOR_DIRECTORY},
        "Story 4.15 lifecycle drift: successor directory mismatch",
    )
    require(
        record_manifest_sha256 == selected_manifest_sha256,
        "Story 4.15 lifecycle drift: successor manifest mismatch",
    )
    require(
        record_subject_sha256 == selected_subject_sha256,
        "Story 4.15 lifecycle drift: review subject mismatch",
    )
    if expected_manifest_sha256 is not None and expected_subject_sha256 is not None:
        require_sha256(expected_manifest_sha256, "Story 4.15 validated v4 manifest identity")
        require_sha256(expected_subject_sha256, "Story 4.15 validated v4 review subject identity")
        require(
            selected_manifest_sha256 == expected_manifest_sha256,
            "Story 4.15 lifecycle drift: selected manifest does not match validated v4 evidence",
        )
        require(
            selected_subject_sha256 == expected_subject_sha256,
            "Story 4.15 lifecycle drift: selected subject does not match validated v4 evidence",
        )
    return state


def validate_pre_review_candidate() -> None:
    require(EVIDENCE.is_dir(), "OQ8 evidence directory is missing")
    require(CLOSURE.is_dir(), "Story 4.15 closure directory is missing")
    capture_packet = load_candidate_json(CLOSURE / "capture-packet-v1.json")
    validate_capture_packet(capture_packet)
    crosswalk = load_candidate_json(CLOSURE / "closure-crosswalk.json")
    identity = load_candidate_json(CLOSURE / "source-artifact-identity.json")
    limitations = load_candidate_json(CLOSURE / "limitations.json")
    execution = load_candidate_json(CLOSURE / PRE_REVIEW_EXECUTION)
    subject = load_candidate_json(CLOSURE / "review-subject.json")
    validate_validator_identity()
    validate_crosswalk(crosswalk)
    validate_limitations(limitations)
    validate_pre_review_execution(execution)
    validate_review_subject(subject, crosswalk, identity, limitations)
    validate_successor_source_identity()
    validate_successor_selector_sdk_link(load_successor_selector())
    validate_status_and_documents(phase="candidate")


def validate_successor_selector_sdk_link(selector: dict[str, Any]) -> None:
    validate_authority(selector.get("authority"))
    historical = selector.get("historical")
    require(isinstance(historical, dict), "Story 4.15 successor historical selection is missing")
    v1_closure = historical.get("v1Closure")
    require(isinstance(v1_closure, dict), "Story 4.15 successor historical v1 selection is missing")
    require(
        v1_closure.get("packetSha256") == PRIOR_PACKET_SHA256,
        "Story 4.15 successor historical selection drift",
    )
    v2_successor = historical.get("v2Successor")
    require(isinstance(v2_successor, dict), "Story 4.15 successor historical v2 selection is missing")
    require(
        v2_successor.get("manifestSha256") == V2_CLOSURE_MANIFEST_SHA256,
        "Story 4.15 successor historical selection drift",
    )
    sdk_successor = historical.get("sdkSuccessor")
    require(isinstance(sdk_successor, dict), "Story 4.15 successor historical SDK selection is missing")
    require(sdk_successor.get("directory") == SUCCESSOR_DIRECTORY, "Story 4.15 successor directory selection drift")
    files = sdk_successor.get("files")
    require(
        isinstance(files, dict)
        and files.get("source-artifact-identity.json") == sha256_file(SUCCESSOR / "source-artifact-identity.json"),
        "Story 4.15 successor source identity selection drift",
    )


def validate_pre_review_execution(document: Any) -> None:
    require(isinstance(document, dict), "Pre-review execution record must be an object")
    require(
        set(document)
        == {"schema", "executedOn", "scope", "authority", "validator", "testSource", "finalValidation", "commands", "summary"},
        "Pre-review execution field set drift",
    )
    require(document.get("schema") == "hexalith.eventstore.story-4-15-pre-review-execution/v1", "Pre-review execution schema drift")
    require(document.get("executedOn") == CURRENT_REVIEW_DATE, "Pre-review execution date drift")
    require(document.get("scope") == "receipt-independent-isolated-candidate", "Pre-review execution scope drift")
    require(
        document.get("authority") == {
            "reviewReceiptsValidated": False,
            "finalHandoffValidated": False,
            "externalAuthorityClaimed": False,
        },
        "Pre-review execution authority disclosure drift",
    )
    require(
        document.get("validator") == {
            "path": "tools/validate-oq8-platform-evidence.py",
            "sha256": PRIOR_VALIDATOR_SHA256,
        },
        "Pre-review execution validator identity drift",
    )
    require(
        sha256_git_file(COMPLETED_V1_CLOSURE_COMMIT, "tools/validate-oq8-platform-evidence.py")
        == PRIOR_VALIDATOR_SHA256,
        "Pre-review execution historical validator identity drift",
    )
    require(
        document.get("testSource") == {
            "path": CLOSURE_TEST_SOURCE,
            "sha256": PRIOR_ROOT_BINDING_HASHES[CLOSURE_TEST_SOURCE],
        },
        "Pre-review execution test-source identity drift",
    )
    require(
        sha256_git_file(COMPLETED_V1_CLOSURE_COMMIT, CLOSURE_TEST_SOURCE)
        == PRIOR_ROOT_BINDING_HASHES[CLOSURE_TEST_SOURCE],
        "Pre-review execution historical test-source identity drift",
    )
    require(document.get("finalValidation") == PRE_REVIEW_FINAL_VALIDATION, "Pre-review final-validation disclosure drift")
    commands = document.get("commands")
    require(isinstance(commands, list) and len(commands) == len(PRE_REVIEW_COMMAND_RESULTS), "Pre-review execution command set drift")
    expected_names = [expected["name"] for expected in PRE_REVIEW_COMMAND_RESULTS]
    actual_names = [command.get("name") if isinstance(command, dict) else None for command in commands]
    require(
        len(set(expected_names)) == len(expected_names)
        and actual_names == expected_names
        and len(set(actual_names)) == len(actual_names),
        "Pre-review execution command names must be exact and unique",
    )
    for index, expected in enumerate(PRE_REVIEW_COMMAND_RESULTS):
        command = commands[index]
        require(isinstance(command, dict) and set(command) == set(expected), f"Pre-review execution command field set drift: {index}")
        require(isinstance(expected.get("command"), str) and expected["command"].strip(), f"Pre-review expected command identity missing: {index}")
        if "tests" in expected:
            require(
                type(expected["tests"]) is int
                and expected["tests"] > 0
                and expected.get("passed") == expected["tests"]
                and expected.get("failed") == 0
                and expected.get("skipped") == 0,
                f"Pre-review expected test counts are not meaningful: {index}",
            )
        for field, value in expected.items():
            if type(value) is int:
                require_exact_integer(command.get(field), value, f"Pre-review execution command {index}:{field}")
            else:
                require(command.get(field) == value, f"Pre-review execution command drift: {index}:{field}")
    expected_test_count = sum(expected.get("tests", 0) for expected in PRE_REVIEW_COMMAND_RESULTS)
    expected_summary = {
        "commands": len(PRE_REVIEW_COMMAND_RESULTS),
        "successfulCommands": len(PRE_REVIEW_COMMAND_RESULTS),
        "tests": expected_test_count,
        "passed": expected_test_count,
        "failed": 0,
        "skipped": 0,
    }
    summary = document.get("summary")
    require(isinstance(summary, dict) and set(summary) == set(expected_summary), "Pre-review execution summary field set drift")
    for field, value in expected_summary.items():
        require_exact_integer(summary.get(field), value, f"Pre-review execution summary {field}")


def validate_platform_closure(platform: dict[str, Any], *, current_source: bool = True) -> None:
    require(isinstance(platform, dict), "Platform closure must be an object")
    require(CLOSURE.is_dir(), "Story 4.15 closure directory is missing")
    manifest = validate_closure_manifest()
    crosswalk = load_candidate_json(CLOSURE / "closure-crosswalk.json")
    identity = load_candidate_json(CLOSURE / "source-artifact-identity.json")
    limitations = load_candidate_json(CLOSURE / "limitations.json")
    execution = load_candidate_json(CLOSURE / PRE_REVIEW_EXECUTION)
    subject = load_candidate_json(CLOSURE / "review-subject.json")
    validate_crosswalk(crosswalk)
    validate_limitations(limitations)
    validate_pre_review_execution(execution)
    subject_sha256 = validate_review_subject(subject, crosswalk, identity, limitations)
    limitations_sha256 = sha256_file(CLOSURE / "limitations.json")
    receipts = validate_reviews(subject_sha256, limitations_sha256)
    validate_handoff(load_candidate_json(CLOSURE / "source-only-handoff.json"), subject, subject_sha256, receipts, limitations_sha256)
    require(
        set(platform) == {
            "story",
            "status",
            "landedSourceCommit",
            "closureDirectory",
            "closureManifestSha256",
            "closureFiles",
            "reviewSubjectSha256",
            "authority",
        },
        "Platform closure field set drift",
    )
    require(platform.get("story") == "4.15", "Platform closure story drift")
    require(platform.get("status") == "complete", "Platform closure status drift")
    require(platform.get("landedSourceCommit") == LANDED_SOURCE, "Platform closure source drift")
    require(platform.get("closureDirectory") == CLOSURE_DIRECTORY, "Platform closure directory drift")
    require(platform.get("closureManifestSha256") == sha256_file(CLOSURE / "closure-sha256.txt"), "Platform closure manifest drift")
    require(platform.get("closureFiles") == manifest, "Platform closure file identities drift")
    require(platform.get("reviewSubjectSha256") == subject_sha256, "Platform closure subject drift")
    validate_authority(platform.get("authority"))
    historical_successor_manifest = validate_successor_closure()
    selector = load_successor_selector()
    validate_successor_selector_historical(selector, manifest, historical_successor_manifest)
    if current_source:
        validate_v2_successor()
        validate_historical_v3_successor()
        if selector.get("schema") == "hexalith.eventstore.story-4-15-successor-selection/v4":
            result = subprocess.run(
                [sys.executable, str(ROOT / "tools/oq8-v5-packet.py"), "--validate-active"],
                cwd=ROOT,
                capture_output=True,
                timeout=240,
            )
            require(result.returncode == 0, "Story 4.15 v5 reviewed packet, selector, or lifecycle validation failed")
            validate_status_and_documents(phase="closed")
            return
        v4_manifest, v4_manifest_sha256, identity_sha256, subject_sha256, handoff_sha256 = validate_v4_successor()
        validate_successor_selector(
            selector,
            v4_manifest,
            v4_manifest_sha256,
            subject_sha256,
            identity_sha256,
            handoff_sha256,
        )
        lifecycle_state = validate_lifecycle_state(
            selector=selector,
            expected_manifest_sha256=v4_manifest_sha256,
            expected_subject_sha256=subject_sha256,
        )
        validate_status_and_documents(phase="final" if lifecycle_state == "ready-to-close" else "closed")


def validate_capture_packet(packet: Any) -> None:
    require(isinstance(packet, dict), "Capture packet must be an object")
    require_exact_fields(
        packet,
        {"schemaVersion", "story", "design", "profile", "baselineCommit", "capturedOn", "evidenceDirectory", "evidenceFiles", "manifestSha256", "matrix", "closureClaimed", "releaseApproved", "story415Status"},
        "Capture packet",
    )
    require_exact_integer(packet.get("schemaVersion"), 1, "Packet schemaVersion")
    require(packet.get("story") == "4.14", "Packet story drift")
    require(packet.get("design") == {"version": DESIGN_VERSION, "sha256": DESIGN_SHA256}, "OQ8 design identity drift")
    require(packet.get("profile") == PROFILE, "Packet profile drift")
    require(packet.get("baselineCommit") == BASELINE, "Packet baseline drift")
    require(packet.get("evidenceDirectory") == EVIDENCE_DIRECTORY, "Packet evidence directory drift")
    require(packet.get("matrix") == {
        "writersFailover": "passed",
        "expiryCompaction": "passed",
        "authorityChange": "passed",
        "deterministicSupport": "passed",
        "diagnosticLeakage": "passed",
        "sanitizedCapture": "passed",
    }, "Packet matrix is not the exact six-entry passed set")
    require(packet.get("closureClaimed") is False, "Packet claims closure")
    require(packet.get("releaseApproved") is False, "Packet claims release approval")
    require(packet.get("story415Status") == "backlog", "Packet advances Story 4.15")

    manifest = validate_manifest()
    evidence_files = packet.get("evidenceFiles", {})
    require(evidence_files == manifest, "Packet evidence identities do not match the manifest")
    require_sha256(packet.get("manifestSha256"), "Manifest identity")
    require(packet["manifestSha256"] == sha256_file(EVIDENCE / "evidence-sha256.txt"), "Packet manifest identity drift")

    observations = validate_observations(
        EVIDENCE / "observations.json",
        COMMITTED_DAPR_RUNTIME_VERSION,
        POSTGRES_TAG,
        COMPLETED_V1_CLOSURE_COMMIT,
        historical=True,
    )
    deterministic_support_path = EVIDENCE / "deterministic-support.json"
    deterministic_support = validate_support_document(
        load_bounded_json(
            deterministic_support_path,
            MAX_CANDIDATE_JSON_BYTES,
            "Story 4.14 deterministic support",
        )
    )
    require(
        observations["observations"]["authority_change"]["deterministicSupportOracles"]
        == deterministic_support["selectors"],
        "Observation and deterministic support oracle identities drifted",
    )
    source_state = load_bounded_json(
        EVIDENCE / "source-state.json",
        MAX_CANDIDATE_JSON_BYTES,
        "Story 4.14 source state",
    )
    identity = load_candidate_json(CLOSURE / "source-artifact-identity.json")
    validate_source_state(source_state, identity)
    environment = load_bounded_json(
        EVIDENCE / "environment.json",
        MAX_CANDIDATE_JSON_BYTES,
        "Story 4.14 environment",
    )
    require_exact_fields(environment, {"schemaVersion", "capturedOn", "runtime", "profile", "executionConfiguration", "artifacts", "limits"}, "Environment")
    require_exact_integer(environment.get("schemaVersion"), 1, "Environment schemaVersion")
    require_exact_fields(environment.get("runtime"), {"dotnet", "dapr", "postgresImage", "postgresImageIdentity"}, "Environment runtime")
    require_exact_fields(environment.get("profile"), {"name", "stateStoreType", "stateComponentSha256", "resiliencySha256"}, "Environment profile")
    require_exact_fields(environment.get("executionConfiguration"), {"shippedReleaseEntryAssemblies", "shadowCopiedBeforeLaunch", "environmentName", "testOnlyHostingStartup", "productionConfigurationUntouched", "seams"}, "Environment execution configuration")
    require_exact_fields(environment.get("artifacts"), {"eventStoreSha256", "sampleSha256", "eventStoreRuntimeSetSha256", "sampleRuntimeSetSha256", "hostingStartupSha256", "additionalDepsSha256"}, "Environment runtime artifacts")
    require(identity.get("runtimeArtifacts") == environment.get("artifacts"), "Closure runtime artifact identities drift")
    require(packet.get("capturedOn") == observations.get("capturedOn"), "Packet capture date crosswalk drift")
    require(environment.get("runtime") == observations.get("runtime"), "Runtime identity crosswalk drift")
    require(environment.get("profile") == observations.get("profile"), "Profile identity crosswalk drift")
    require(environment.get("executionConfiguration") == observations.get("executionConfiguration"), "Execution-configuration disclosure crosswalk drift")
    require(environment.get("artifacts") == observations.get("artifacts"), "Runtime artifact identity crosswalk drift")
    require(environment.get("capturedOn") == observations.get("capturedOn"), "Capture date crosswalk drift")
    state_component_sha256 = sha256_git_file(
        COMPLETED_V1_CLOSURE_COMMIT,
        "deploy/dapr/statestore-postgresql.yaml",
    )
    resiliency_sha256 = sha256_git_file(
        COMPLETED_V1_CLOSURE_COMMIT,
        "deploy/dapr/resiliency.yaml",
    )
    require(
        observations["profile"]["stateComponentSha256"]
        == state_component_sha256
        == source_state["sourceInputs"]["deploy/dapr/statestore-postgresql.yaml"],
        "PostgreSQL component identity crosswalk drift",
    )
    require(
        observations["profile"]["resiliencySha256"]
        == resiliency_sha256
        == source_state["sourceInputs"]["deploy/dapr/resiliency.yaml"],
        "Resiliency identity crosswalk drift",
    )
    limits = environment.get("limits", {})
    require_exact_fields(limits, {"healthTimeoutSeconds", "nodeReadinessOverallTimeoutSeconds", "actorRuntimeReadinessRequired", "requestTimeoutSeconds", "diagnosticLogCharactersPerStream", "diagnosticStreamsScanned", "forbiddenTermClassesScanned", "rawDiagnosticsCommitted", "postgresqlProjection", "rawPostgresqlValuesCommitted"}, "Environment limits")
    require_exact_integer(limits.get("healthTimeoutSeconds"), 60, "Environment healthTimeoutSeconds")
    require_exact_integer(limits.get("nodeReadinessOverallTimeoutSeconds"), 60, "Environment nodeReadinessOverallTimeoutSeconds")
    require(limits.get("actorRuntimeReadinessRequired") is True, "Environment actor-runtime readiness requirement drift")
    require_exact_integer(limits.get("requestTimeoutSeconds"), 30, "Environment requestTimeoutSeconds")
    require_exact_integer(limits.get("diagnosticLogCharactersPerStream"), 32768, "Environment diagnosticLogCharactersPerStream")
    require_exact_integer(limits.get("diagnosticStreamsScanned"), 12, "Environment diagnosticStreamsScanned")
    require_exact_integer(limits.get("forbiddenTermClassesScanned"), len(DIAGNOSTIC_FORBIDDEN_CLASSES), "Environment forbiddenTermClassesScanned")
    require(limits.get("rawDiagnosticsCommitted") is False, "Environment permits committed raw diagnostics")
    require(
        limits.get("postgresqlProjection")
        == "row counts, state-shape counts, schema hash, projection hash, invariant results",
        "Environment PostgreSQL projection disclosure drift",
    )
    require(limits.get("rawPostgresqlValuesCommitted") is False, "Environment permits committed raw PostgreSQL values")
    test_results = load_bounded_json(
        EVIDENCE / "test-results.json",
        MAX_CANDIDATE_JSON_BYTES,
        "Story 4.14 focused test result",
    )
    validate_focused_document(test_results)
    commands = load_bounded_json(
        EVIDENCE / "commands.json",
        MAX_CANDIDATE_JSON_BYTES,
        "Story 4.14 verification commands",
    )
    require_exact_fields(commands, {"schemaVersion", "capturedOn", "commands"}, "Verification commands")
    require_exact_integer(commands.get("schemaVersion"), 1, "Verification commands schemaVersion")
    require(isinstance(commands.get("commands"), list), "Verification commands must be a list")
    for index, command in enumerate(commands["commands"]):
        require_exact_fields(command, {"name", "command", "exitCode", "counts"}, f"Verification command {index}")
        require(isinstance(command.get("counts"), dict), f"Verification command {index} counts must be an object")
    require(commands.get("capturedOn") == observations.get("capturedOn"), "Command record capture date crosswalk drift")
    command_names = [item.get("name") for item in commands.get("commands", [])]
    require(
        len(command_names) == len(EXPECTED_CAPTURE_COMMAND_RESULTS)
        and len(set(command_names)) == len(command_names)
        and set(command_names) == set(EXPECTED_CAPTURE_COMMAND_RESULTS),
        "Verification command names must be exact and unique",
    )
    command_records = {item["name"]: item for item in commands["commands"]}
    for name, expected in EXPECTED_CAPTURE_COMMAND_RESULTS.items():
        record = command_records[name]
        require_exact_integer(record.get("exitCode"), 0, f"Verification command {name}:exitCode")
        require(record.get("command") == expected["command"], f"Verification command identity drift: {name}")
        counts = record.get("counts")
        require(isinstance(counts, dict) and set(counts) == set(expected["counts"]), f"Verification command count field set drift: {name}")
        for field, value in expected["counts"].items():
            require_exact_integer(counts.get(field), value, f"Verification command {name}:{field}")
    require(
        command_records["focused-production-matrix"].get("command") == test_results.get("command"),
        "Recorded focused method command drift",
    )
    require(
        command_records["explicit-deterministic-support-oracles"].get("command")
        == deterministic_support.get("command"),
        "Recorded deterministic support command drift",
    )
    require(
        command_records["explicit-deterministic-support-oracles"].get("counts")
        == {"methods": len(EXPECTED_SUPPORT_METHOD_CASES), "passed": SUPPORT_CASE_TOTAL, "failed": 0, "skipped": 0},
        "Recorded deterministic support counts drift",
    )
    reviews = load_bounded_json(
        EVIDENCE / "review-records.json",
        MAX_CANDIDATE_JSON_BYTES,
        "Story 4.14 capture review records",
    )
    require_exact_fields(reviews, {"schemaVersion", "records", "releaseApproval", "foldersOq8Closure", "story415Status"}, "Capture review records")
    require_exact_integer(reviews.get("schemaVersion"), 1, "Capture review records schemaVersion")
    require(
        reviews.get("records") == [
            {"kind": "implementation-verification", "performedOn": "2026-08-10", "result": "passed", "authority": "development-verification-only"},
            {"kind": "external-release-authority", "performed": False, "approval": False, "ownedBy": "Story 4.15 and external authority"},
            {"kind": "production-evidence-review", "performed": False, "approval": False, "ownedBy": "Murat"},
            {"kind": "leakage-fence-review", "performed": False, "approval": False, "ownedBy": "Security Reviewer"},
        ],
        "Capture review record set or field drift",
    )
    require(any(
        record == {
            "kind": "production-evidence-review",
            "performed": False,
            "approval": False,
            "ownedBy": "Murat",
        }
        for record in reviews.get("records", [])
    ), "Pending Murat production-evidence review record is missing")
    require(any(
        record == {
            "kind": "leakage-fence-review",
            "performed": False,
            "approval": False,
            "ownedBy": "Security Reviewer",
        }
        for record in reviews.get("records", [])
    ), "Pending Security Reviewer leakage/fence review record is missing")
    require(reviews.get("releaseApproval") is False, "Review record claims release approval")
    require(reviews.get("foldersOq8Closure") is False, "Review record claims Folders OQ8 closure")
    require(reviews.get("story415Status") == "backlog", "Review record advances Story 4.15")


def validate_committed_packet(*, current_source: bool = True) -> None:
    require(EVIDENCE.is_dir(), "OQ8 evidence directory is missing")
    packet_bytes = read_bounded_regular_snapshot(
        PACKET,
        MAX_CANDIDATE_JSON_BYTES,
        "OQ8 closure packet",
    )
    scan_support_safe_text(decode_utf8(packet_bytes, "OQ8 closure packet"), PACKET.name)
    outer_packet = load_json_bytes(packet_bytes, "OQ8 closure packet")
    require(isinstance(outer_packet, dict), "Closure packet must be an object")
    require_exact_integer(outer_packet.get("schemaVersion"), 2, "Closure packet schemaVersion")
    require(set(outer_packet) == {"schemaVersion", "capture", "platformClosure"}, "Closure packet field set drift")
    packet = outer_packet.get("capture", {})
    require(packet == load_candidate_json(CLOSURE / "capture-packet-v1.json"), "Immutable v1 capture packet snapshot drift")
    validate_capture_packet(packet)
    validate_platform_closure(outer_packet.get("platformClosure", {}), current_source=current_source)


def validate_v5_draft_candidate(candidate_path: Path) -> None:
    """Verify review inputs without treating an unapproved v5 draft as a successor."""
    require(
        ROOT == DEFAULT_ROOT and GIT_ROOT == DEFAULT_ROOT,
        "Story 4.15 v5 draft validation requires the live EventStore checkout",
    )
    require(
        candidate_path.resolve() != ROOT and ROOT not in candidate_path.resolve().parents,
        "Story 4.15 v5 draft must be outside the repository",
    )
    candidate = load_bounded_json(
        candidate_path,
        MAX_V5_CANDIDATE_BYTES,
        "Story 4.15 v5 draft candidate",
        repository_bound=False,
    )
    require(isinstance(candidate, dict), "Story 4.15 v5 draft candidate must be an object")
    require(
        candidate.get("schema") == "hexalith.eventstore.oq8-v5-review-candidate/v1"
        and candidate.get("status") == "draft-unapproved",
        "Story 4.15 v5 draft schema or status drift",
    )
    require(
        candidate.get("review")
        == {
            "subjectFrozen": False,
            "subjectSha256": None,
            "architecture": "pending",
            "security": "pending",
            "test": "pending",
        },
        "Story 4.15 v5 draft claims review authority",
    )
    require(
        candidate.get("authority")
        == {
            "currentSourceApproved": False,
            "releaseApproved": False,
            "packageAuthority": False,
            "registryAuthority": False,
        },
        "Story 4.15 v5 draft claims source or publication authority",
    )
    selector = load_successor_selector()
    require(
        selector.get("successor", {}).get("directory") == V4_SUCCESSOR_DIRECTORY,
        "Story 4.15 v5 draft requires the approved v4 selector to remain active",
    )
    result = subprocess.run(
        [sys.executable, str(ROOT / "tools/prepare-oq8-v5-candidate.py")],
        cwd=ROOT,
        capture_output=True,
        timeout=180,
    )
    require(result.returncode == 0, "Story 4.15 v5 draft preparation failed")
    require(
        len(result.stdout) <= MAX_V5_CANDIDATE_BYTES,
        "Story 4.15 v5 freshly prepared draft exceeds the size limit",
    )
    expected = load_json_bytes(result.stdout, "Story 4.15 v5 freshly prepared draft")
    require(candidate == expected, "Story 4.15 v5 draft source or historical identity drift")


def validate_v5_subject_input_draft(path: Path) -> None:
    require(
        ROOT == DEFAULT_ROOT and GIT_ROOT == DEFAULT_ROOT,
        "Story 4.15 v5 draft validation requires the live EventStore checkout",
    )
    result = subprocess.run(
        [sys.executable, str(ROOT / "tools/oq8-v5-packet.py"), "--validate-draft", str(path)],
        cwd=ROOT,
        capture_output=True,
        timeout=240,
    )
    require(result.returncode == 0, "Story 4.15 v5 subject-input draft validation failed")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT, help="Artifact/document root to validate")
    parser.add_argument("--git-root", type=Path, default=DEFAULT_ROOT, help="Git repository used for immutable source proof")
    parser.add_argument("--git-timeout-seconds", type=float, default=30.0, help="Bound each Git identity subprocess")
    parser.add_argument("--pre-review", action="store_true", help="Validate receipt-independent frozen candidate inputs")
    parser.add_argument(
        "--v5-candidate",
        type=Path,
        help="Validate an unapproved v5 draft outside the repository without changing the active v4 gate",
    )
    parser.add_argument(
        "--v5-subject-draft",
        type=Path,
        help="Validate a versioned, receipt-independent v5 subject packet outside the repository",
    )
    parser.add_argument(
        "--historical-v1-only",
        action="store_true",
        help="Validate immutable Story 4.15 v1 evidence without authorizing current source",
    )
    parser.add_argument(
        "--historical-v2-only",
        action="store_true",
        help="Validate immutable Story 4.15 v1 and v2 evidence without authorizing current source",
    )
    parser.add_argument(
        "--historical-v3-only",
        action="store_true",
        help="Validate immutable Story 4.15 v3 evidence without authorizing current source",
    )
    parser.add_argument(
        "--lifecycle-mode",
        choices=("final", "closed"),
        help="Validate one exact lifecycle/document gate in isolation without approving evidence",
    )
    parser.add_argument("--expected-configuration", choices=("Debug", "Release"), default="Release", help="Fresh capture configuration; historical validation always uses Release")
    parser.add_argument("--capture-directory", type=Path, help="Validate one fresh opt-in OQ8 capture")
    parser.add_argument("--ctrf", type=Path, help="Raw CTRF input to sanitize for capture upload")
    parser.add_argument("--support-ctrf", type=Path, help="Raw deterministic-support CTRF input to validate and sanitize")
    parser.add_argument("--support-output", type=Path, help="Write one sanitized deterministic-support document")
    parser.add_argument(
        "--expected-runtime-version",
        action="append",
        help="Exact Dapr runtime version required for one fresh capture",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        configure_roots(args.root, args.git_root, args.git_timeout_seconds)
        if args.v5_candidate is not None:
            require(
                args.v5_subject_draft is None
                and
                not args.pre_review
                and not args.historical_v1_only
                and not args.historical_v2_only
                and not args.historical_v3_only
                and args.lifecycle_mode is None
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "V5 draft mode cannot be combined with another validation mode",
            )
        if args.v5_subject_draft is not None:
            require(
                args.v5_candidate is None
                and not args.pre_review
                and not args.historical_v1_only
                and not args.historical_v2_only
                and not args.historical_v3_only
                and args.lifecycle_mode is None
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "V5 subject-draft mode cannot be combined with another validation mode",
            )
        if args.pre_review:
            require(
                not args.historical_v1_only
                and not args.historical_v2_only
                and not args.historical_v3_only
                and args.lifecycle_mode is None
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "Pre-review mode cannot be combined with capture, support, or lifecycle arguments",
            )
        if args.historical_v1_only:
            require(
                not args.historical_v2_only
                and not args.historical_v3_only
                and args.lifecycle_mode is None
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "Historical-v1 mode cannot be combined with capture, support, or lifecycle arguments",
            )
        if args.historical_v2_only:
            require(
                not args.historical_v3_only
                and args.lifecycle_mode is None
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "Historical-v2 mode cannot be combined with capture, support, or lifecycle arguments",
            )
        if args.historical_v3_only:
            require(
                args.lifecycle_mode is None
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "Historical-v3 mode cannot be combined with capture, support, or lifecycle arguments",
            )
        if args.v5_subject_draft is not None:
            validate_v5_subject_input_draft(args.v5_subject_draft)
            print("OQ8 v5 subject-input draft validated; inactive and unapproved.")
        elif args.v5_candidate is not None:
            validate_v5_draft_candidate(args.v5_candidate)
            print("OQ8 v5 draft candidate inputs validated; inactive and unapproved.")
        elif args.lifecycle_mode is not None:
            require(
                not args.pre_review
                and not args.historical_v1_only
                and not args.historical_v2_only
                and not args.historical_v3_only
                and args.capture_directory is None
                and args.ctrf is None
                and args.support_ctrf is None
                and args.support_output is None
                and args.expected_runtime_version is None,
                "Lifecycle mode cannot be combined with another validation mode",
            )
            expected_state = "ready-to-close" if args.lifecycle_mode == "final" else "closed"
            validate_lifecycle_state(expected_state)
            validate_status_and_documents(phase=args.lifecycle_mode)
            print(f"OQ8 {args.lifecycle_mode} lifecycle validation passed. Evidence was not evaluated.")
        elif (
            args.capture_directory is not None
            or args.ctrf is not None
            or args.expected_runtime_version is not None
        ):
            require(
                args.capture_directory is not None
                and args.ctrf is not None
                and args.support_ctrf is not None
                and args.expected_runtime_version is not None
                and len(args.expected_runtime_version) == 1,
                "Capture mode requires --capture-directory, --ctrf, --support-ctrf, and exactly one --expected-runtime-version",
            )
            require(args.support_output is None, "Capture mode writes deterministic support into the capture directory")
            validate_capture(
                args.capture_directory.resolve(),
                args.ctrf.resolve(),
                args.support_ctrf.resolve(),
                args.expected_runtime_version[0],
                args.expected_configuration,
            )
            print("OQ8 capture validation passed.")
        elif args.support_ctrf is not None or args.support_output is not None:
            require(args.support_ctrf is not None and args.support_output is not None, "Support mode requires --support-ctrf and --support-output")
            sanitize_support_ctrf(args.support_ctrf.resolve(), args.support_output.resolve())
            print("OQ8 deterministic support validation passed.")
        else:
            if args.historical_v1_only:
                validate_committed_packet(current_source=False)
                print("OQ8 Story 4.15 v1 historical evidence validation passed; v1 does not authorize current source.")
            elif args.historical_v2_only:
                validate_committed_packet(current_source=False)
                validate_v2_successor()
                print("OQ8 Story 4.15 v1/v2 historical evidence validation passed; v2 does not authorize current source.")
            elif args.historical_v3_only:
                validate_historical_v3_successor()
                print("OQ8 Story 4.15 v3 historical evidence validation passed; v3 does not authorize current source.")
            elif args.pre_review:
                validate_pre_review_candidate()
            else:
                validate_committed_packet()
            if not args.historical_v1_only and not args.historical_v2_only and not args.historical_v3_only:
                print("OQ8 pre-review candidate validation passed." if args.pre_review else "OQ8 platform evidence validation passed.")
        return 0
    except EvidenceError as exception:
        print(f"OQ8 evidence validation failed: {exception}", file=sys.stderr)
        return 1
    except Exception as exception:
        message = re.sub(r"[\r\n\t]+", " ", str(exception)).strip()
        message = PRIVATE_PATH_TOKEN_RE.sub("<redacted-path>", message)[:256]
        detail = f": {message}" if message else ""
        bounded = EvidenceError(f"Unexpected validator failure was safely bounded ({type(exception).__name__}){detail}")
        print(f"OQ8 evidence validation failed: {bounded}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
