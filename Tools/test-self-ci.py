#!/usr/bin/env python3
"""Prove required CI validation and Release's exact-commit CI dependency."""

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
    def test_release_delegates_source_validation_to_exact_commit_ci(self) -> None:
        release = WORKFLOW.with_name("build-release.yml").read_text()
        self.assertIn("    needs: verify-source\n", release)
        self.assertEqual(release.count("run: bash ./Tools/verify-g4-release-source.sh"), 2)
        self.assertIn("      id: approved_source\n", release)
        self.assertIn("HEXALITH_G4_SOURCE_CI_RUN_ID: ${{ steps.approved_source.outputs.ci-run-id }}", release)
        self.assertIn("      uses: ./Github/create-release\n", release)
        self.assertIn("uses: ./Github/initialize-dotnet", release)
        self.assertNotIn("dotnet-version:", release)
        self.assertNotIn("timeout-minutes", release)
        self.assertNotIn("-File ./Tools/test-", release)
        self.assertNotIn("-File ./Tools/validate-", release)
        self.assertNotIn("0.0.0-ci.", release)
        self.assertNotIn("npm ci", release)
        self.assertNotIn("actions/setup-node@", release)
        ci = WORKFLOW.read_text()
        self.assertIn("run: bash ./Tools/test-g4-release-source.sh", ci)
        self.assertIn("run: pwsh -NoProfile -File ./Tools/test-g4-source-ci.ps1", ci)

    def test_parallel_jobs_gate_required_status(self) -> None:
        text = WORKFLOW.read_text(encoding="utf-8")
        match = re.search(r"(?ms)^  build-and-test:\n(.*?)(?=^  [\w-]+:|\Z)", text)
        self.assertIsNotNone(match)
        block = match[1]
        self.assertIn("    name: Build and test\n", block)
        self.assertIn("    needs: [catalog, contracts, dotnet, python-tests]\n", block)
        self.assertIn("    if: always()\n", block)
        self.assertIn("CATALOG_RESULT: ${{ needs.catalog.result }}", block)
        self.assertIn("CONTRACTS_RESULT: ${{ needs.contracts.result }}", block)
        self.assertIn("DOTNET_RESULT: ${{ needs.dotnet.result }}", block)
        self.assertIn("PYTHON_RESULT: ${{ needs.python-tests.result }}", block)
        body = re.search(r"(?ms)^        run: \|\n(.*)", block)
        self.assertIsNotNone(body)
        script = textwrap.dedent(body[1])
        for catalog, contracts, dotnet, python in itertools.product(
            ("success", "failure", "cancelled", "skipped", ""), repeat=4
        ):
            with self.subTest(catalog=catalog, contracts=contracts, dotnet=dotnet, python=python):
                environment = os.environ.copy()
                environment.update(CATALOG_RESULT=catalog, CONTRACTS_RESULT=contracts, DOTNET_RESULT=dotnet, PYTHON_RESULT=python)
                result = subprocess.run(["bash", "-e", "-c", script], env=environment, check=False)
                self.assertEqual(result.returncode == 0, catalog == contracts == dotnet == python == "success")


if __name__ == "__main__":
    unittest.main()
