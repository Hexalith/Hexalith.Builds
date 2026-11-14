"""Evaluate the exact FrontComposer net10 Immutable view and the shared net11 default."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import escape


CATALOG = Path(__file__).resolve().parents[1] / "Props/Directory.Packages.props"
PROJECTS = (
    "Counter.Domain",
    "Counter.Specimens",
    "Counter.Specimens.Domain",
    "Counter.Web",
    "Hexalith.FrontComposer.AppHost",
    "Hexalith.FrontComposer.Cli",
    "Hexalith.FrontComposer.Cli.Tests",
    "Hexalith.FrontComposer.Contracts",
    "Hexalith.FrontComposer.Contracts.Tests",
    "Hexalith.FrontComposer.Contracts.UI",
    "Hexalith.FrontComposer.Contracts.UI.Tests",
    "Hexalith.FrontComposer.CounterFixture",
    "Hexalith.FrontComposer.Mcp",
    "Hexalith.FrontComposer.Mcp.Tests",
    "Hexalith.FrontComposer.Schema",
    "Hexalith.FrontComposer.Shell",
    "Hexalith.FrontComposer.Shell.Tests",
    "Hexalith.FrontComposer.Shell.Tests.Bench",
    "Hexalith.FrontComposer.SourceTools",
    "Hexalith.FrontComposer.SourceTools.Tests",
    "Hexalith.FrontComposer.Testing",
    "Hexalith.FrontComposer.Testing.Tests",
    "Hexalith.FrontComposer.UI",
    "Hexalith.FrontComposer.UI.Tests",
    "IdeParityCounter",
)


class FrontComposerImmutableViewTests(unittest.TestCase):
    def evaluate(self, name: str, configuration: str) -> dict:
        with tempfile.TemporaryDirectory(prefix="frontcomposer-immutable-view-") as temporary:
            project = Path(temporary) / f"{name}.proj"
            project.write_text(f'<Project><Import Project="{escape(str(CATALOG))}" /></Project>', encoding="utf-8")
            environment = {key: value for key, value in os.environ.items() if not key.casefold().startswith("hexalith")}
            result = subprocess.run(
                ["dotnet", "msbuild", str(project), "-nologo", "-noAutoResponse",
                 f"-p:Configuration={configuration}", "-getProperty:HexalithFrontComposerImmutable10CatalogView,HexalithImmutableVersion,CentralPackageVersionOverrideEnabled,ManagePackageVersionsCentrally",
                 "-getItem:PackageVersion"], cwd=CATALOG.parents[1], env=environment,
                capture_output=True, text=True, timeout=60, check=False,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            return json.loads(result.stdout)

    def assert_view(self, name: str, configuration: str, expected: str) -> None:
        result = self.evaluate(name, configuration)
        rows = result["Items"]["PackageVersion"]
        immutable = [row["Version"] for row in rows if row["Identity"].casefold() == "system.collections.immutable"]
        self.assertEqual([expected], immutable)
        properties = result["Properties"]
        self.assertEqual(expected, properties["HexalithImmutableVersion"])
        self.assertEqual("true" if expected == "10.0.15" else "false", properties["HexalithFrontComposerImmutable10CatalogView"])
        self.assertEqual("false", properties["CentralPackageVersionOverrideEnabled"])
        self.assertEqual("true", properties["ManagePackageVersionsCentrally"])
        self.assertEqual(len(rows), len({row["Identity"].casefold() for row in rows}))

    def test_exact_frontcomposer_consumers_select_net10_in_debug_and_release(self) -> None:
        for name in PROJECTS:
            for configuration in ("Debug", "Release"):
                with self.subTest(project=name, configuration=configuration):
                    self.assert_view(name, configuration, "10.0.15")

    def test_other_and_near_miss_consumers_keep_net11_default(self) -> None:
        for name in ("Hexalith.Builds.Tooling", "Hexalith.Builds.Module.AppHost", "Hexalith.Parties", "Hexalith.Platform.Composition",
                     "Hexalith.FrontComposer.Unlisted", "Hexalith.FrontComposer.SourceToolsExtra", "AdopterProofKit.Web"):
            for configuration in ("Debug", "Release"):
                with self.subTest(project=name, configuration=configuration):
                    self.assert_view(name, configuration, "11.0.0")


if __name__ == "__main__":
    unittest.main()
