#!/usr/bin/env python3
"""Independently recheck the final public-command and live evidence records."""

import hashlib
import json
from pathlib import Path


HERE = Path(__file__).resolve().parent
LIVE = HERE.parent / "stage3-public-20260923-live-final-qualified"


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    baseline = read(LIVE / "fingerprints.json")
    qualification = read(LIVE / "qualification.json")
    results = read(LIVE / "results.json")
    public = read(HERE / "public-command-qualification.json")
    package = read(HERE / "package-hash-verification-qualified.json")
    source = baseline["source"]["sha256"]
    binary = baseline["binaries"]["sha256"]
    problems = []

    if qualification != {"status": "passed", "requestedSuites": 10, "passedSuites": 10,
                         "sourceSha256": source, "binarySha256": binary}:
        problems.append("campaign summary does not match the baseline")
    if len(results) != 10:
        problems.append("campaign did not record ten suites")
    for index, result in enumerate(results, 1):
        if result.get("run") != index or result.get("status") != "passed" or result.get("exit") != 0:
            problems.append(f"suite {index} did not pass")
        if result.get("suiteSummary") != [11, 0, 0, 0] or result.get("unchanged") is not True:
            problems.append(f"suite {index} test count or drift invalid")
        if any(result.get(key) != value for key, value in (
                ("sourceSha256Before", source), ("sourceSha256After", source),
                ("binarySha256Before", binary), ("binarySha256After", binary))):
            problems.append(f"suite {index} fingerprint mismatch")
        probes = result.get("probes", {})
        if set(probes) != {"containers", "processes", "workspaces", "redaction"} or any(
                item.get("status") != "verified" or item.get("exit") != 0 or item.get("count") != 0
                for item in probes.values()):
            problems.append(f"suite {index} cleanup or redaction unverified")
        log = LIVE / f"run-{index:02d}" / "test.log"
        if not log.is_file() or result.get("testLogSha256") != sha256(log):
            problems.append(f"suite {index} test log hash mismatch")

    if public.get("status") != "passed" or public.get("unchanged") is not True:
        problems.append("public command probe did not pass unchanged")
    if public.get("sourceSha256") != source or public.get("binarySha256") != binary:
        problems.append("public command probe used different inputs")
    if set(public.get("checks", {})) != {
            "readyStateAndContainer", "exactDown", "cleaned", "idempotentDown", "unsupportedTestNonPassing"
    } or not all(public["checks"].values()):
        problems.append("public command checks incomplete")
    if package.get("status") != "passed" or package.get("verifiedFiles") != 58 or package.get("failures"):
        problems.append("package inventory verification incomplete")

    report = {"status": "passed" if not problems else "failed", "problems": problems,
              "sourceSha256": source, "binarySha256": binary,
              "qualifiedSuites": len(results) if not problems else 0,
              "qualifiedLiveTests": 11 * len(results) if not problems else 0,
              "verifiedZeroProbes": 4 * len(results) if not problems else 0,
              "verifiedPackageFiles": package.get("verifiedFiles")}
    (HERE / "stage3-public-verification.json").write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(report["status"], report["qualifiedSuites"], report["qualifiedLiveTests"])
    return 0 if not problems else 1


if __name__ == "__main__":
    raise SystemExit(main())
