#!/usr/bin/env python3
"""Hermetic controls for the G-6 CLI's selected-workspace file boundary."""

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("g6_cli_paths", Path(__file__).with_name("g6_current.py"))
G6 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(G6)


class CliPathTests(unittest.TestCase):
    def test_audit_writes_relative_and_absolute_workspace_outputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary).resolve()
            for output in ("evidence/audit.json", str(workspace / "absolute.json")):
                with self.subTest(output=output), patch.object(G6, "audit", return_value={"issues": []}):
                    self.assertEqual(0, G6.main([
                        "audit", "--workspace", str(workspace), "--policy", "policy.json", "--out", output,
                    ]))
                    self.assertEqual({"issues": []}, json.loads((workspace / output).read_text()))

    def test_output_traversal_is_refused_before_audit_or_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary).resolve()
            workspace = base / "workspace"
            workspace.mkdir()
            protected = base / "protected.json"
            protected.write_text("preserved")
            for output in ("../protected.json", str(protected)):
                with self.subTest(output=output), patch.object(G6, "audit") as audit, \
                        contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(1, G6.main([
                        "audit", "--workspace", str(workspace), "--policy", "policy.json", "--out", output,
                    ]))
                    audit.assert_not_called()
                    self.assertEqual("preserved", protected.read_text())

    def test_symlink_output_escape_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary).resolve()
            workspace = base / "workspace"
            workspace.mkdir()
            outside = base / "outside"
            outside.mkdir()
            try:
                (workspace / "link").symlink_to(outside, target_is_directory=True)
            except OSError as error:
                self.skipTest(f"directory symlinks unavailable: {error}")
            with patch.object(G6, "audit") as audit, contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(1, G6.main([
                    "audit", "--workspace", str(workspace), "--policy", "policy.json",
                    "--out", "link/audit.json",
                ]))
                audit.assert_not_called()
            self.assertFalse((outside / "audit.json").exists())

    def test_policy_and_evidence_escapes_are_refused_before_validation(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary).resolve()
            for arguments in (
                ["audit", "--policy", "../policy.json"],
                ["validate", "--policy", "policy.json", "--evidence", "../evidence.json"],
            ):
                with self.subTest(arguments=arguments), patch.object(G6, "audit") as audit, \
                        patch.object(G6, "validate") as validate, contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(1, G6.main([*arguments, "--workspace", str(workspace)]))
                    audit.assert_not_called()
                    validate.assert_not_called()


if __name__ == "__main__":
    unittest.main()
