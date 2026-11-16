"""Keep packaged catalog snapshots independent from actual source-project inspection."""
from pathlib import Path
import json
import os
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from evaluated_catalog import evaluate_catalog, project_sdk_selections

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "Props/Directory.Packages.props"


class EvaluatedCatalogModeTests(unittest.TestCase):
    def test_timesheets_package_roles_keep_the_net10_dependency_contract(self):
        roles = ("Hexalith.Timesheets", "Hexalith.Timesheets.AppHost", "Hexalith.Timesheets.ArchitectureTests",
                 "Hexalith.Timesheets.Client", "Hexalith.Timesheets.Contracts", "Hexalith.Timesheets.Contracts.Tests",
                 "Hexalith.Timesheets.IntegrationTests", "Hexalith.Timesheets.Projections", "Hexalith.Timesheets.Projections.Tests",
                 "Hexalith.Timesheets.ReadModels", "Hexalith.Timesheets.Server", "Hexalith.Timesheets.Server.Tests",
                 "Hexalith.Timesheets.ServiceDefaults", "Hexalith.Timesheets.Testing", "Hexalith.Timesheets.Works",
                 "Hexalith.Timesheets.Works.Tests")
        expected = {"Hexalith.Commons.UniqueIds": "2.30.11", "Hexalith.PolymorphicSerializations": "1.19.9",
                    "Hexalith.EventStore.Client": "4.2.0", "Hexalith.Tenants.Contracts": "7.0.7",
                    "Microsoft.AspNetCore.Mvc.Testing": "10.0.15", "Microsoft.AspNetCore.TestHost": "10.0.15",
                    "Microsoft.AspNetCore.Authentication.JwtBearer": "10.0.15",
                    "Microsoft.AspNetCore.OpenApi": "10.0.15", "Microsoft.OpenApi": "2.13.0"}
        for role in roles:
            with self.subTest(role=role):
                properties, versions = evaluate_catalog(CATALOG, target_framework="net10.0", consumer_project_name=role)
                self.assertEqual("true", properties["HexalithTimesheetsNet10CatalogView"])
                for package, version in expected.items():
                    self.assertEqual(version, versions[package], package)

    def test_timesheets_catalog_view_does_not_capture_other_or_future_projects(self):
        for role in ("Unrelated", "Hexalith.McpCli", "Hexalith.Works.Contracts", "Hexalith.Timesheets.Works.Future",
                     "hexalith.timesheets", "Hexalith.TimesheetsExtra"):
            with self.subTest(role=role):
                properties, versions = evaluate_catalog(CATALOG, consumer_project_name=role)
                self.assertEqual("false", properties["HexalithTimesheetsNet10CatalogView"])
                self.assertEqual("3.0.0", versions["Hexalith.Commons.UniqueIds"])
                self.assertEqual("2.0.0", versions["Hexalith.PolymorphicSerializations"])
                self.assertEqual("5.2.0", versions["Hexalith.EventStore.Client"])
                self.assertEqual("8.0.1", versions["Hexalith.Tenants.Contracts"])
                for package in ("Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.AspNetCore.Mvc.Testing", "Microsoft.AspNetCore.TestHost"):
                    self.assertEqual("11.0.0", versions[package])

    def test_explicit_timesheets_cohort_and_view_selections_remain_authoritative(self):
        with tempfile.TemporaryDirectory(prefix="catalog-timesheets-explicit-") as directory:
            root = Path(directory)
            for name, options, expected in (
                    ("Hexalith.Timesheets", {"HexalithCommonsVersion": "3.0.0", "HexalithEventStoreVersion": "5.2.0"}, "10.0.15"),
                    ("Hexalith.Timesheets", {"HexalithTimesheetsNet10CatalogView": "false"}, "11.0.0"),
                    ("Unrelated", {"HexalithTimesheetsNet10CatalogView": "true"}, "10.0.15")):
                with self.subTest(name=name, options=options):
                    project = root / (name + ".proj")
                    import xml.etree.ElementTree as ET
                    document = ET.Element("Project")
                    ET.SubElement(document, "Import", Project=str(CATALOG))
                    ET.ElementTree(document).write(project, encoding="utf-8")
                    result = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-noAutoResponse", "-nr:false",
                                             "-p:UseHexalithProjectReferences=false", "-getItem:PackageVersion",
                                             *["-p:" + key + "=" + value for key, value in options.items()]],
                                            check=True, capture_output=True, text=True, timeout=60)
                    versions = {item["Identity"]: item["Version"] for item in json.loads(result.stdout)["Items"]["PackageVersion"]}
                    for package in ("Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.AspNetCore.Mvc.Testing", "Microsoft.AspNetCore.TestHost"):
                        self.assertEqual(expected, versions[package])
                    if "HexalithCommonsVersion" in options:
                        self.assertEqual(options["HexalithCommonsVersion"], versions["Hexalith.Commons.UniqueIds"])
                        self.assertEqual(options["HexalithEventStoreVersion"], versions["Hexalith.EventStore.Gateway"])

    def test_named_package_snapshots_ignore_inherited_source_mode(self):
        with patch.dict(os.environ, {"UseHexalithProjectReferences": "true"}):
            for name in ("Hexalith.Parties", "Hexalith.Platform.EventStoreHost", "Hexalith.Builds.Module.EventStoreHost"):
                with self.subTest(name=name):
                    properties, versions = evaluate_catalog(CATALOG, consumer_project_name=name)
                    self.assertEqual("4.2.0", properties["HexalithEventStoreVersion"])
                    self.assertEqual("10.0.15", versions["Microsoft.AspNetCore.OpenApi"])
                    self.assertEqual("2.13.0", versions["Microsoft.OpenApi"])
                    self.assertEqual("2.0.1", versions["Hexalith.McpCli.Abstractions"])

    def test_unknown_snapshot_retains_the_unqualified_current_catalog(self):
        _, versions = evaluate_catalog(CATALOG, consumer_project_name="Hexalith.Platform.EventStoreHost.Future")
        self.assertEqual("5.2.0", versions["Hexalith.EventStore.Gateway"])
        self.assertEqual("3.0.0", versions["Hexalith.McpCli.Abstractions"])
        self.assertEqual("11.0.0", versions["Microsoft.AspNetCore.OpenApi"])
        self.assertEqual("3.11.0", versions["Microsoft.OpenApi"])

    def test_actual_named_source_project_evaluation_uses_the_source_abi(self):
        with tempfile.TemporaryDirectory(prefix="catalog-source-project-") as directory:
            project = Path(directory) / "Hexalith.Platform.EventStoreHost.proj"
            import xml.etree.ElementTree as ET
            document = ET.Element("Project")
            ET.SubElement(document, "Import", Project=str(CATALOG))
            ET.ElementTree(document).write(project, encoding="utf-8")
            result = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", "-noAutoResponse", "-nr:false",
                                     "-p:UseHexalithProjectReferences=true", "-getItem:PackageVersion"],
                                    check=True, capture_output=True, text=True, timeout=60)
            versions = {item["Identity"]: item["Version"] for item in json.loads(result.stdout)["Items"]["PackageVersion"]}
            self.assertEqual("11.0.0", versions["Microsoft.AspNetCore.OpenApi"])
            self.assertEqual("3.11.0", versions["Microsoft.OpenApi"])
            self.assertEqual("5.2.0", versions["Hexalith.EventStore.Gateway"])
            self.assertEqual("3.0.0", versions["Hexalith.McpCli.Abstractions"])

    def test_project_sdk_inspection_preserves_its_source_context(self):
        with tempfile.TemporaryDirectory(prefix="catalog-source-inspection-") as directory:
            root = Path(directory)
            (root / "Directory.Build.props").write_text("""<Project><PropertyGroup>
<HexalithAspireAppHostSdkVersion Condition="'$(UseHexalithProjectReferences)' == 'true'">13.6.5</HexalithAspireAppHostSdkVersion>
<HexalithAspireAppHostSdkVersion Condition="'$(UseHexalithProjectReferences)' != 'true'">1.0.0</HexalithAspireAppHostSdkVersion>
</PropertyGroup></Project>""")
            project = root / "Source.AppHost.csproj"
            project.write_text('<Project Sdk="Aspire.AppHost.Sdk/$(HexalithAspireAppHostSdkVersion)" />')
            selections = project_sdk_selections(project)
            self.assertEqual([{"Id": "Aspire.AppHost.Sdk", "Version": "13.6.5", "CatalogSelected": True}], selections)


if __name__ == "__main__":
    unittest.main()
