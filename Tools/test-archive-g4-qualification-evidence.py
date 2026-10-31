#!/usr/bin/env python3
"""Exercise carrier bytes, inventory boundaries and producer identity offline."""

from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SPEC = importlib.util.spec_from_file_location("carrier", Path(__file__).with_name("archive-g4-qualification-evidence.py"))
carrier = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(carrier)
SOURCE = "a" * 40


class CarrierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="g4-carrier-contract-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.evidence = self.directory / "qualification-evidence"
        self.evidence.mkdir()
        self.data = b'{"fixture":"offline contract only"}\r\n'
        (self.evidence / "sample.json").write_bytes(self.data)
        self.inventory = {
            "schema": "hexalith.g4-tool-package-inventory.v1", "version": "0.0.0-contract",
            "configuration": "Release",
            "qualification": {"releaseEligible": True, "sourceTree": {"clean": True, "revision": SOURCE}},
            "qualificationEvidence": [{"file": "qualification-evidence/sample.json",
                                       "sha256": hashlib.sha256(self.data).hexdigest().upper(),
                                       "sizeBytes": len(self.data)}],
        }
        environment = {
            "GITHUB_SHA": SOURCE, "GITHUB_REPOSITORY": "Hexalith/Hexalith.Builds",
            "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "2", "GITHUB_JOB": "release",
            "GITHUB_WORKFLOW_REF": "Hexalith/Hexalith.Builds/.github/workflows/build-release.yml@refs/heads/main",
            "GITHUB_WORKFLOW_SHA": SOURCE, "GITHUB_EVENT_NAME": "workflow_dispatch",
        }
        self.env = patch.dict(os.environ, environment, clear=True)
        self.env.start()
        self.addCleanup(self.env.stop)
        self.git = patch.object(carrier.subprocess, "check_output", side_effect=lambda args, **kwargs: "" if "status" in args else SOURCE + "\n")
        self.git.start()
        self.addCleanup(self.git.stop)

    def write_inventory(self):
        data = (json.dumps(self.inventory, indent=2) + "\r\n").encode()
        (self.directory / carrier.INVENTORY_NAME).write_bytes(data)
        return data

    def test_archive_preserves_exact_bytes_names_and_run_identity(self):
        original = self.write_inventory()
        archive_path = carrier.archive_evidence(self.directory, "release")
        with zipfile.ZipFile(archive_path) as archive:
            self.assertEqual(set(archive.namelist()), {carrier.INVENTORY_NAME, "qualification-evidence/sample.json", "producer-run.json"})
            self.assertEqual(archive.read(carrier.INVENTORY_NAME), original)
            self.assertEqual(archive.read("qualification-evidence/sample.json"), self.data)
            identity = json.loads(archive.read("producer-run.json"))
            self.assertEqual(identity["inventorySha256"], hashlib.sha256(original).hexdigest())
            self.assertEqual(identity["sourceRevision"], SOURCE)
            self.assertEqual(identity["GITHUB_RUN_ID"], "123")
            self.assertEqual(identity["GITHUB_RUN_ATTEMPT"], "2")
            self.assertEqual(identity["version"], "0.0.0-contract")
            self.assertEqual(identity["qualificationEvidenceFiles"], 1)
        self.assertEqual((self.directory / carrier.INVENTORY_NAME).read_bytes(), original)
        self.assertEqual((self.evidence / "sample.json").read_bytes(), self.data)

    def test_rejects_changed_hash_size_empty_duplicate_or_unsafe_inventory(self):
        original = copy.deepcopy(self.inventory)
        for fault in ("hash", "size", "empty", "duplicate", "escape", "absolute", "case-alias"):
            with self.subTest(fault=fault):
                self.inventory = copy.deepcopy(original)
                rows = self.inventory["qualificationEvidence"]
                if fault == "hash": rows[0]["sha256"] = "0" * 64
                if fault == "size": rows[0]["sizeBytes"] += 1
                if fault == "empty": rows.clear()
                if fault == "duplicate": rows.append(copy.deepcopy(rows[0]))
                if fault == "escape": rows[0]["file"] = "qualification-evidence/../sample.json"
                if fault == "absolute": rows[0]["file"] = str(self.evidence / "sample.json")
                if fault == "case-alias": rows.append({**rows[0], "file": "qualification-evidence/SAMPLE.json"})
                self.write_inventory()
                with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
                self.assertFalse((self.directory / carrier.ARCHIVE_NAME).exists())

    def test_rejects_missing_extra_or_symlink_evidence(self):
        self.write_inventory()
        path = self.evidence / "sample.json"
        path.unlink()
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        target = self.directory / "outside.json"
        target.write_bytes(self.data)
        path.symlink_to(target)
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        path.unlink()
        path.write_bytes(self.data)
        (self.evidence / "extra.json").write_bytes(b"unexpected")
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        self.assertFalse((self.directory / carrier.ARCHIVE_NAME).exists())

    def test_rejects_ineligible_release_but_labels_ci_without_promoting_it(self):
        self.inventory["qualification"]["releaseEligible"] = False
        original = self.write_inventory()
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        os.environ["GITHUB_WORKFLOW_REF"] = "Hexalith/Hexalith.Builds/.github/workflows/ci.yml@refs/pull/1/merge"
        os.environ["GITHUB_EVENT_NAME"] = "pull_request"
        os.environ["GITHUB_JOB"] = "contracts"
        with zipfile.ZipFile(carrier.archive_evidence(self.directory, "ci")) as archive:
            self.assertEqual(archive.read(carrier.INVENTORY_NAME), original)
            self.assertEqual(json.loads(archive.read("producer-run.json"))["kind"], "ci")

    def test_rejects_dirty_source_and_missing_or_mismatched_run_identity(self):
        self.write_inventory()
        for key in ("GITHUB_SHA", "GITHUB_WORKFLOW_SHA", "GITHUB_REPOSITORY", "GITHUB_RUN_ID",
                    "GITHUB_RUN_ATTEMPT", "GITHUB_WORKFLOW_REF", "GITHUB_JOB", "GITHUB_EVENT_NAME"):
            with self.subTest(key=key), patch.dict(os.environ, {key: ""}):
                with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        with patch.dict(os.environ, {"GITHUB_SHA": "b" * 40}):
            with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        self.inventory["qualification"]["sourceTree"]["clean"] = False
        self.write_inventory()
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")

    def test_existing_carrier_is_never_overwritten(self):
        self.write_inventory()
        path = carrier.archive_evidence(self.directory, "release")
        original = path.read_bytes()
        with self.assertRaisesRegex(ValueError, "already exists"):
            carrier.archive_evidence(self.directory, "release")
        self.assertEqual(path.read_bytes(), original)

    def test_interrupted_archive_never_leaves_a_final_or_staged_carrier(self):
        self.write_inventory()
        with patch.object(carrier.zipfile.ZipFile, "writestr", side_effect=OSError("injected disk failure")):
            with self.assertRaises(OSError): carrier.archive_evidence(self.directory, "release")
        self.assertFalse((self.directory / carrier.ARCHIVE_NAME).exists())
        self.assertEqual(list(self.directory.glob(".g4-carrier-*")), [])

    def test_rechecks_real_source_cleanliness(self):
        self.write_inventory()
        with patch.object(carrier.subprocess, "check_output", side_effect=[SOURCE + "\n", " M changed.py\n"]):
            with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")

    def test_failed_attempt_records_diagnostic_identity_without_qualification_credit(self):
        with patch.dict(os.environ, {"G4_PRODUCER_STEP_OUTCOME": "failure"}):
            path = carrier.record_attempt(self.directory, "release")
        record = json.loads(path.read_bytes())
        self.assertEqual(record["producerStepOutcome"], "failure")
        self.assertEqual(record["GITHUB_RUN_ID"], "123")
        self.assertIs(record["qualificationCredit"], False)
        self.assertFalse((self.directory / carrier.ARCHIVE_NAME).exists())

    def test_rejects_ambiguous_inventory_and_untyped_source_identity(self):
        original = self.write_inventory()
        path = self.directory / carrier.INVENTORY_NAME
        path.write_bytes(original.replace(b'"configuration": "Release"', b'"configuration": "Debug", "configuration": "Release"'))
        with self.assertRaisesRegex(ValueError, "Duplicate"): carrier.archive_evidence(self.directory, "release")
        self.inventory["qualification"]["sourceTree"]["clean"] = 1
        self.write_inventory()
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")
        self.inventory["qualification"]["sourceTree"]["clean"] = True
        self.inventory["version"] = 4383
        self.write_inventory()
        with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")

    def test_rejects_symlinked_ancestor_and_inconsistent_event_identity(self):
        self.write_inventory()
        alias = self.directory / "alias"
        alias.symlink_to(self.directory, target_is_directory=True)
        with self.assertRaises(ValueError): carrier.archive_evidence(alias / ".", "release")
        for key, value in (("GITHUB_JOB", "unrelated"), ("GITHUB_EVENT_NAME", "pull_request"),
                           ("GITHUB_WORKFLOW_REF", "Hexalith/Hexalith.Builds/.github/workflows/build-release.yml@")):
            with self.subTest(key=key), patch.dict(os.environ, {key: value}):
                with self.assertRaises(ValueError): carrier.archive_evidence(self.directory, "release")

    def test_verification_rejects_missing_truncated_changed_and_extra_members(self):
        self.write_inventory()
        with self.assertRaises(FileNotFoundError): carrier.verify_archive(self.directory, "release")
        path = carrier.archive_evidence(self.directory, "release")
        carrier.verify_archive(self.directory, "release")
        original = path.read_bytes()
        path.write_bytes(b"truncated")
        with self.assertRaises(zipfile.BadZipFile): carrier.verify_archive(self.directory, "release")
        for member in ("producer-run.json", "qualification-evidence/sample.json", carrier.INVENTORY_NAME):
            path.write_bytes(original)
            with zipfile.ZipFile(path) as archive:
                content = {name: archive.read(name) for name in archive.namelist()}
            content[member] = b"changed"
            with zipfile.ZipFile(path, "w") as archive:
                for name, data in content.items(): archive.writestr(name, data)
            with self.assertRaises(ValueError): carrier.verify_archive(self.directory, "release")
        path.write_bytes(original)
        with zipfile.ZipFile(path, "a") as archive: archive.writestr("extra", b"unexpected")
        with self.assertRaises(ValueError): carrier.verify_archive(self.directory, "release")

    def test_workflows_retain_carriers_and_release_publishes_it(self):
        root = Path(__file__).resolve().parents[1]
        release = json.loads((root / "package.json").read_text())["release"]
        assets = next(plugin[1]["assets"] for plugin in release["plugins"] if isinstance(plugin, list) and plugin[0] == "@semantic-release/github")
        self.assertEqual({asset["path"] for asset in assets}, {
            "artifacts/g4-tool-packages/**/g4-tool-package-inventory.json",
            "artifacts/g4-tool-packages/**/g4-tool-qualification-evidence.zip",
        })
        for workflow, path in (("ci.yml", "artifacts/g4-ci-qualification/"), ("build-release.yml", "artifacts/g4-tool-packages/**/")):
            text = (root / ".github/workflows" / workflow).read_text()
            block, = re.findall(r"(?m)^\s*- name: Retain (?:CI qualification evidence|actual-version qualification evidence)\n([\s\S]*?)(?=\n\s*- name:|\n  [a-zA-Z_-]+:|\Z)", text)
            self.assertIn("if: always()\n", block)
            self.assertIn("actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9", block)
            self.assertIn(path + carrier.ARCHIVE_NAME, block)
            self.assertIn(path + "qualification-evidence/", block)
            self.assertIn("${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}", block)
            self.assertIn("retention-days: " + ("30" if workflow == "ci.yml" else "90"), block)
        ci = (root / ".github/workflows/ci.yml").read_text()
        self.assertIn("-PackageDirectory artifacts/g4-ci-qualification -RetainPackageDirectory -RequireControls -SkipSourceValidation", ci)
        verifier = (root / "Tools/verify-g4-tool-release.ps1").read_text()
        self.assertLess(verifier.index("Assert-ReleaseEligibleInventory"), verifier.index("archive-g4-qualification-evidence.py"))
        self.assertIn("if ($LASTEXITCODE -ne 0)", verifier)
        publisher = (root / "Tools/publish-g4-tool-packages.ps1").read_text()
        self.assertLess(publisher.index("--kind release --verify"), publisher.index("'nuget', 'push'"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
