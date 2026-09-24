"""Failure controls for the live qualification evidence harness."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[2] / "Tools/run-g4-stage3-live-qualification.py"
SPEC = importlib.util.spec_from_file_location("stage3_live_qualification", SCRIPT)
qualification = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(qualification)


class Stage3LiveQualificationTests(unittest.TestCase):
    def test_inaccessible_owned_process_environment_is_unverified(self):
        with tempfile.TemporaryDirectory() as directory:
            proc = Path(directory) / "12345"
            proc.mkdir()
            (proc / "environ").write_bytes(b"unrelated=true")
            (proc / "stat").write_text("12345 (test) " + " ".join(["0"] * 19 + ["1"]), encoding="ascii")
            with mock.patch.object(Path, "read_bytes", side_effect=PermissionError("denied")):
                result = qualification.probe_processes(Path(directory))
        self.assertEqual("unverified", result["status"])
        self.assertEqual(1, result["exit"])
        self.assertEqual(1, result["inaccessible"])
        self.assertIsNone(result["count"])
        self.assertIn("denied", result["errors"][0])

    def test_failed_container_enumeration_cannot_report_zero(self):
        with mock.patch.object(qualification.subprocess, "run", side_effect=FileNotFoundError("docker missing")):
            result = qualification.probe_containers()
        self.assertEqual("unverified", result["status"])
        self.assertIsNone(result["count"])
        self.assertEqual(1, len(result["errors"]))

    def test_missing_environment_of_existing_process_is_unverified(self):
        with tempfile.TemporaryDirectory() as directory:
            proc = Path(directory) / "12345"
            proc.mkdir()
            (proc / "stat").write_text("12345 (test) " + " ".join(["0"] * 19 + ["1"]), encoding="ascii")
            result = qualification.probe_processes(Path(directory))
        self.assertEqual("unverified", result["status"])
        self.assertEqual(1, result["inaccessible"])
        self.assertIsNone(result["count"])

    def test_unreadable_log_is_unverified(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / "missing.log").symlink_to(Path(directory) / "absent.log")
            result = qualification.probe_redaction(Path(directory))
        self.assertEqual("unverified", result["status"])
        self.assertIsNone(result["count"])
        self.assertEqual(1, len(result["errors"]))

    def test_drift_or_unverified_probe_rejects_suite(self):
        baseline = {"source": "a", "binaries": "b"}
        probes = {"containers": {"status": "verified", "count": 0}}
        self.assertTrue(qualification.qualifies(0, (11, 0, 0, 0), baseline, baseline, baseline, probes))
        self.assertFalse(qualification.qualifies(0, (10, 0, 0, 0), baseline, baseline, baseline, probes))
        self.assertFalse(qualification.qualifies(0, (11, 0, 0, 0), baseline,
                                                {"source": "changed", "binaries": "b"}, baseline, probes))
        probes["containers"] = {"status": "unverified", "count": None}
        self.assertFalse(qualification.qualifies(0, (11, 0, 0, 0), baseline, baseline, baseline, probes))


if __name__ == "__main__":
    unittest.main()
