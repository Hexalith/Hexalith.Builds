#!/usr/bin/env python3
"""Validate the separate, unqualified disposition of the immutable 2026-09-27 baseline."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path

BASELINE_NAME = "runtime-toolchain-baseline-2026-09-27.json"
BASELINE_SHA256 = "b9aa6791effe78cbb9bf405cd8384b81a6d750f996fabf8f79b7cd1c3bedad01"


def workspace_file(value: str) -> Path:
    """Confine explicitly selected CLI files to the current working directory."""
    workspace = Path.cwd().resolve()
    try:
        relative = (workspace / value).resolve(strict=True).relative_to(workspace)
        if not relative.parts:
            raise ValueError("a file is required")
        path = workspace.joinpath(*(os.path.basename(part) for part in relative.parts))
        if not path.is_file():
            raise ValueError("a regular file is required")
        return path
    except (OSError, ValueError) as error:
        raise argparse.ArgumentTypeError("input must be an existing file inside the current workspace") from error


def read_json(path: Path) -> dict:
    """Reject duplicate JSON keys as well as malformed documents."""
    def unique(pairs: list[tuple[str, object]]) -> dict:
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def fields(value: object, names: set[str], label: str) -> dict:
    require(isinstance(value, dict) and set(value) == names, f"{label} fields drift")
    return value


def validate(disposition_path: Path, baseline_path: Path) -> None:
    """Require the pinned normalized-LF digest and reject new qualification or approval."""
    require(hashlib.sha256(baseline_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() == BASELINE_SHA256,
            "Historical baseline SHA-256 drift")
    baseline = read_json(baseline_path)
    disposition = fields(read_json(disposition_path), {
        "schema", "historicalBaseline", "status", "aspireSdkSplit", "daprMeanings",
        "qualification", "containment",
    }, "Disposition")
    require(disposition["schema"] == "hexalith.runtime-toolchain-disposition.v1", "Schema drift")
    binding = fields(disposition["historicalBaseline"], {"path", "sha256"}, "Baseline binding")
    require(binding == {"path": BASELINE_NAME, "sha256": BASELINE_SHA256}, "Baseline binding drift")
    require(disposition["status"] == "superseded-unqualified", "Historical status cannot qualify the split")
    split = fields(disposition["aspireSdkSplit"], {
        "tupleSdk", "auditedAppHostSdks", "disposition", "qualified",
    }, "Aspire SDK split")
    observed = sorted({pin["version"] for pin in baseline["pinAudit"]["appHostProjects"]})
    require(split["tupleSdk"] == baseline["tuple"]["aspireSdk"]
            and split["auditedAppHostSdks"] == observed and len(observed) > 1,
            "Aspire SDK split does not match historical evidence")
    require(split["disposition"] == "superseded-without-split-qualification"
            and split["qualified"] is False, "Aspire SDK split is unqualified")
    meanings = fields(disposition["daprMeanings"], {"dapr", "Dapr"}, "Dapr meanings")
    require(meanings["dapr"] == {
        "meaning": "runtime-dotnet-support-table-metadata",
        "historicalDecision": baseline["dispositions"]["dapr"]["decision"],
    }, "Lowercase dapr must retain its support-table meaning")
    require(meanings["Dapr"] == {
        "meaning": "catalog-package-identity",
        "historicalDecision": baseline["dispositions"]["Dapr"],
    }, "Uppercase Dapr must retain its catalog-package meaning")
    qualification = fields(disposition["qualification"], {
        "runtimeQualified", "packageAdoptionQualified",
    }, "Qualification")
    require(all(value is False for value in qualification.values()), "No qualification is granted")
    containment = fields(disposition["containment"], set(baseline["containment"]), "Containment")
    require(all(value is False for value in containment.values()), "Historical release holds remain active")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--disposition")
    parser.add_argument("--baseline")
    arguments = parser.parse_args()
    try:
        disposition = (Path(__file__).with_name("runtime-toolchain-disposition-2026-09-27.json")
                       if arguments.disposition is None else workspace_file(arguments.disposition))
        baseline = (Path(__file__).with_name(BASELINE_NAME)
                    if arguments.baseline is None else workspace_file(arguments.baseline))
        validate(disposition, baseline)
    except (argparse.ArgumentTypeError, OSError, ValueError, KeyError, TypeError) as error:
        print(f"RUNTIME-TOOLCHAIN-DISPOSITION-INVALID: {error}", file=sys.stderr)
        return 1
    print("RUNTIME-TOOLCHAIN-DISPOSITION-VALID: superseded; SDK split unqualified; release holds preserved")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
