"""Real SDK release evaluation/pack/audit and executed composite controls."""
from __future__ import annotations

from contextlib import redirect_stderr
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_build as build
from ci_pipeline import PipelineError, prepare_config, evaluated_properties

ROOT = Path(__file__).resolve().parents[3]
SHA = "a" * 40
VERSION = "2.4.0"
REPOSITORY = "Hexalith/Hexalith.Builds"


def fixture(root, solution="PackageOnly.slnx"):
    shutil.copytree(ROOT / "test/fixtures/release", root)
    (root / ".github").mkdir()
    config = json.loads((ROOT / ".github/hexalith-ci.json").read_text())
    config.update(solution=solution, codeql=[], tests={"integration": [], "exclude": {}},
                  hooks=[], jobs={}, submodules=[], dapr=False, browsers=False, registry=False)
    (root / ".github/hexalith-ci.json").write_text(json.dumps(config))
    return prepare_config(root, repository=REPOSITORY)


class BuildTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="release-sdk-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.prepared = fixture(self.root / "source with spaces")
        self.flags = build.release_flags(VERSION, SHA, REPOSITORY)

    def test_real_multitarget_import_discovery_and_pack_stamp(self):
        projects = build.discover_projects(self.prepared, self.flags)
        self.assertEqual("Hexalith.Release.CreateMode.Disposable", projects[0].package)
        manifest = build.build_release(self.prepared, self.root / "payload", version=VERSION, sha=SHA,
                                       repository=REPOSITORY)
        package, = manifest["packages"]
        build.validate_package(self.root / "payload" / package["path"], identity=package["id"], version=VERSION, sha=SHA)
        self.assertEqual(manifest, json.loads((self.root / "payload/release-manifest.json").read_text()))
        self.assertEqual([], manifest["containers"])
        with self.assertRaises(PipelineError):
            build.validate_package(self.root / "payload" / package["path"], identity="other", version=VERSION, sha=SHA)

    def test_none_payload_does_not_execute_sdk_or_submodules(self):
        prepared = fixture(self.root / "none", "none")
        (prepared.root / "global.json").unlink()
        with patch.object(build, "initialize_submodules", side_effect=AssertionError("submodule")), \
                patch.object(build, "audited_command", side_effect=AssertionError("SDK")), \
                patch.object(build, "discover_projects", side_effect=AssertionError("discovery")):
            manifest = build.build_release(prepared, self.root / "empty", version=VERSION, sha=SHA, repository=REPOSITORY)
        self.assertEqual({"sha": SHA, "version": VERSION, "packages": [], "containers": []}, manifest)
        self.assertEqual(["release-manifest.json"], [path.name for path in (self.root / "empty").iterdir()])

    def test_no_publishable_project_still_writes_empty_manifest(self):
        path = self.prepared.root / "Package.props"
        path.write_text(path.read_text().replace("<IsPackable>true</IsPackable>", "<IsPackable>false</IsPackable>"))
        manifest = build.build_release(self.prepared, self.root / "unselected", version=VERSION, sha=SHA,
                                       repository=REPOSITORY)
        self.assertEqual([], manifest["packages"])
        self.assertEqual([], manifest["containers"])

    def test_audit_failure_suppresses_payload_manifest(self):
        path = self.prepared.root / "Directory.Build.props"
        path.write_text(path.read_text().replace("</PropertyGroup>",
            "<ReleaseFixtureAuditCode>NU1903</ReleaseFixtureAuditCode></PropertyGroup>"))
        diagnostic = io.StringIO()
        with redirect_stderr(diagnostic), self.assertRaises(PipelineError):
            build.build_release(self.prepared, self.root / "blocked", version=VERSION, sha=SHA, repository=REPOSITORY)
        self.assertIn("v2.4.0 blocked by a vulnerable package", diagnostic.getvalue())
        self.assertEqual([], list((self.root / "blocked").rglob("*")))

    def test_real_nuget_vulnerability_blocks_inherited_exemptions(self):
        path = self.prepared.root / "Package/Release.Package.csproj"
        path.write_text(path.read_text().replace("</Project>",
            '<ItemGroup><PackageReference Include="Newtonsoft.Json" /></ItemGroup></Project>'))
        (self.prepared.root / "Directory.Packages.props").write_text('<Project><PropertyGroup>'
            '<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>'
            '<ItemGroup><PackageVersion Include="Newtonsoft.Json" Version="9.0.1" /></ItemGroup></Project>')
        diagnostic = io.StringIO()
        with redirect_stderr(diagnostic), self.assertRaises(PipelineError):
            build.build_release(self.prepared, self.root / "vulnerable", version=VERSION, sha=SHA, repository=REPOSITORY)
        self.assertIn("v2.4.0 blocked by a vulnerable package", diagnostic.getvalue())
        self.assertFalse((self.root / "vulnerable/release-manifest.json").exists())

    def test_real_sdk_audit_codes_and_inherited_exemptions(self):
        project = str(self.prepared.root / "Package/Release.Package.csproj")
        values = evaluated_properties(self.prepared.root, "Package/Release.Package.csproj",
            properties=("WarningsNotAsErrors", "NoWarn"), flags=self.flags)
        for code in ("NU1900", "NU1903", "NU1904", "NU1905"):
            self.assertNotIn(code, values["WarningsNotAsErrors"])
            self.assertNotIn(code, values["NoWarn"])
        self.assertIn("CS1591", values["WarningsNotAsErrors"])
        for code in ("NU1900", "NU1903", "NU1904", "NU1905", "NU1901", "NU1902"):
            with self.subTest(code=code):
                diagnostic = io.StringIO()
                command = ["dotnet", "restore", project, f"-p:ReleaseFixtureAuditCode={code}", *self.flags]
                with redirect_stderr(diagnostic):
                    if code in ("NU1901", "NU1902"):
                        build.audited_command(command, self.prepared.root, VERSION)
                        self.assertEqual("", diagnostic.getvalue())
                    else:
                        with self.assertRaises(PipelineError) as raised:
                            build.audited_command(command, self.prepared.root, VERSION)
                        self.assertEqual(1, raised.exception.returncode)
                        self.assertIn("v2.4.0 blocked by a vulnerable package", diagnostic.getvalue())

    def test_non_audit_failure_keeps_status_without_vulnerability_diagnostic(self):
        diagnostic = io.StringIO()
        with redirect_stderr(diagnostic), self.assertRaises(PipelineError) as raised:
            build.audited_command([sys.executable, "-c", "print('compiler failure'); raise SystemExit(7)"],
                                  self.prepared.root, VERSION)
        self.assertEqual(7, raised.exception.returncode)
        self.assertEqual("", diagnostic.getvalue())

    def test_duplicate_and_conditional_framework_identity_rejected(self):
        project = self.prepared.root / "Package/Release.Package.csproj"
        text = project.read_text()
        project.write_text(text.replace("</Project>", '<PropertyGroup Condition="\'$(TargetFramework)\' == \'net9.0\'">'
                           '<PackageId>Conflicting</PackageId></PropertyGroup></Project>'))
        with self.assertRaisesRegex(PipelineError, "differ across"):
            build.discover_projects(self.prepared, self.flags)
        project.write_text(text)
        duplicate = project.parent / "Duplicate.csproj"
        duplicate.write_text(text)
        (self.prepared.root / "PackageOnly.slnx").write_text('<Solution><Project Path="Package/Release.Package.csproj" />'
                                                          '<Project Path="Package/Duplicate.csproj" /></Solution>')
        with self.assertRaisesRegex(PipelineError, "duplicate package"):
            build.discover_projects(self.prepared, self.flags)

    def test_container_opt_in_rids_and_test_exclusion(self):
        self.prepared = fixture(self.root / "containers", "Release.slnx")
        projects = build.discover_projects(self.prepared, self.flags)
        container = next(project for project in projects if project.container)
        self.assertEqual(("linux-x64", "linux-arm64"), container.runtimes)
        self.assertEqual("hexalith-release-create-disposable", container.container)
        path = self.prepared.root / container.path
        path.write_text(path.read_text().replace("</PropertyGroup>", "<IsTestProject>true</IsTestProject></PropertyGroup>"))
        self.assertFalse(any(project.container for project in build.discover_projects(self.prepared, self.flags)))
        for rid in ("linux-x64", "linux-musl-arm64", "win-x64", "linux-arm"):
            self.assertIn("architecture", build.platform_for_rid(rid))
        with self.assertRaises(PipelineError):
            build.platform_for_rid("alien-x128")

    def test_real_sdk_complete_container_graph(self):
        self.prepared = fixture(self.root / "container-real", "Release.slnx")
        manifest = build.build_release(self.prepared, self.root / "container-payload", version=VERSION,
                                       sha=SHA, repository=REPOSITORY)
        container, = manifest["containers"]
        graph = build.inspect_archive(self.root / "container-payload" / container["path"], revision=SHA,
            version=VERSION, repository=REPOSITORY, name=container["name"])
        self.assertEqual(["linux/amd64", "linux/arm64"], graph["platforms"])

    def test_executed_build_adapter_frozen_identity_and_digest_handoff(self):
        prepared = fixture(self.root / "adapter", "none")
        for arguments in (["init", "--quiet", "-b", "main"], ["add", "."],
                          ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.com", "commit", "--quiet", "-m", "fixture"]):
            subprocess.run(["git", "-C", str(prepared.root), *arguments], check=True)
        sha = subprocess.check_output(["git", "-C", str(prepared.root), "rev-parse", "HEAD"], text=True).strip()
        action_path = ROOT / "Github/release-build"
        action = yaml.safe_load((action_path / "action.yml").read_text())
        output = self.root / "adapter-output"
        environment = dict(os.environ, GITHUB_WORKSPACE=str(prepared.root), GITHUB_REPOSITORY=REPOSITORY,
            GITHUB_ACTION_PATH=str(action_path), GITHUB_OUTPUT=str(output), RELEASE_SHA=sha,
            RELEASE_VERSION=VERSION, RELEASE_MODULE_ROOT="", RELEASE_OUTPUT=str(self.root / "adapter-payload"))
        for step in action["runs"]["steps"]:
            if "run" in step:
                result = subprocess.run(["bash", "-e", "-o", "pipefail", "-c", step["run"]],
                    cwd=self.root, env=environment, text=True, capture_output=True)
                self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(sha, json.loads((self.root / "adapter-payload/release-manifest.json").read_text())["sha"])
        # Evaluate the composite's public outputs against an upload action result.
        # This executes its expressions; it does not assert source strings.
        upload = {"artifact-id": "31", "artifact-digest": "b" * 64}
        def expression(value):
            import re
            match = re.fullmatch(r"\$\{\{ steps\.upload\.outputs\.([a-z-]+) \}\}", value)
            if match:
                return upload[match[1]]
            match = re.fullmatch(r"\$\{\{ format\('([^']+)', steps\.upload\.outputs\.([a-z-]+)\) \}\}", value)
            if match:
                return match[1].format(upload[match[2]])
            raise AssertionError("unsupported composite output expression")
        values = {key: expression(metadata["value"]) for key, metadata in action["outputs"].items()}
        self.assertEqual({"artifact-id": "31", "artifact-digest": "sha256:" + "b" * 64}, values)
        environment["RELEASE_SHA"] = "f" * 40
        result = subprocess.run([sys.executable, "-I", str(ROOT / "Github/lib/release_build.py"), "--phase", "prepare"],
            cwd=self.root, env=environment, text=True, capture_output=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("does not match frozen", result.stderr)


if __name__ == "__main__":
    unittest.main()
