"""Keep packaged catalog snapshots independent from actual source-project inspection."""
from pathlib import Path
import os
import runpy
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from evaluated_catalog import evaluate_catalog, project_sdk_selections
from runtime_toolchain_v2 import inventory

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "Props/Directory.Packages.props"


class EvaluatedCatalogModeTests(unittest.TestCase):
    def test_inventory_applies_imported_equals_selector_to_exact_consumer(self):
        api = SimpleNamespace(**runpy.run_path(str(ROOT / "Tools/validate-runtime-toolchain-evidence.py")))
        package = "Hexalith.EventStore.Client"
        with tempfile.TemporaryDirectory(prefix="catalog-inventory-") as directory:
            root = Path(directory)
            catalog_dir = root / "references/Hexalith.Builds/Props"
            catalog_dir.mkdir(parents=True)
            (catalog_dir / "Directory.Packages.props").write_text(f"""<Project>
  <PropertyGroup><HexalithEventStoreVersion>5.0.0</HexalithEventStoreVersion></PropertyGroup>
  <Import Project="Retained.props" Condition="'$(UseHexalithProjectReferences)' == 'false'" />
  <ItemGroup><PackageVersion Include="{package}" Version="$(HexalithEventStoreVersion)" /></ItemGroup>
</Project>""")
            (catalog_dir / "Retained.props").write_text("""<Project>
  <PropertyGroup Condition="$(MSBuildProjectName.Equals('_Legacy Consumer'))">
    <HexalithEventStoreVersion>4.2.0</HexalithEventStoreVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Quoted.Consumer'">
    <HexalithEventStoreVersion>4.2.1</HexalithEventStoreVersion>
  </PropertyGroup>
</Project>""")
            consumers = root / "Consumers"
            retained = "Consumers/retained/_Legacy Consumer.csproj"
            current = "Consumers/current/_legacy Consumer.csproj"
            quoted = "Consumers/quoted/quoted.Consumer.csproj"
            for relative in (retained, current, quoted):
                project = root / relative
                project.parent.mkdir(parents=True)
                project.write_text(f'<Project><ItemGroup><PackageReference Include="{package}" /></ItemGroup></Project>')
            subprocess.run(["git", "init", "--quiet", str(consumers)], check=True)
            baseline = {
                "consumerInventory": {
                    "roots": ["Consumers"],
                    "unqualifiedExclusions": [
                        {"path": retained, "package": package, "version": "4.2.0"},
                        {"path": quoted, "package": package, "version": "4.2.1"},
                    ],
                    "fileBasedAppHosts": [],
                },
                "qualification": {"eventStorePackageVersion": "5.0.0"},
            }
            self.assertEqual([
                {"path": current, "pins": [{"package": package, "version": "5.0.0", "qualified": True}]},
                {"path": quoted, "pins": [{"package": package, "version": "4.2.1", "qualified": False}]},
                {"path": retained, "pins": [{"package": package, "version": "4.2.0", "qualified": False}]},
            ], inventory(api, root, baseline))

    def test_named_consumers_use_the_current_package_families(self):
        _, baseline = evaluate_catalog(CATALOG)
        for name in ("Hexalith.Timesheets", "Hexalith.Parties", "Hexalith.Platform.EventStoreHost",
                     "Hexalith.Builds.Module.EventStoreHost", "Hexalith.FrontComposer.Shell", "Unrelated"):
            with self.subTest(name=name), patch.dict(os.environ, {"UseHexalithProjectReferences": "true"}):
                properties, versions = evaluate_catalog(CATALOG, consumer_project_name=name)
                expected = baseline
                if name == "Hexalith.Platform.EventStoreHost":
                    expected = baseline | {"Hexalith.Platform.TestExtension": "2.2.5"}
                self.assertEqual(expected, versions)
                self.assertEqual(properties["HexalithEventStoreVersion"], versions["Hexalith.EventStore.Client"])

    def test_project_sdk_inspection_preserves_its_source_context(self):
        with tempfile.TemporaryDirectory(prefix="catalog-source-inspection-") as directory:
            root = Path(directory)
            (root / "Directory.Build.props").write_text("""<Project><PropertyGroup>
<HexalithAspireAppHostSdkVersion Condition="'$(UseHexalithProjectReferences)' == 'true'">17.0.0</HexalithAspireAppHostSdkVersion>
<HexalithAspireAppHostSdkVersion Condition="'$(UseHexalithProjectReferences)' != 'true'">1.0.0</HexalithAspireAppHostSdkVersion>
</PropertyGroup></Project>""")
            project = root / "Source.AppHost.csproj"
            project.write_text('<Project Sdk="Aspire.AppHost.Sdk/$(HexalithAspireAppHostSdkVersion)" />')
            selections = project_sdk_selections(project)
            self.assertEqual([{"Id": "Aspire.AppHost.Sdk", "Version": "17.0.0", "CatalogSelected": True}], selections)


if __name__ == "__main__":
    unittest.main()
