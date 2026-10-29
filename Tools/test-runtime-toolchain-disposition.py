#!/usr/bin/env python3
"""Hermetic negative controls for historical SDK-split disposition and digest binding."""

import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("validate-runtime-toolchain-disposition.py")
SPEC = importlib.util.spec_from_file_location("disposition_validator", SCRIPT)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class DispositionTests(unittest.TestCase):
    def test_historical_disposition_and_cli(self):
        result = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("SDK split unqualified", result.stdout)

    def test_crlf_equivalent_historical_checkout_retains_normalized_digest(self):
        baseline = SCRIPT.with_name(VALIDATOR.BASELINE_NAME)
        disposition = SCRIPT.with_name("runtime-toolchain-disposition-2026-09-27.json")
        with tempfile.TemporaryDirectory() as scratch:
            crlf = Path(scratch) / VALIDATOR.BASELINE_NAME
            crlf.write_bytes(baseline.read_bytes().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
            VALIDATOR.validate(disposition, crlf)
            result = subprocess.run([sys.executable, str(SCRIPT), "--baseline", str(crlf)],
                                    capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_rejects_mutations_without_changing_historical_bytes(self):
        source = SCRIPT.with_name("runtime-toolchain-disposition-2026-09-27.json")
        original = json.loads(source.read_text(encoding="utf-8"))
        mutations = [
            ("baseline digest", lambda d: d["historicalBaseline"].update(sha256="0" * 64)),
            ("baseline path", lambda d: d["historicalBaseline"].update(path="../other.json")),
            ("qualified status", lambda d: d.update(status="qualified")),
            ("split qualification", lambda d: d["aspireSdkSplit"].update(qualified=True)),
            ("numeric false", lambda d: d["aspireSdkSplit"].update(qualified=0)),
            ("invented split approval", lambda d: d["aspireSdkSplit"].update(disposition="approved")),
            ("tuple rewrite", lambda d: d["aspireSdkSplit"].update(tupleSdk="13.5.3")),
            ("hidden split", lambda d: d["aspireSdkSplit"].update(auditedAppHostSdks=["13.5.4"])),
            ("runtime qualification", lambda d: d["qualification"].update(runtimeQualified=True)),
            ("package qualification", lambda d: d["qualification"].update(packageAdoptionQualified=True)),
            ("invented approval", lambda d: d.update(approvedBy="invented")),
            ("support meaning", lambda d: d["daprMeanings"]["dapr"].update(meaning="catalog-package-identity")),
            ("catalog meaning", lambda d: d["daprMeanings"]["Dapr"].update(meaning="runtime-dotnet-support-table-metadata")),
            ("catalog activation", lambda d: d["daprMeanings"]["Dapr"].update(historicalDecision="activated")),
        ]
        mutations += [(f"release hold {key}", lambda d, key=key: d["containment"].update({key: True}))
                      for key in original["containment"]]
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "disposition.json"
            baseline = SCRIPT.with_name(VALIDATOR.BASELINE_NAME)
            for name, mutate in mutations:
                with self.subTest(name=name):
                    changed = copy.deepcopy(original)
                    mutate(changed)
                    path.write_text(json.dumps(changed), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        VALIDATOR.validate(path, baseline)
            path.write_text(source.read_text(encoding="utf-8"), encoding="utf-8")
            drifted = Path(scratch) / VALIDATOR.BASELINE_NAME
            drifted.write_bytes(baseline.read_bytes() + b"\n")
            with self.assertRaisesRegex(ValueError, "SHA-256 drift"):
                VALIDATOR.validate(path, drifted)
            changed = copy.deepcopy(original)
            import hashlib
            changed["historicalBaseline"]["sha256"] = hashlib.sha256(drifted.read_bytes()).hexdigest()
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "SHA-256 drift"):
                VALIDATOR.validate(path, drifted)

    def test_duplicate_json_keys_fail_as_controlled_cli_error(self):
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "duplicate.json"
            path.write_text('{"status":"qualified","status":"superseded-unqualified"}', encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), "--disposition", str(path)],
                                    capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 1)
            self.assertIn("Duplicate JSON key", result.stderr)
            self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
