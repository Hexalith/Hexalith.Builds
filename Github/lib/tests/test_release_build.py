"""Real SDK release evaluation/pack/audit and executed composite controls."""
from __future__ import annotations

from contextlib import redirect_stderr
import io
import json
import os
from pathlib import Path
import re
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
        props = self.prepared.root / "Directory.Build.props"
        original = props.read_text()
        for index, separator in enumerate((";", ",", ";,")):
            with self.subTest(separator=separator):
                exemptions = separator.join(("NU1900", "NU1901", "NU1902", "NU1903", "NU1904", "NU1905", "CS1591"))
                value = re.sub(r"<WarningsNotAsErrors>.*?</WarningsNotAsErrors>",
                    f"<WarningsNotAsErrors>{exemptions}</WarningsNotAsErrors>", original)
                value = re.sub(r"<NoWarn>.*?</NoWarn>", f"<NoWarn>{exemptions}</NoWarn>", value)
                props.write_text(value)
                diagnostic = io.StringIO()
                output = self.root / f"vulnerable-{index}"
                with redirect_stderr(diagnostic), self.assertRaises(PipelineError):
                    build.build_release(self.prepared, output, version=VERSION, sha=SHA, repository=REPOSITORY)
                self.assertIn("v2.4.0 blocked by a vulnerable package", diagnostic.getvalue())
                self.assertFalse((output / "release-manifest.json").exists())

    def test_warning_filters_preserve_ordinary_and_low_moderate_policy_for_both_separators(self):
        props = self.prepared.root / "Directory.Build.props"
        original = props.read_text()
        for separator in (";", ",", ";,"):
            with self.subTest(separator=separator):
                warnings = separator.join(("NU1900", "NU1901", "NU1902", "NU1903", "NU1904", "NU1905", "CS1591"))
                value = re.sub(r"<WarningsNotAsErrors>.*?</WarningsNotAsErrors>",
                    f"<WarningsNotAsErrors>{warnings}</WarningsNotAsErrors>", original)
                value = re.sub(r"<NoWarn>.*?</NoWarn>", f"<NoWarn>{warnings}</NoWarn>", value)
                props.write_text(value)
                values = evaluated_properties(self.prepared.root, "Package/Release.Package.csproj",
                    properties=("WarningsNotAsErrors", "NoWarn", "TreatWarningsAsErrors"), flags=self.flags)
                self.assertEqual("true", values["TreatWarningsAsErrors"])
                for name in ("WarningsNotAsErrors", "NoWarn"):
                    codes = {part.strip() for part in re.split(r"[;,]", values[name]) if part.strip()}
                    self.assertEqual({"NU1901", "NU1902", "CS1591"}, codes)
                for code in ("NU1901", "NU1902"):
                    build.audited_command(["dotnet", "restore", str(self.prepared.root / "Package/Release.Package.csproj"),
                        f"-p:ReleaseFixtureAuditCode={code}", *self.flags], self.prepared.root, VERSION)

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

    def test_effective_sdk_rid_precedence_for_singular_and_plural_properties(self):
        self.prepared = fixture(self.root / "rid-precedence", "Release.slnx")
        path = self.prepared.root / "Container/Release.Container.csproj"
        original = re.sub(r"<RuntimeIdentifiers>.*?</RuntimeIdentifiers>", "", path.read_text())
        cases = (
            ({"ContainerRuntimeIdentifier": "linux-x64", "ContainerRuntimeIdentifiers": "linux-arm64"}, ("linux-x64",)),
            ({"RuntimeIdentifier": "linux-x64", "RuntimeIdentifiers": "linux-arm64"}, ("linux-x64",)),
            ({"RuntimeIdentifier": "linux-x64", "ContainerRuntimeIdentifiers": "linux-arm64"}, ("linux-x64",)),
            ({"ContainerRuntimeIdentifier": "linux-arm64", "RuntimeIdentifier": "linux-x64"}, ("linux-arm64",)),
            ({"ContainerRuntimeIdentifiers": "linux-arm64", "RuntimeIdentifiers": "linux-x64"}, ("linux-arm64",)),
            ({"RuntimeIdentifiers": "linux-x64;linux-arm64"}, ("linux-x64", "linux-arm64")),
        )
        for properties, expected in cases:
            with self.subTest(properties=properties):
                additions = "".join(f"<{name}>{value}</{name}>" for name, value in properties.items())
                path.write_text(original.replace("</PropertyGroup>", additions + "</PropertyGroup>"))
                project = next(project for project in build.discover_projects(self.prepared, self.flags) if project.container)
                self.assertEqual(expected, project.runtimes)

    def test_real_default_rid_and_sdk_normalized_repository_publish_without_extra_restore(self):
        self.prepared = fixture(self.root / "default-rid", "Release.slnx")
        path = self.prepared.root / "Container/Release.Container.csproj"
        path.write_text(re.sub(r"<(ContainerRepository|RuntimeIdentifiers)>.*?</\1>", "", path.read_text()))
        calls = []
        command = build.audited_command
        def record(arguments, *args):
            calls.append(arguments)
            return command(arguments, *args)
        with patch.object(build, "audited_command", side_effect=record):
            manifest = build.build_release(self.prepared, self.root / "default-payload", version=VERSION,
                sha=SHA, repository=REPOSITORY)
        container, = manifest["containers"]
        self.assertEqual("release-container", container["name"])
        graph = build.inspect_archive(self.root / "default-payload" / container["path"],
            revision=SHA, version=VERSION, repository=REPOSITORY, name=container["name"])
        self.assertEqual(["linux/amd64"], graph["platforms"])
        self.assertEqual(1, sum(arguments[1] == "restore" for arguments in calls))
        self.assertEqual(1, sum(arguments[1] == "build" for arguments in calls))
        self.assertEqual(1, sum(arguments[1] == "publish" for arguments in calls))

    def test_container_specific_rid_assets_and_colliding_repository_names_publish(self):
        self.prepared = fixture(self.root / "collision", "Release.slnx")
        path = self.prepared.root / "Container/Release.Container.csproj"
        original = re.sub(r"<RuntimeIdentifiers>.*?</RuntimeIdentifiers>",
            "<ContainerRuntimeIdentifier>linux-x64</ContainerRuntimeIdentifier>", path.read_text())
        path.write_text(original.replace("hexalith-release-create-disposable", "a/b_c"))
        other = self.prepared.root / "Other"
        shutil.copytree(path.parent, other)
        other_project = other / "Other.Container.csproj"
        (other / path.name).rename(other_project)
        other_project.write_text(original.replace("hexalith-release-create-disposable", "a_b/c"))
        (self.prepared.root / "Release.slnx").write_text('<Solution><Project Path="Container/Release.Container.csproj" />'
            '<Project Path="Other/Other.Container.csproj" /></Solution>')
        manifest = build.build_release(self.prepared, self.root / "collision-payload", version=VERSION,
            sha=SHA, repository=REPOSITORY)
        self.assertEqual(["a/b_c", "a_b/c"], [value["name"] for value in manifest["containers"]])
        self.assertEqual(2, len({value["path"] for value in manifest["containers"]}))
        for container in manifest["containers"]:
            graph = build.inspect_archive(self.root / "collision-payload" / container["path"],
                revision=SHA, version=VERSION, repository=REPOSITORY, name=container["name"])
            self.assertEqual(["linux/amd64"], graph["platforms"])

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
