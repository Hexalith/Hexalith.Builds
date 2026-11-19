"""Aspire.AppHost.Sdk's implicit AppHost reference takes the catalog pin instead of failing with NU1009."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import escape


CATALOG = Path(__file__).resolve().parents[1] / "Props/Directory.Packages.props"
PACKAGE = "Aspire.Hosting.AppHost"
# Mirrors AddImplicitAspireAppHostPackage from Aspire.AppHost.Sdk 17.0.0 Sdk/Sdk.targets.
SDK_TARGET = """
  <Target Name="AddImplicitAspireAppHostPackage" BeforeTargets="Restore;CollectPackageReferences" Condition="'$(SkipAddAspireDefaultReferences)' != 'true'">
    <PropertyGroup>
      <_ImplicitAppHostVersion>17.0.0</_ImplicitAppHostVersion>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Aspire.Hosting.AppHost" Version="$(_ImplicitAppHostVersion)" IsImplicitlyDefined="true"
                        Condition="'@(PackageReference->WithMetadataValue('Identity','Aspire.Hosting.AppHost'))' == ''" />
    </ItemGroup>
  </Target>
"""
EXPLICIT = f'<ItemGroup><PackageReference Include="{PACKAGE}" Marker="explicit" /></ItemGroup>'
SKIP = "<PropertyGroup><SkipAddAspireDefaultReferences>true</SkipAddAspireDefaultReferences></PropertyGroup>"


class AspireAppHostCatalogReferenceTests(unittest.TestCase):
    def evaluate(self, body: str, target: str | None) -> dict:
        with tempfile.TemporaryDirectory(prefix="aspire-apphost-catalog-") as temporary:
            project = Path(temporary) / "AppHost.proj"
            project.write_text(f'<Project><Import Project="{escape(str(CATALOG))}" />{body}</Project>', encoding="utf-8")
            environment = {key: value for key, value in os.environ.items() if not key.casefold().startswith("hexalith")}
            arguments = ["dotnet", "msbuild", str(project), "-nologo", "-noAutoResponse",
                         "-getItem:PackageReference", "-getItem:PackageVersion"]
            if target:
                arguments.append(f"-t:{target}")
            result = subprocess.run(arguments, cwd=CATALOG.parents[1], env=environment,
                                    capture_output=True, text=True, timeout=60, check=False)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            return json.loads(result.stdout)

    @staticmethod
    def rows(result: dict, item: str) -> list[dict]:
        return [row for row in result["Items"].get(item, []) if row["Identity"].casefold() == PACKAGE.casefold()]

    def test_catalog_keeps_the_central_pin(self) -> None:
        result = self.evaluate("", None)
        self.assertEqual(["17.0.0"], [row["Version"] for row in self.rows(result, "PackageVersion")])
        self.assertEqual([], self.rows(result, "PackageReference"))

    def test_implicit_sdk_reference_becomes_a_central_reference(self) -> None:
        result = self.evaluate(SDK_TARGET, "AddImplicitAspireAppHostPackage")
        references = self.rows(result, "PackageReference")
        self.assertEqual(1, len(references), references)
        self.assertNotEqual("true", references[0].get("IsImplicitlyDefined", "").casefold())
        self.assertEqual("", references[0].get("Version", ""))
        self.assertEqual(["17.0.0"], [row["Version"] for row in self.rows(result, "PackageVersion")])

    def test_explicit_references_are_unchanged(self) -> None:
        for name, body in (("explicit", SDK_TARGET + EXPLICIT), ("skip and explicit", SDK_TARGET + SKIP + EXPLICIT)):
            with self.subTest(style=name):
                references = self.rows(self.evaluate(body, "AddImplicitAspireAppHostPackage"), "PackageReference")
                self.assertEqual(["explicit"], [row.get("Marker", "") for row in references])
                self.assertEqual("", references[0].get("IsImplicitlyDefined", ""))

    def test_skip_without_reference_adds_nothing(self) -> None:
        result = self.evaluate(SDK_TARGET + SKIP + '<Target Name="Probe" DependsOnTargets="AddImplicitAspireAppHostPackage" />', "Probe")
        self.assertEqual([], self.rows(result, "PackageReference"))

    def test_projects_without_the_sdk_keep_their_reference(self) -> None:
        references = self.rows(self.evaluate(EXPLICIT, "HexalithUseCatalogAspireAppHostPackage"), "PackageReference")
        self.assertEqual(["explicit"], [row.get("Marker", "") for row in references])


if __name__ == "__main__":
    unittest.main()
