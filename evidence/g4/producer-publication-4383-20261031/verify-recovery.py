#!/usr/bin/env python3
"""Recheck the frozen 4.38.3 inventory and recovered content without regeneration."""

import hashlib
import json
from pathlib import Path
import subprocess


ROOT = Path(__file__).resolve().parents[3]
PACKET = Path(__file__).resolve().parent
SOURCE = "d1931b1e803144048d927fff16cb8119afde541e"
INVENTORY_SHA256 = "2421408b564af44ba75a83670aee147d21347d09ed5b823d091c24a6dbc29b5e"


def verify():
    inventory_bytes = (PACKET / "original-inventory.json").read_bytes()
    assert len(inventory_bytes) == 74432
    assert hashlib.sha256(inventory_bytes).hexdigest() == INVENTORY_SHA256
    inventory = json.loads(inventory_bytes)
    ledger = json.loads((PACKET / "recovery-ledger.json").read_bytes())
    assert inventory["version"] == "4.38.3"
    assert inventory["qualification"]["sourceTree"] == {"clean": True, "revision": SOURCE}
    assert ledger["targetSource"] == SOURCE and ledger["inventorySha256"] == INVENTORY_SHA256
    assert ledger["verifiedOriginalRunCarriers"] == 0
    rows = ledger["rows"]
    assert len(rows) == 55 and len({row["file"] for row in rows}) == 55
    assert [{key: row[key] for key in ("file", "sha256", "sizeBytes")} for row in rows] == inventory["qualificationEvidence"]
    recovered = set()
    unavailable = set()
    for row in rows:
        if row["byteRecovery"] == "PASS":
            path = ROOT / row["recoveredPath"]
            assert not path.is_symlink() and path.resolve().is_relative_to(PACKET / "matching-content")
            assert path.relative_to(PACKET / "matching-content").as_posix() == row["file"]
            data = path.read_bytes()
            assert len(data) == row["sizeBytes"] == row["observedSizeBytes"]
            assert hashlib.sha256(data).hexdigest().upper() == row["sha256"] == row["observedSha256"]
            assert row["carrierRevision"] == SOURCE and row["originalRunProvenance"] == "UNVERIFIED"
            blob = subprocess.check_output(["git", "rev-parse", SOURCE + ":" + row["carrier"]], cwd=ROOT, text=True).strip()
            assert blob == row["gitBlob"]
            assert data == subprocess.check_output(["git", "cat-file", "blob", blob], cwd=ROOT)
            recovered.add(row["file"])
        else:
            assert row["byteRecovery"] == "UNAVAILABLE" and row["originalRunProvenance"] == "UNAVAILABLE"
            assert row["recoveredPath"] is None and row["observedSha256"] is None and row["observedSizeBytes"] is None
            unavailable.add(row["file"])
    assert recovered == {p.relative_to(PACKET / "matching-content").as_posix() for p in (PACKET / "matching-content").rglob("*") if p.is_file()}
    assert len(recovered) == ledger["matchingContentFiles"] == 50
    assert unavailable == {"qualification-evidence/" + name for name in (
        "packaged-down-evidence.json", "packaged-seed-subscriptions.json", "packaged-test-evidence.json",
        "packaged-unavailable-evidence.json", "qualification.log")}
    tag = json.loads((PACKET / "release-tag.json").read_bytes())
    assert tag["object"]["sha"] == SOURCE and tag["object"]["type"] == "commit"
    for name, run in (("ci", 40669428503), ("release", 40673566658)):
        metadata = json.loads((PACKET / f"{name}-run.json").read_bytes())
        assert metadata["head_sha"] == SOURCE and metadata["id"] == run and metadata["run_attempt"] == 1
        assert metadata["status"] == "completed" and metadata["conclusion"] == "success"
        assert metadata["path"] == ".github/workflows/" + ("ci.yml" if name == "ci" else "build-release.yml")
        assert metadata["event"] == ("push" if name == "ci" else "workflow_dispatch")
        assert json.loads((PACKET / f"{name}-artifacts.json").read_bytes()) == {"artifacts": []}
        jobs = json.loads((PACKET / f"{name}-jobs.json").read_bytes())["jobs"]
        assert jobs and all(job["run_id"] == run for job in jobs)
        job, = [job for job in jobs if job["id"] == (122752147580 if name == "ci" else 122766264075)]
        assert job["conclusion"] == "success" and job["status"] == "completed"
    asset, = json.loads((PACKET / "release-assets.json").read_bytes())["assets"]
    assert asset["id"] == 684839586 and asset["size"] == 74432
    assert asset["digest"] == "sha256:" + INVENTORY_SHA256
    receipts = json.loads((PACKET / "retrieval-receipts.json").read_bytes())["receipts"]
    assert len(receipts) == 7
    for receipt in receipts:
        path = ROOT / receipt["responseFile"]
        assert not path.is_symlink() and path.resolve().is_relative_to(PACKET)
        data = path.read_bytes()
        assert receipt["exitCode"] == 0 and receipt["startedAtUtc"] and receipt["endedAtUtc"]
        assert len(data) == receipt["responseSizeBytes"]
        assert hashlib.sha256(data).hexdigest() == receipt["responseSha256"]
        assert receipt["command"][:2] == ["gh", "api"]
    print("PASS: 55 exact inventory rows; 50 recovered byte matches; 5 explicitly unavailable; run/source/carrier identities checked. Original-run recovery remains BLOCKED.")


if __name__ == "__main__":
    verify()
