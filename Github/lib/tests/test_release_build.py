"""Real SDK release evaluation/pack/audit and executed composite controls."""
from __future__ import annotations

import contextlib
from contextlib import redirect_stderr, redirect_stdout
from datetime import datetime, timedelta, timezone
import hashlib
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
import zipfile

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_build as build
from ci_pipeline import PipelineError, prepare_config, evaluated_properties
sys.path.insert(0, str(Path(__file__).resolve().parent))
from actions_expressions import run_composite
import oci_archive as oci
import release_draft
import test_release_oci as oci_fixtures

ROOT = Path(__file__).resolve().parents[3]
SHA = "a" * 40
VERSION = "2.4.0"
REPOSITORY = "Hexalith/Hexalith.Builds"
FIRST = "2026-11-19T23:59:59Z"
CREATED_FLAG = "-p:ContainerProvenanceCreated="
IMAGE_CREATED = "org.opencontainers.image.created"
ARTIFACT_CREATED = "org.opencontainers.artifact.created"
INVALID_INSTANTS = (
    "2026-11-19T23:59:59.5Z", "2026-11-19T23:59:59.0000000Z", "2026-11-19T23:59:59+00:00",
    "2026-11-19T23:59:59+01:00", "2026-11-19T23:59:59", "2026-02-30T00:00:00Z", "2025-02-29T00:00:00Z",
    "2026-04-31T00:00:00Z", "2026-13-01T00:00:00Z", "2026-11-19T24:00:00Z", "2026-11-19T23:60:00Z",
    "2026-11-19T23:59:60Z", "0000-01-01T00:00:00Z", "2026-11-19 23:59:59Z", "2026-11-19t23:59:59z",
    "2026-11-19T23:59:59Z\n", "\uff12026-11-19T23:59:59Z", "", None,
    datetime(2026, 11, 19, 23, 59, 59, tzinfo=timezone.utc))
# A product that, like EventStore and Parties, requires the publisher instant
# with no default and also carries it as the artifact created label.
PROVENANCE_PROJECT = r"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <HexalithPublishContainer>true</HexalithPublishContainer>
    <ContainerRepository>hexalith-release-provenance</ContainerRepository>
    <ContainerBaseImage>mcr.microsoft.com/dotnet/runtime:10.0-alpine</ContainerBaseImage>
    <RuntimeIdentifiers>linux-x64;linux-arm64</RuntimeIdentifiers>
    <ContainerGenerateLabelsImageCreated>false</ContainerGenerateLabelsImageCreated>
  </PropertyGroup>
  <ItemGroup>
    <ContainerLabel Include="org.opencontainers.image.created" Value="$(ContainerProvenanceCreated)" />
    <ContainerLabel Include="org.opencontainers.artifact.created" Value="$(ContainerProvenanceCreated)" />
  </ItemGroup>
  <Target Name="ValidateFixtureProvenanceCreated" BeforeTargets="_PublishSingleContainer">
    <Error Condition="$([System.Text.RegularExpressions.Regex]::IsMatch('$(ContainerProvenanceCreated)', '\A(?!0000)[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\z')) != 'true'"
           Text="ContainerProvenanceCreated must be an exact UTC RFC 3339 second." />
  </Target>
</Project>
"""


def fixture(root, solution="PackageOnly.slnx"):
    shutil.copytree(ROOT / "test/fixtures/release", root)
    (root / ".github").mkdir()
    config = json.loads((ROOT / ".github/hexalith-ci.json").read_text())
    config.update(solution=solution, codeql=[],
                  tests={"integration": [], "exclude": {}, "minimumExpectedTests": {}, "failSkips": False},
                  hooks=[], jobs={}, submodules=[], dapr=False, browsers=False, registry=False)
    (root / ".github/hexalith-ci.json").write_text(json.dumps(config))
    return prepare_config(root, repository=REPOSITORY)


class AdvancingClock:
    """Publisher clock control that moves one second on every read, without sleeping."""

    def __init__(self, start=datetime(2026, 11, 19, 23, 59, 59, tzinfo=timezone.utc)):
        self.start, self.reads = start, []

    def __call__(self):
        self.reads.append((self.start + timedelta(seconds=len(self.reads))).strftime("%Y-%m-%dT%H:%M:%SZ"))
        return self.reads[-1]


def child_configs(path):
    """Each child image config of an assembled release archive."""
    files = oci.archive_files(path)
    root, = oci.document(files["index.json"])["manifests"]
    index = oci.document(files["blobs/sha256/" + root["digest"][7:]])
    for child in index["manifests"]:
        manifest = oci.document(files["blobs/sha256/" + child["digest"][7:]])
        yield child["platform"], oci.document(files["blobs/sha256/" + manifest["config"]["digest"][7:]])


def drafted_inventory(payload):
    """Zip the real payload as the upload does (stored) and read it with the draft parser."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_STORED) as archive:
        for path in sorted(payload.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(payload).as_posix())
    data = buffer.getvalue()
    return release_draft.artifact_inventory(data, sha=SHA, version=VERSION, repository=REPOSITORY,
                                            digest="sha256:" + hashlib.sha256(data).hexdigest())


class BuildTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="release-sdk-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.prepared = fixture(self.root / "source with spaces")
        self.flags = build.release_flags(VERSION, SHA, REPOSITORY)

    def recorded(self, calls):
        """Execute every SDK command and evaluation for real while recording its arguments."""
        command, evaluate = build.audited_command, build.evaluated_properties
        def run(arguments, *args):
            calls.append(list(arguments))
            return command(arguments, *args)
        def read(root, project, **kwargs):
            calls.append(["dotnet", "msbuild", project, *kwargs["flags"]])
            return evaluate(root, project, **kwargs)
        stack = contextlib.ExitStack()
        stack.enter_context(patch.object(build, "audited_command", side_effect=run))
        stack.enter_context(patch.object(build, "evaluated_properties", side_effect=read))
        return stack

    def assert_one_instant(self, calls, created):
        self.assertTrue(calls)
        for arguments in calls:
            self.assertEqual([CREATED_FLAG + created], [flag for flag in arguments if flag.startswith(CREATED_FLAG)])
            self.assertIn("-p:ContainerGenerateLabelsImageCreated=false", arguments)

    def test_publisher_instant_is_one_exact_real_utc_second(self):
        for value in (FIRST, "2024-02-29T00:00:00Z", "2000-02-29T12:30:45Z"):
            self.assertEqual(value, build.publisher_created(lambda value=value: value))
        reads = []
        def clock():
            reads.append(build.utc_second())
            return reads[-1]
        before = datetime.now(timezone.utc).replace(microsecond=0)
        created = build.publisher_created(clock)
        after = datetime.now(timezone.utc)
        self.assertEqual([created], reads)
        self.assertRegex(created, r"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\Z")
        self.assertTrue(before <= datetime.strptime(created, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc) <= after)
        for value in INVALID_INSTANTS:
            with self.subTest(value=value), self.assertRaisesRegex(PipelineError, "created instant"):
                build.publisher_created(lambda value=value: value)

    def test_invalid_publisher_instant_fails_before_any_sdk_command_or_payload(self):
        none = fixture(self.root / "invalid-none", "none")
        for index, value in enumerate(INVALID_INSTANTS):
            for prepared in (self.prepared, none):
                with self.subTest(value=value, solution=prepared.solution):
                    reads, output = [], self.root / f"invalid-{index}-{prepared.solution}"
                    def clock(value=value):
                        reads.append(value)
                        return value
                    with patch.object(build, "initialize_submodules", side_effect=AssertionError("submodule")), \
                            patch.object(build, "audited_command", side_effect=AssertionError("SDK")), \
                            patch.object(build, "evaluated_properties", side_effect=AssertionError("evaluation")), \
                            patch.object(build, "discover_projects", side_effect=AssertionError("discovery")), \
                            self.assertRaisesRegex(PipelineError, "created instant"):
                        build.build_release(prepared, output, version=VERSION, sha=SHA, repository=REPOSITORY,
                                            clock=clock)
                    self.assertEqual([value], reads)
                    self.assertFalse(output.exists())

    def test_real_multitarget_import_discovery_and_pack_stamp(self):
        projects = build.discover_projects(self.prepared, self.flags)
        self.assertEqual("Hexalith.Release.CreateMode.Disposable", projects[0].package)
        clock, calls = AdvancingClock(), []
        with self.recorded(calls):
            manifest = build.build_release(self.prepared, self.root / "payload", version=VERSION, sha=SHA,
                                           repository=REPOSITORY, clock=clock)
        # Package-only releases receive the instant but keep the legacy manifest and package shape.
        self.assertEqual([FIRST], clock.reads)
        self.assert_one_instant(calls, FIRST)
        self.assertEqual({"msbuild", "restore", "build", "pack"}, {arguments[1] for arguments in calls})
        self.assertEqual({"sha", "version", "packages", "containers"}, set(manifest))
        package, = manifest["packages"]
        self.assertEqual({"id", "path"}, set(package))
        build.validate_package(self.root / "payload" / package["path"], identity=package["id"], version=VERSION, sha=SHA)
        self.assertEqual(manifest, json.loads((self.root / "payload/release-manifest.json").read_text()))
        self.assertEqual([], manifest["containers"])
        self.assertEqual({"packages": [package["id"]], "containers": []}, drafted_inventory(self.root / "payload"))
        with self.assertRaises(PipelineError):
            build.validate_package(self.root / "payload" / package["path"], identity="other", version=VERSION, sha=SHA)

    def test_none_payload_does_not_execute_sdk_or_submodules(self):
        prepared = fixture(self.root / "none", "none")
        (prepared.root / "global.json").unlink()
        clock = AdvancingClock()
        with patch.object(build, "initialize_submodules", side_effect=AssertionError("submodule")), \
                patch.object(build, "audited_command", side_effect=AssertionError("SDK")), \
                patch.object(build, "discover_projects", side_effect=AssertionError("discovery")):
            manifest = build.build_release(prepared, self.root / "empty", version=VERSION, sha=SHA, repository=REPOSITORY,
                                           clock=clock)
        self.assertEqual([FIRST], clock.reads)
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
        path.write_text(path.read_text().replace("<IsTestProject>true</IsTestProject>",
            "<TargetFrameworks>net9.0;net10.0</TargetFrameworks><TargetFramework />"))
        with self.assertRaisesRegex(PipelineError, "container requires one evaluated target framework"):
            build.discover_projects(self.prepared, self.flags)
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
        self.assertEqual({"packages": [value["id"] for value in manifest["packages"]], "containers": [container["name"]]},
                         drafted_inventory(self.root / "container-payload"))

    def test_real_multi_project_multi_rid_images_share_one_publisher_instant(self):
        self.prepared = fixture(self.root / "created", "Release.slnx")
        provenance = self.prepared.root / "Provenance"
        shutil.copytree(self.prepared.root / "Container", provenance)
        (provenance / "Release.Container.csproj").unlink()
        (provenance / "Release.Provenance.csproj").write_text(PROVENANCE_PROJECT)
        (self.prepared.root / "Release.slnx").write_text('<Solution><Project Path="Package/Release.Package.csproj" />'
            '<Project Path="Container/Release.Container.csproj" /><Project Path="Provenance/Release.Provenance.csproj" />'
            '</Solution>')
        clock, calls = AdvancingClock(), []
        with self.recorded(calls):
            manifest = build.build_release(self.prepared, self.root / "created-payload", version=VERSION, sha=SHA,
                                           repository=REPOSITORY, clock=clock)
        # The clock advances on every read, so any second read would split the labels.
        self.assertEqual([FIRST], clock.reads)
        self.assert_one_instant(calls, FIRST)
        self.assertEqual(4, sum(arguments[1] == "publish" for arguments in calls))
        self.assertEqual({"sha", "version", "packages", "containers"}, set(manifest))
        self.assertEqual(["hexalith-release-create-disposable", "hexalith-release-provenance"],
                         [container["name"] for container in manifest["containers"]])
        for container in manifest["containers"]:
            path = self.root / "created-payload" / container["path"]
            graph = build.inspect_archive(path, revision=SHA, version=VERSION, repository=REPOSITORY,
                                          name=container["name"], created=FIRST)
            self.assertEqual(["linux/amd64", "linux/arm64"], graph["platforms"])
            with self.assertRaisesRegex(oci.OciError, "created label mismatch"):
                build.inspect_archive(path, revision=SHA, version=VERSION, created="2026-11-20T00:00:00Z")
            plain = container["name"] == "hexalith-release-create-disposable"
            for platform, config in child_configs(path):
                with self.subTest(container=container["name"], platform=platform):
                    labels = config["config"]["Labels"]
                    self.assertEqual(FIRST, labels[IMAGE_CREATED])
                    # A plain consumer gets the image label with no setup; a product that
                    # declares the artifact label receives the same publisher instant.
                    self.assertEqual(None if plain else FIRST, labels.get(ARTIFACT_CREATED))
        self.assertEqual({"packages": [value["id"] for value in manifest["packages"]],
                          "containers": [value["name"] for value in manifest["containers"]]},
                         drafted_inventory(self.root / "created-payload"))
        # The product requirement is real: without the publisher instant its publication fails.
        diagnostic = io.StringIO()
        with redirect_stdout(diagnostic), self.assertRaises(PipelineError):
            build.audited_command(["dotnet", "publish", str(provenance / "Release.Provenance.csproj"), "--no-restore",
                "-t:PublishContainer", "--runtime", "linux-x64", "-p:TargetFramework=net10.0",
                "-p:ContainerRuntimeIdentifiers=", "-p:ContainerRuntimeIdentifier=linux-x64",
                f"-p:ContainerArchiveOutputPath={self.root / 'without-instant.tar.gz'}", *self.flags],
                self.prepared.root, VERSION)
        self.assertIn("ContainerProvenanceCreated must be an exact UTC RFC 3339 second", diagnostic.getvalue())
        self.assertFalse((self.root / "without-instant.tar.gz").exists())

    def test_created_label_mismatch_fails_inspection_and_writes_no_manifest(self):
        prepared = fixture(self.root / "mismatch", "Release.slnx")
        project = build.Project("Container/Release.Container.csproj", "", "product/image", "net10.0",
                                ("linux-x64", "linux-arm64"))
        later = "2026-11-20T00:00:00Z"
        cases = {"honored": {}, "differs from instant": {"linux-x64": later, "linux-arm64": later},
                 "differs between platforms": {"linux-arm64": later},
                 "sdk per-platform clock": {"linux-x64": "2026-11-19T23:59:59.1Z", "linux-arm64": "2026-11-19T23:59:59.2Z"}}
        for index, (case, overrides) in enumerate(cases.items()):
            def sdk(arguments, _root, _version, overrides=overrides):
                # Fixture archives stand in for SDK output; each honours or overrides the instant.
                if arguments[1] != "publish":
                    return
                created = next(flag.removeprefix(CREATED_FLAG) for flag in arguments if flag.startswith(CREATED_FLAG))
                runtime = arguments[arguments.index("--runtime") + 1]
                destination = next(flag.split("=", 1)[1] for flag in arguments
                                   if flag.startswith("-p:ContainerArchiveOutputPath="))
                oci_fixtures.sdk_archive(Path(destination), build.platform_for_rid(runtime)["architecture"],
                                         labels={IMAGE_CREATED: overrides.get(runtime, created)})
            output = self.root / f"mismatch-{index}"
            with self.subTest(case=case), patch.object(oci_fixtures, "REPOSITORY", REPOSITORY), \
                    patch.object(build, "discover_projects", return_value=(project,)), \
                    patch.object(build, "audited_command", side_effect=sdk):
                if not overrides:
                    manifest = build.build_release(prepared, output, version=VERSION, sha=SHA, repository=REPOSITORY,
                                                   clock=lambda: FIRST)
                    container, = manifest["containers"]
                    self.assertEqual([FIRST, FIRST], [config["config"]["Labels"][IMAGE_CREATED]
                                                      for _platform, config in child_configs(output / container["path"])])
                    continue
                with self.assertRaisesRegex(oci.OciError, "created label mismatch"):
                    build.build_release(prepared, output, version=VERSION, sha=SHA, repository=REPOSITORY,
                                        clock=lambda: FIRST)
                self.assertFalse((output / "release-manifest.json").exists())
                self.assertEqual([], [path for path in output.rglob("*") if path.is_file()])

    def test_executed_build_adapter_frozen_identity_and_digest_handoff(self):
        prepared = fixture(self.root / "adapter", "none")
        for arguments in (["init", "--quiet", "-b", "main"], ["add", "."],
                          ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.com", "commit", "--quiet", "-m", "fixture"]):
            subprocess.run(["git", "-C", str(prepared.root), *arguments], check=True)
        sha = subprocess.check_output(["git", "-C", str(prepared.root), "rev-parse", "HEAD"], text=True).strip()
        action_path = ROOT / "Github/release-build"
        runner_temp = self.root / "adapter-temp"
        runner_temp.mkdir()
        output = self.root / "adapter-output"
        output.write_text("")
        environment = dict(os.environ, GITHUB_WORKSPACE=str(prepared.root), GITHUB_REPOSITORY=REPOSITORY,
            GITHUB_OUTPUT=str(output))
        for name in ("RELEASE_SHA", "RELEASE_VERSION", "RELEASE_MODULE_ROOT", "RELEASE_OUTPUT"):
            environment.pop(name, None)
        external = []
        def action(reference, values):
            external.append((reference.partition("@")[0], values))
            if reference.startswith("actions/upload-artifact@"):
                manifest = Path(values["path"]) / "release-manifest.json"
                self.assertEqual(sha, json.loads(manifest.read_text())["sha"])
                return {"artifact-id": "31", "artifact-digest": "b" * 64}
            return {}
        # The composite's own input/env/if/output mappings run; RELEASE_* is never preset.
        values, processes = run_composite(action_path, {"sha": sha, "version": VERSION}, environment,
                                          cwd=self.root, runner_temp=runner_temp, external=action)
        self.assertIsNotNone(values, processes[-1].stderr)
        payload = str(runner_temp / "hexalith-release-payload") + "/"
        # solution none skips the SDK action; checkout is credential-free at the frozen SHA.
        self.assertEqual([
            ("actions/checkout", {"ref": sha, "submodules": "false", "persist-credentials": "false"}),
            ("actions/upload-artifact", {"name": "release", "path": payload, "retention-days": "90",
                                         "if-no-files-found": "error", "compression-level": "0"})], external)
        self.assertEqual({"artifact-id": "31", "artifact-digest": "sha256:" + "b" * 64}, values)
        environment.update(GITHUB_ACTION_PATH=str(action_path), RELEASE_MODULE_ROOT="")
        environment["RELEASE_SHA"] = "f" * 40
        result = subprocess.run([sys.executable, "-I", str(ROOT / "Github/lib/release_build.py"), "--phase", "prepare"],
            cwd=self.root, env=environment, text=True, capture_output=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("does not match frozen", result.stderr)


if __name__ == "__main__":
    unittest.main()
