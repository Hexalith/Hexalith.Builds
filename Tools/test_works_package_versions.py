"""Check the shared catalog against read-only Works CPM wrappers using MSBuild evaluation."""

from __future__ import annotations

import collections
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from xml.sax.saxutils import escape


PINS = {
    "Microsoft.Build": "18.9.6",
    "Microsoft.Build.Framework": "18.9.6",
    "Microsoft.Build.Locator": "1.11.2",
}
CATALOG = Path(__file__).resolve().parents[1] / "Props/Directory.Packages.props"


class WorksPackageVersionTests(unittest.TestCase):
    """Protect uniqueness, ownership, unchanged pins, and explicit catalog selection."""

    def test_evaluated_wrapper_compatibility(self) -> None:
        with tempfile.TemporaryDirectory(prefix="works-package-versions-") as temporary:
            root = Path(temporary)
            works = root / "renamed-checkout"
            other = root / "other-consumer"
            works.mkdir()
            other.mkdir()
            (works / "Hexalith.Works.slnx").write_text("<Solution />", encoding="utf-8")
            shared_import = f'<Import Project="{escape(str(CATALOG))}" />'
            local_pins = "".join(
                f'<PackageVersion Include="{name}" Version="{version}" />'
                for name, version in PINS.items()
            )
            wrapper = works / "Directory.Packages.props"
            wrapper.write_text(
                f"<Project>{shared_import}<ItemGroup>{local_pins}</ItemGroup></Project>",
                encoding="utf-8",
            )
            alternate = other / "Directory.Packages.props"
            alternate.write_text(f"<Project>{shared_import}</Project>", encoding="utf-8")
            projects = {}
            for owner, name in [(works, "Hexalith.Works.Contracts"), (works, "Consumer"),
                                (other, "Hexalith.Workshop")]:
                directory = owner / "tests" / name
                directory.mkdir(parents=True)
                project = directory / f"{name}.proj"
                project.write_text(
                    '<Project><Import Project="$(DirectoryPackagesPropsPath)" /></Project>',
                    encoding="utf-8",
                )
                projects[name] = project

            cases = [
                (name, configuration, mode, str(wrapper), wrapper, None)
                for name in ["Hexalith.Works.Contracts", "Consumer"]
                for configuration, mode in [("Debug", "true"), ("Release", "false")]
            ]
            cases += [
                ("Consumer", "Debug", "true", str(works) + "/./Directory.Packages.props", wrapper, None),
                ("Consumer", "Release", "false", str(works) + "/../renamed-checkout/Directory.Packages.props", wrapper, None),
                ("Consumer", "Debug", "true", str(alternate), CATALOG, None),
                ("Hexalith.Workshop", "Debug", "true", str(alternate), CATALOG, None),
                ("Consumer", "Debug", "true", str(wrapper), wrapper, "3.130.0"),
            ]
            for name, configuration, mode, selected, expected_owner, version in cases:
                with self.subTest(project=name, configuration=configuration, selected=selected, version=version):
                    command = [
                        "dotnet", "msbuild", str(projects[name]), "-nologo",
                        f"-p:Configuration={configuration}",
                        f"-p:UseHexalithProjectReferences={mode}",
                        f"-p:DirectoryPackagesPropsPath={selected}",
                        "-getProperty:HexalithEventStoreVersion", "-getItem:PackageVersion",
                    ]
                    if version:
                        command.append(f"-p:HexalithEventStoreVersion={version}")
                    result = subprocess.run(command, cwd=CATALOG.parents[1], capture_output=True,
                                            text=True, timeout=60, check=False)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    evaluated = json.loads(result.stdout)
                    items = evaluated["Items"]["PackageVersion"]
                    counts = collections.Counter(item["Identity"].casefold() for item in items)
                    self.assertTrue(all(count == 1 for count in counts.values()), counts)
                    for package, pin in PINS.items():
                        item, = [item for item in items if item["Identity"] == package]
                        self.assertEqual(item["Version"], pin)
                        self.assertEqual(Path(item["DefiningProjectFullPath"]).resolve(), expected_owner)
                    if version:
                        self.assertEqual(evaluated["Properties"]["HexalithEventStoreVersion"], version)
                        eventstore = [item for item in items if item["Identity"].startswith("Hexalith.EventStore.")]
                        self.assertTrue(eventstore)
                        self.assertTrue(all(item["Version"] == version for item in eventstore))


if __name__ == "__main__":
    unittest.main()
