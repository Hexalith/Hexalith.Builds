#!/usr/bin/env python3
"""Exercise shared README packing with real MSBuild evaluation and NuGet archives."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape


BUILDS = Path(__file__).resolve().parents[1]


class PackageReadmeTests(unittest.TestCase):
    def project(self, directory: Path, package_path: str | None, *, missing: bool = False,
                shared_identity: bool = False, omitted_package_path: bool = False) -> Path:
        module = directory / "module"
        module.mkdir()
        if not missing:
            (module / "README.md").write_bytes(b"Commons-owned module README\n")
        inherited = directory / "inherited"
        inherited.mkdir()
        (inherited / "README.md").write_bytes(b"Enclosing umbrella README\n")
        docs = directory / "docs"
        docs.mkdir()
        (docs / "README.md").write_bytes(b"Preserved nested README\n")
        unpacked = directory / "unpacked"
        unpacked.mkdir()
        (unpacked / "README.md").write_bytes(b"Preserved unpacked README\n")
        (inherited / "README.txt").write_bytes(b"Preserved other file\n")
        root_item = (f'<None Include="{escape(str(inherited / "README.md"))}" Pack="true" PackagePath="{package_path}" />'
                     if package_path is not None else "")
        preserved_items = ""
        if shared_identity:
            identity = escape(str(inherited / "README.md"))
            preserved_items += (f'<None Include="{identity}" Pack="true" PackagePath="shared/" />'
                                f'<None Include="{identity}" Pack="false" PackagePath="{package_path}" />')
        if omitted_package_path:
            omitted = directory / "omitted"
            omitted.mkdir()
            (omitted / "README.md").write_bytes(b"Preserved implicit content README\n")
            preserved_items += f'<None Include="{escape(str(omitted / "README.md"))}" Pack="true" />'
        project = directory / "fixture.csproj"
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Configuration>Debug</Configuration>
    <ProjectRoot>{escape(str(module))}</ProjectRoot>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableDefaultItems>false</EnableDefaultItems>
  </PropertyGroup>
  <ItemGroup>
    {root_item}
    {preserved_items}
    <None Include="{escape(str(docs / "README.md"))}" Pack="true" PackagePath="docs/" />
    <None Include="{escape(str(unpacked / "README.md"))}" Pack="false" PackagePath="/" />
    <None Include="{escape(str(inherited / "README.txt"))}" Pack="true" PackagePath="/" />
  </ItemGroup>
  <Import Project="{escape(str(BUILDS / "Hexalith.Package.props"))}" />
</Project>
''', encoding="utf-8")
        return project

    def invoke(self, directory: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
        removed = {"CI", "GITHUB_ACTIONS", "TF_BUILD", "TERM_PROGRAM", "IDEBUILD",
                   "BUILDINGBYRESHARPER", "BUILDINGINSIDEVISUALSTUDIO", "CIBUILD",
                   "CONTINUOUSINTEGRATIONBUILD", "ISPACKABLE", "GENERATEPACKAGEONBUILD"}
        environment = {key: value for key, value in os.environ.items()
                       if key.upper() not in removed and not key.upper().startswith("VSCODE_")}
        environment.update(GITHUB_ACTIONS="true", DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="1",
                           MSBUILDDISABLENODEREUSE="1")
        return subprocess.run(["dotnet", *arguments], cwd=directory, env=environment,
                              text=True, capture_output=True, check=False, timeout=120)

    def check_package(self, package_path: str | None, *, shared_identity: bool = False,
                      omitted_package_path: bool = False) -> None:
        with tempfile.TemporaryDirectory(prefix="hexalith-readme-") as temporary:
            directory = Path(temporary)
            project = self.project(directory, package_path, shared_identity=shared_identity,
                                   omitted_package_path=omitted_package_path)
            evaluated = self.invoke(directory, "msbuild", str(project), "-getItem:None")
            self.assertEqual(0, evaluated.returncode, evaluated.stdout + evaluated.stderr)
            items = json.loads(evaluated.stdout)["Items"]["None"]
            identities = {Path(item["Identity"]).resolve() for item in items}
            if shared_identity:
                shared = [item for item in items if Path(item["Identity"]).resolve() == directory / "inherited/README.md"]
                self.assertEqual(2, len(shared))
                self.assertEqual({("true", "shared/"), ("false", package_path.replace("\\", "/"))},
                                 {(item["Pack"], item["PackagePath"]) for item in shared})
            else:
                self.assertNotIn(directory / "inherited/README.md", identities)
            if omitted_package_path:
                self.assertIn(directory / "omitted/README.md", identities)
            self.assertIn(directory / "docs/README.md", identities)
            self.assertIn(directory / "unpacked/README.md", identities)
            self.assertIn(directory / "inherited/README.txt", identities)
            built = self.invoke(directory, "build", str(project), "--configuration", "Debug", "--no-incremental", "-m:1", "-v:minimal")
            self.assertEqual(0, built.returncode, built.stdout + built.stderr)
            packages = list((directory / "bin/Debug").glob("*.nupkg"))
            self.assertEqual(1, len(packages), built.stdout + built.stderr)
            with zipfile.ZipFile(packages[0]) as archive:
                self.assertEqual(1, archive.namelist().count("README.md"))
                self.assertEqual((directory / "module/README.md").read_bytes(), archive.read("README.md"))
                self.assertEqual((directory / "docs/README.md").read_bytes(), archive.read("docs/README.md"))
                self.assertEqual((directory / "inherited/README.txt").read_bytes(), archive.read("README.txt"))
                self.assertNotIn(b"Preserved unpacked README", [archive.read(name) for name in archive.namelist()])
                if shared_identity:
                    self.assertEqual((directory / "inherited/README.md").read_bytes(), archive.read("shared/README.md"))
                if omitted_package_path:
                    implicit_content = [name for name in archive.namelist() if name.startswith("content/") and name.endswith("README.md")]
                    self.assertEqual(1, len(implicit_content), archive.namelist())
                    source = directory / "omitted/README.md"
                    self.assertEqual(source.read_bytes(), archive.read(implicit_content[0]))
                    preserved = [name for name in archive.namelist() if archive.read(name) == source.read_bytes()]
                    print(f"README-DEFAULT-CONTENT: omitted PackagePath: {', '.join(preserved)}", flush=True)

    def test_forward_slash_root_package_path_is_replaced(self) -> None:
        self.check_package("/")

    def test_backslash_root_package_path_is_replaced(self) -> None:
        self.check_package("\\")

    def test_standalone_module_readme_is_packed(self) -> None:
        self.check_package(None)

    def test_same_source_root_docs_and_unpacked_items_are_distinguished(self) -> None:
        for package_path in ("/", "\\"):
            with self.subTest(package_path=package_path):
                self.check_package(package_path, shared_identity=True)

    def test_omitted_package_path_preserves_default_content_packaging(self) -> None:
        self.check_package("/", omitted_package_path=True)

    def test_missing_module_readme_remains_a_pack_failure(self) -> None:
        with tempfile.TemporaryDirectory(prefix="hexalith-readme-missing-") as temporary:
            directory = Path(temporary)
            project = self.project(directory, "/", missing=True)
            result = self.invoke(directory, "build", str(project), "--configuration", "Debug", "-m:1", "-v:minimal")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("NU5019", result.stdout + result.stderr)
            self.assertIn("README.md", result.stdout + result.stderr)
            self.assertFalse(list((directory / "bin/Debug").glob("*.nupkg")))


if __name__ == "__main__":
    unittest.main()
