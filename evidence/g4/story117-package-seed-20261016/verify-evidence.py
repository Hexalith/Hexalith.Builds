#!/usr/bin/env python3
"""Verify the byte-bound local diagnostic receipt, including its portable archives."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import tarfile
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify(receipt, repository, workspace):
    record = json.loads(receipt.read_bytes())
    for name, expected in record["repositoryFilesSha256"].items():
        assert digest((repository / name).read_bytes()) == expected, name
    for name, expected in record["protectedWorkspaceFilesSha256"].items():
        assert digest((workspace / name).read_bytes()) == expected, name
    initial = json.loads((repository / record["initialInputsPath"]).read_bytes())["files"]
    preserved = {name: expected for name, expected in initial.items()
                 if name.startswith("references/Hexalith.Platform/")
                 or name.startswith("references/Hexalith.Builds/src/")
                 or name in ["references/Hexalith.Builds/Props/Directory.Packages.props",
                             "references/Hexalith.Builds/Tools/package-version-audit.json"]}
    for name, expected in preserved.items():
        assert digest((workspace / name).read_bytes()) == expected, name
    bundle = record["retainedArtifactBundle"]
    assert digest((repository / bundle["path"]).read_bytes()) == bundle["sha256"], "bundle"
    contents = {}
    with tarfile.open(repository / bundle["path"], "r:gz") as archive:
        files = [member for member in archive.getmembers() if member.isfile()]
        assert len(files) == len(bundle["membersSha256"]), "bundle member count"
        assert len({member.name for member in files}) == len(files), "duplicate bundle member"
        for member in files:
            body = archive.extractfile(member).read()
            assert digest(body) == bundle["membersSha256"][member.name], member.name
            contents[member.name] = body
    proof = json.loads(contents["diagnostic/qualification-evidence/packaged-seed-subscriptions.json"])
    final_proof = json.loads((repository / record["finalProofPath"]).read_bytes())
    assert final_proof == proof, "reviewed probe differs from original package proof"
    package_name = "Hexalith.Builds.Module.Cli.4.31.2.nupkg"
    package = contents["diagnostic/" + package_name]
    assert digest(package).upper() == proof["package"]["sha256"], "module archive"
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        assert digest(archive.read(proof["seed"]["entry"])).upper() == proof["seed"]["sha256"], "seed"
    assert proof["seed"]["loadedSha256"] == proof["seed"]["sha256"], "loaded seed"
    assert proof["status"] == "passed" and len(proof["checks"]) == 2, "positive result"
    for check, declarative in zip(proof["checks"], [False, True], strict=True):
        assert check["declarative"] is declarative
        assert check["discoveryStatusCode"] == (404 if declarative else 200)
        assert check["receiverPresent"] is True
        assert check["receiverTopicMetadataCount"] == (0 if declarative else 1)
        assert check["projectionSubscriptionCount"] == (0 if declarative else 1)
        for name in ["subscriptionPubsubName", "receiverPubsubName"]:
            assert check[name] == (None if declarative else "configured-seed-events")
    inventory = json.loads(contents["diagnostic/g4-tool-package-inventory.json"])
    assert inventory["qualification"]["releaseEligible"] is False, "release eligibility"
    assert inventory["qualification"]["sourceTree"]["clean"] is False, "dirty source"
    assert record["decision"]["storyClosure"] is False and record["decision"]["stagingQualification"] is False
    if "corpusRepair" in record:
        qualification = inventory["qualification"]
        for lane in ["sourceValidation", "controls"]:
            assert qualification[lane] == {"mode": "executed", "result": "passed"}, lane
        assert qualification["packageBuild"] == {"mode": "official", "result": "passed"}, "package build"
        assert qualification["fixtures"]["mode"] == "repository-modified", "fixture provenance"
        assert qualification["ineligibilityReasons"], "ineligibility reasons"
        for row in qualification["fixtures"]["files"]:
            assert digest((repository / "test/fixtures" / row["file"]).read_bytes()).lower() == row["sha256"].lower(), row["file"]
        for package_row in inventory["packages"]:
            for role in ["nupkg", "snupkg"]:
                artifact = package_row[role]
                assert digest(contents["diagnostic/" + artifact["file"]]).lower() == artifact["sha256"].lower(), artifact["file"]
        command = record["corpusRepair"]["fullQualification"]
        assert command["exitCode"] == 0
        assert "-RequireControls" in command["command"]
        assert "-SkipSourceValidation" not in command["command"]
        prepared = json.loads((repository / record["corpusRepair"]["fixturePreparationPath"]).read_bytes())
        assert prepared["idempotent"] and prepared["twoIndependentPreparationsIdentical"]
        assert len(prepared["changedFiles"]) == 52
        assert len(prepared["unchangedExpectationsSha256"]) == 37
        for name, expected in prepared["unchangedExpectationsSha256"].items():
            assert digest((repository / "test/fixtures/evidence/acceptance" / name).read_bytes()) == expected, name
    print(f"Verified {len(record['repositoryFilesSha256'])} repository files, "
          f"{len(record['protectedWorkspaceFilesSha256'])} protected workspace files, "
          f"{len(preserved)} preserved Platform/packaged-source inputs, {len(contents)} bundle members "
          "and both exact-package subscription modes; release ineligible.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", type=Path, default=Path(__file__).with_name("qualification.json"))
    parser.add_argument("--workspace", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        verify(arguments.receipt.resolve(), Path(__file__).resolve().parents[3], arguments.workspace.resolve())
    except (AssertionError, KeyError, OSError, ValueError, tarfile.TarError, zipfile.BadZipFile) as error:
        raise SystemExit(f"Evidence verification failed: {error}") from error
