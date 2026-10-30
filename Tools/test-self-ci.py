#!/usr/bin/env python3
"""Prove the required CI status fails when either parallel validation fails."""

from __future__ import annotations

import itertools
import os
import pathlib
import re
import subprocess
import textwrap
import unittest


WORKFLOW = pathlib.Path(__file__).resolve().parents[1] / ".github/workflows/ci.yml"


class RequiredStatusTests(unittest.TestCase):
    def test_parallel_jobs_gate_required_status(self) -> None:
        text = WORKFLOW.read_text(encoding="utf-8")
        match = re.search(r"(?ms)^  build-and-test:\n(.*?)(?=^  [\w-]+:|\Z)", text)
        self.assertIsNotNone(match)
        block = match[1]
        self.assertIn("    name: Build and test\n", block)
        self.assertIn("    needs: [contracts, dotnet]\n", block)
        self.assertIn("    if: always()\n", block)
        self.assertIn("CONTRACTS_RESULT: ${{ needs.contracts.result }}", block)
        self.assertIn("DOTNET_RESULT: ${{ needs.dotnet.result }}", block)
        body = re.search(r"(?ms)^        run: \|\n(.*)", block)
        self.assertIsNotNone(body)
        script = textwrap.dedent(body[1])
        for contracts, dotnet in itertools.product(
            ("success", "failure", "cancelled", "skipped", ""), repeat=2
        ):
            with self.subTest(contracts=contracts, dotnet=dotnet):
                environment = os.environ.copy()
                environment.update(CONTRACTS_RESULT=contracts, DOTNET_RESULT=dotnet)
                result = subprocess.run(["bash", "-e", "-c", script], env=environment, check=False)
                self.assertEqual(result.returncode == 0, contracts == dotnet == "success")


if __name__ == "__main__":
    unittest.main()
