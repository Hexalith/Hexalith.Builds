#!/usr/bin/env python3
"""Recheck the retained final Stage 3 live streak and package inventory."""

import hashlib
import json
from pathlib import Path


HERE = Path(__file__).resolve().parent
FIRST = HERE.parent / "stage3-review-remediation-20260923-final"
PACKAGE_ROOT = HERE / "packages"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    first = read_json(FIRST / "results.json")
    continuation = read_json(HERE / "results.json")
    assert len(first) == 10 and len(continuation) == 2
    assert first[1]["status"] == "failed"
    assert first[1]["suiteSummary"] == [10, 0, 0, 0]
    assert first[1]["probes"]["processes"]["status"] == "unverified"
    assert first[1]["probes"]["processes"]["errors"]
    baseline = read_json(FIRST / "fingerprints.json")
    assert baseline == read_json(HERE / "fingerprints.json")
    streak = read_json(HERE / "combined-streak.json")
    assert streak["status"] == "verified" and streak["suiteCount"] == 10
    assert streak["sourceSha256"] == baseline["source"]["sha256"]
    assert streak["binarySha256"] == baseline["binaries"]["sha256"]
    selected = [(FIRST, result) for result in first[2:]] + [(HERE, result) for result in continuation]
    assert len(selected) == 10
    for entry, (directory, result) in zip(streak["runs"], selected, strict=True):
        assert entry["campaign"] == directory.name and entry["run"] == result["run"]
        assert result["status"] == "passed" and result["exit"] == 0
        assert result["suiteSummary"] == [10, 0, 0, 0] and result["unchanged"]
        assert result["sourceSha256Before"] == result["sourceSha256After"] == streak["sourceSha256"]
        assert result["binarySha256Before"] == result["binarySha256After"] == streak["binarySha256"]
        assert all(probe["exit"] == 0 and probe["status"] == "verified"
                   and probe["count"] == 0 and not probe["errors"]
                   for probe in result["probes"].values())
        log = directory / f"run-{result['run']:02d}" / "test.log"
        assert digest(log) == result["testLogSha256"] == entry["testLogSha256"]

    inventory_path = PACKAGE_ROOT / "g4-tool-package-inventory.json"
    inventory = read_json(inventory_path)
    records = []
    for package in inventory["packages"]:
        for role in ("nupkg", "snupkg"):
            records.append(package[role])
    records.extend(inventory["qualificationEvidence"])
    assert len(inventory["packages"]) == 2 and len(records) == 58
    for record in records:
        path = PACKAGE_ROOT / record["file"]
        assert path.stat().st_size == record["sizeBytes"]
        assert digest(path).lower() == record["sha256"].lower()
    assert inventory["qualification"]["controls"]["result"] == "passed"
    assert inventory["qualification"]["sourceValidation"]["result"] == "passed"
    assert inventory["qualification"]["releaseEligible"] is False

    for name in ("packaged-test-output.json", "packaged-unavailable-output.json"):
        result = read_json(PACKAGE_ROOT / "qualification-evidence" / name)
        assert result["status"] == "unavailable" and result["outcome"]["ruleId"] == "HXR003"
    for name in ("packaged-test-evidence.json", "packaged-unavailable-evidence.json"):
        result = read_json(PACKAGE_ROOT / "qualification-evidence" / name)
        assert result["finalStatus"] == "unavailable"
        assert result["outcome"]["ruleId"] == "HXR003" and result["outcome"]["exitCode"] == 2

    print("LIVE-STREAK-VALID 10 suites 100 tests 0 failures 0 skips 40 verified zero-count probes")
    print("SOURCE-SHA256", streak["sourceSha256"])
    print("BINARY-SHA256", streak["binarySha256"])
    print("PACKAGE-INVENTORY-VALID", len(records), "entries", digest(inventory_path))
    print("PUBLIC-RUN-TEST HXR003 exit 2")


if __name__ == "__main__":
    main()
