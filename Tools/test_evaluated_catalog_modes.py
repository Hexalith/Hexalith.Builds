"""Keep packaged catalog snapshots independent from actual source-project inspection."""
from pathlib import Path
import os
import tempfile
import unittest
from unittest.mock import patch

from evaluated_catalog import evaluate_catalog, project_sdk_selections

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "Props/Directory.Packages.props"


class EvaluatedCatalogModeTests(unittest.TestCase):
    def test_named_consumers_use_the_current_package_families(self):
        _, baseline = evaluate_catalog(CATALOG)
        for name in ("Hexalith.Timesheets", "Hexalith.Parties", "Hexalith.Platform.EventStoreHost",
                     "Hexalith.Builds.Module.EventStoreHost", "Hexalith.FrontComposer.Shell", "Unrelated"):
            with self.subTest(name=name), patch.dict(os.environ, {"UseHexalithProjectReferences": "true"}):
                properties, versions = evaluate_catalog(CATALOG, consumer_project_name=name)
                self.assertEqual(baseline, versions)
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
