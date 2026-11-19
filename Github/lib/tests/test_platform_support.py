"""Source qualification for historical preparation, finite cases and strict archives.

Synthetic producer bytes/process controls prove protocol boundaries, not product
or signature qualification. Real SDK and unsigned-verifier probes are separate.
"""
from __future__ import annotations
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

from jsonschema import Draft202012Validator
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build_projects as cases
import ci_config
import ci_pipeline as pipeline
import module_preparation as preparation
import package_archive
import published_builds as published
import release_build
import release_draft
import release_publish
from release_state import ReleaseError
from test_release_draft import artifact, digest, package, zip_bytes

ROOT = Path(__file__).resolve().parents[3]
VERSIONS = ("3.130.0", "3.130.1", "3.130.2", "3.132.1", "4.1.0", "4.2.0")
SHA = "a" * 40


def sha(data):
    return hashlib.sha256(data).hexdigest()


def git(root, *arguments):
    return subprocess.check_output(["git", "-C", str(root), *arguments], stderr=subprocess.DEVNULL).decode().rstrip("\r\n")


def initialize(root):
    root.mkdir(parents=True, exist_ok=True)
    git(root, "init", "--quiet", "--initial-branch=main")
    git(root, "config", "user.name", "Fixture")
    git(root, "config", "user.email", "fixture@example.invalid")


def commit(root):
    git(root, "add", ".")
    git(root, "commit", "--quiet", "-m", "test: source fixture")
    return git(root, "rev-parse", "HEAD")


def config():
    value = copy.deepcopy(json.loads((ROOT / "Github/lib/tests/fixtures/contract.json").read_text())[0]["document"])
    value.update(buildsTag="v6.0.6", publishedBuilds="eng/tuple.json", jobs={})
    return value


def binding(source="1" * 40, blob="2" * 40, catalog="3" * 64, archive="4" * 64, tooling="5" * 64):
    version = "6.0.5"
    manifest = json.dumps({"sha": source, "version": version, "packages": [
        {"id": identity, "path": f"packages/{identity}.{version}.nupkg"}
        for identity in ("Hexalith.Builds.Evidence.Cli", "Hexalith.Builds.Module.Cli")], "containers": []})
    body = "\n".join(("hexalith-release: " + source, "hexalith-release-run: 11",
        "hexalith-release-artifact: 12 sha256:" + "6" * 64,
        "hexalith-release-packages: Hexalith.Builds.Evidence.Cli,Hexalith.Builds.Module.Cli", "hexalith-release-containers: ")) + "\n"
    value = {"schemaVersion": 1, "candidateAcceptance": False, "producer": {
        "repository": "https://github.com/Hexalith/Hexalith.Builds.git", "sourceRevision": source,
        "catalog": {"path": "Props/Directory.Packages.props", "blob": blob, "sha256": catalog}},
        "publication": {"kind": "shared-release-handoff/v1", "version": version, "sourceRevision": source,
        "releaseTag": "v" + version, "catalogSha256": catalog, "archiveSha256": archive, "toolingSha256": tooling,
        "releaseId": 10, "runId": 11, "artifactId": 12, "artifactSha256": "6" * 64,
        "releaseBodySha256": sha(body.encode()), "manifestJson": manifest, "manifestSha256": sha(manifest.encode())}}
    metadata = {"releases/10": {"id": 10, "tag_name": "v6.0.5", "draft": False, "prerelease": False,
        "published_at": "2026-11-16T00:00:00Z", "body": body},
        "git/ref/tags/v6.0.5": {"object": {"type": "commit", "sha": source}},
        "actions/runs/11": {"id": 11, "head_sha": source, "status": "completed", "conclusion": "success",
            "path": ".github/workflows/builds-release.yml"}}
    return value, metadata


class ContractTests(unittest.TestCase):
    def test_defaults_closed_fields_and_semantic_refusals(self):
        schema = json.loads((ROOT / "schemas/hexalith.ci.v1.json").read_text())
        validator = Draft202012Validator(schema)
        base = config()
        case = {"id": "sdk-420", "project": "compat/Consumer.csproj", "versions": {"HexalithEventStoreVersion": "4.2.0"}}
        for extra in ({}, {"publishedBuilds": None}, {"buildProjects": []}, {"buildProjects": [case]}):
            value = dict(base, **extra)
            validator.validate(value)
            self.assertIs(value, ci_config.validate_config(value, repository="Example/Fixture"))
        self.assertIsNone(schema["properties"]["publishedBuilds"]["default"])
        self.assertEqual([], schema["properties"]["buildProjects"]["default"])
        invalid = [{"moduleManifest": "module.json"}, {"publishedBuilds": "../tuple.json"},
            {"publishedBuilds": {"url": "https://foreign.invalid"}}, {"solution": "none"},
            {"buildProjects": [dict(case, hiddenCommand="build")]},
            {"buildProjects": [dict(case, versions={"NuGetAudit": "false"})]},
            {"buildProjects": [dict(case, versions={"HexalithEventStoreVersion": "4.2.*"})]},
            {"buildProjects": [case] * 33}, {"buildProjects": [case], "publishedBuilds": None},
            {"jobs": {"windows": {"runner": "windows-latest"}}},
            *({"jobs": {"platform": {"runner": runner, "trigger": "manual"}}} for runner in ("windows-latest", "macos-latest"))]
        for extra in invalid:
            value = dict(base, **extra)
            with self.subTest(extra=extra):
                self.assertFalse(validator.is_valid(value))
                with self.assertRaises(ValueError):
                    ci_config.validate_config(value, repository="Example/Fixture")
        for project in ("references/Foreign/C.csproj", "compat/x;C.csproj", "compat/link.csproj"):
            if project.endswith("link.csproj"):
                continue
            with self.assertRaises(ValueError):
                ci_config.validate_config(dict(base, buildProjects=[dict(case, project=project)]), repository="Example/Fixture")
        for extra_case in (dict(case, project="compat/Other.csproj"), dict(case, id="other")):
            with self.assertRaisesRegex(ValueError, "duplicate"):
                ci_config.validate_config(dict(base, buildProjects=[case, extra_case]), repository="Example/Fixture")


class HistoricalTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="historical-source-")
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "module"
        initialize(self.root)
        self.builds = self.root / "references/Hexalith.Builds"
        initialize(self.builds)
        (self.builds / "Props").mkdir()
        self.catalog = self.builds / "Props/Directory.Packages.props"
        self.catalog.write_text("<Project><!-- historical --></Project>\n")
        self.old = commit(self.builds)
        git(self.builds, "tag", "v6.0.5")
        blob = git(self.builds, "rev-parse", "HEAD:Props/Directory.Packages.props")
        catalog_sha = sha(self.catalog.read_bytes())
        self.catalog.write_text("<Project><!-- active independently accepted --></Project>\n")
        self.active = commit(self.builds)
        git(self.builds, "tag", "v6.0.6")
        tooling = b"synthetic Tooling control"
        self.payload = zip_bytes([("tools/net11.0/any/Hexalith.Builds.Tooling.dll", tooling),
            ("tools/net11.0/any/Hexalith.Builds.Module.Cli.dll", b"synthetic executable control"),
            ("tools/net11.0/any/g4-host/bin/EventStoreHost/Hexalith.Builds.Module.EventStoreHost.dll", b"host"),
            (".signature.p7s", b"synthetic signature control")])
        self.value, self.metadata = binding(self.old, blob, catalog_sha, sha(self.payload), sha(tooling))
        (self.root / "eng").mkdir()
        (self.root / ".config").mkdir()
        (self.root / ".github").mkdir()
        (self.root / "eng/tuple.json").write_text(json.dumps(self.value))
        (self.root / ".config/dotnet-tools.json").write_text(json.dumps({"isRoot": True, "tools": {
            "hexalith.builds.module.cli": {"version": "6.0.5", "rollForward": False, "commands": ["hexalith-module"]}}}))
        (self.root / ".gitmodules").write_text('[submodule "references/Hexalith.Builds"]\npath = references/Hexalith.Builds\nurl = https://github.com/Hexalith/Hexalith.Builds.git\n')
        self.prepared = SimpleNamespace(root=self.root, document=config(), workspace=self.root, solution="Fixture.slnx", global_json="global.json", submodules=())
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.prepared.document))
        self.head = commit(self.root)
        comparison = SimpleNamespace(require_clean=lambda: None, actual=SimpleNamespace(read=lambda _: (self.root / ".github/hexalith-ci.json").read_bytes()),
            delivered_sha="7" * 40, document={"accepted": "control"})
        self.comparison = comparison
        self.authority = preparation.accepted_authority(self.prepared, repository="Example/Fixture", comparison=comparison)
        self.clean_environment = patch.dict(os.environ, {key: value for key, value in os.environ.items()
            if key not in (*preparation.VALIDATOR.KEYS, "HexalithBuildsPublishedToolRoot", cases.VARIABLE, preparation.STATE_VARIABLE)}, clear=True)
        self.clean_environment.start()
        self.addCleanup(self.clean_environment.stop)

    def runner(self, arguments, **kwargs):
        self.assertFalse(any("TOKEN" in key or "KEY" in key or key in ("PYTHONPATH", "NUGET_PACKAGES") for key in kwargs["env"]))
        if arguments[1:3] == ["nuget", "verify"]:
            return SimpleNamespace(returncode=0, stdout=b"synthetic signature verifier control")
        return SimpleNamespace(returncode=0, stdout=("6.0.5+" + self.old).encode())

    def prepare(self, payload=None):
        real_prepare = published.prepare
        with patch.object(published, "prepare", side_effect=lambda prepared, workspace: real_prepare(prepared, workspace,
                read=lambda path: self.metadata[path], download=lambda url: self.payload if payload is None else payload, run=self.runner)):
            return preparation.prepare_environment(self.prepared, self.authority, self.base)

    def test_independent_catalogs_historical_bytes_and_one_lifetime(self):
        inputs = preparation._inputs(self.prepared, self.authority)
        self.assertNotEqual(inputs["activeCatalogSha256"], inputs["historicalCatalogSha256"])
        self.assertEqual(self.old, inputs["historicalSource"])
        environment = self.prepare()
        self.assertEqual(preparation.PUBLISHED_KEYS, environment.values.keys())
        with preparation.applied(self.prepared, self.authority, environment):
            self.assertTrue((Path(os.environ["HexalithBuildsPublishedToolRoot"]) / "Hexalith.Builds.Tooling.dll").is_file())
            preparation.verify_environment(self.prepared, self.authority, environment)
        self.assertNotIn("HexalithBuildsPublishedToolRoot", os.environ)
        Path(environment.values["HexalithBuildsPublishedToolRoot"], "Hexalith.Builds.Tooling.dll").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "changed"):
            preparation.verify_environment(self.prepared, self.authority, environment)

    def test_wrong_changed_unsigned_unavailable_producers_export_nothing(self):
        for kind in ("digest", "metadata", "signature", "tooling", "host", "version"):
            with self.subTest(kind=kind):
                with patch.object(published, "verify_handoff", side_effect=ValueError("unavailable authority") if kind == "metadata" else None), \
                     patch.object(published, "verify_signature", side_effect=ValueError("unsigned") if kind == "signature" else None), \
                     patch.object(published, "members", wraps=package_archive.members) as parse:
                    if kind == "digest":
                        payload = self.payload + b"changed"
                    else:
                        payload = self.payload
                    if kind in ("tooling", "host"):
                        files = package_archive.members(self.payload)
                        if kind == "tooling":
                            files["tools/net11.0/any/Hexalith.Builds.Tooling.dll"] = b"changed"
                        else:
                            del files["tools/net11.0/any/g4-host/bin/EventStoreHost/Hexalith.Builds.Module.EventStoreHost.dll"]
                        parse.return_value = files
                        parse.side_effect = lambda *_args, **_kwargs: files
                    with patch.object(self, "runner", return_value=SimpleNamespace(returncode=0, stdout=b"wrong")) if kind == "version" else patch.dict(os.environ, {}):
                        with self.assertRaises(ValueError):
                            self.prepare(payload)
                self.assertFalse(list(self.base.glob("hexalith-module-preparation-*")))
                self.assertNotIn("HexalithBuildsPublishedToolRoot", os.environ)

    def test_source_binding_tool_and_active_identity_revalidated(self):
        environment = self.prepare()
        for path in (self.root / "eng/tuple.json", self.root / ".config/dotnet-tools.json", self.catalog):
            environment = self.prepare()
            original = path.read_bytes()
            path.write_bytes(original + b" ")
            with self.subTest(path=path), self.assertRaises(ValueError):
                preparation.verify_environment(self.prepared, self.authority, environment)
            path.write_bytes(original)
        with self.assertRaisesRegex(ValueError, "accepted-delivery"):
            preparation.prepare_environment(self.prepared, dict(self.authority, kind="candidate-qualification"), self.base)

    def test_job_reuse_prepares_once_and_mapping_conflicts_fail(self):
        real_prepare = published.prepare
        calls = []
        def controlled(prepared, workspace):
            calls.append(workspace)
            return real_prepare(prepared, workspace, read=lambda p: self.metadata[p], download=lambda _: self.payload, run=self.runner)
        with patch.object(preparation, "accepted_authority", return_value=self.authority), patch.object(published, "prepare", side_effect=controlled), \
             patch.dict(os.environ, {"RUNNER_TEMP": str(self.base), "GITHUB_ENV": str(self.base / "environment")}):
            first = preparation.ensure_for_job(self.prepared, repository="Example/Fixture")
            second = preparation.ensure_for_job(self.prepared, repository="Example/Fixture")
            self.assertEqual(first, second)
            self.assertEqual(1, len(calls))
            with patch.dict(os.environ, {"HexalithBuildsPublishedToolRoot": "/changed"}):
                with self.assertRaisesRegex(ValueError, "conflict"):
                    preparation.ensure_for_job(self.prepared, repository="Example/Fixture")

    def test_ci_local_and_release_prepare_before_first_evaluation(self):
        import local_check
        real_prepare = published.prepare
        calls = []
        def controlled(prepared, workspace):
            calls.append("prepare")
            return real_prepare(prepared, workspace, read=lambda p: self.metadata[p], download=lambda _: self.payload, run=self.runner)
        def evaluate(*_args, **_kwargs):
            calls.append("evaluate")
            tool = Path(os.environ["HexalithBuildsPublishedToolRoot"])
            self.assertTrue((tool / "Hexalith.Builds.Tooling.dll").is_file())
            return ()
        environment = {key: value for key, value in os.environ.items() if key not in (*preparation.PUBLISHED_KEYS, preparation.STATE_VARIABLE)}
        with patch.object(published, "prepare", side_effect=controlled), patch.object(preparation, "accepted_authority", return_value=self.authority):
            with patch.dict(os.environ, dict(environment, RUNNER_TEMP=str(self.base), GITHUB_ENV=str(self.base / "ci-environment")), clear=True), \
                 patch.object(pipeline, "prepare_config", return_value=self.prepared), patch.object(pipeline, "initialize_submodules"), \
                 patch.object(pipeline, "write_outputs"), patch.object(pipeline, "restore_and_build", side_effect=evaluate), \
                 patch.object(pipeline, "run_unit_tests", return_value=pipeline.TestProjects((), (), ())), patch.object(pipeline, "write_stage_outputs"), \
                 patch.object(sys, "argv", ["ci", "--repository", "Example/Fixture", "--phase", "all"]):
                self.assertEqual(0, pipeline.main())
            self.assertEqual(["prepare", "evaluate"], calls)
            calls.clear()
            fake_git = SimpleNamespace(root=self.root, run=lambda *args, **kwargs: (str(self.root).encode() + b"\n") if args[-1] == "--show-toplevel" else b"false")
            fake_builds = SimpleNamespace(root=self.builds)
            with patch.dict(os.environ, environment, clear=True), patch.object(local_check, "git_at", return_value=fake_git), \
                 patch.object(local_check, "commit", side_effect=lambda git, ref, label: self.active if ref.startswith("refs/tags/") else self.head), \
                 patch.object(local_check.state, "tree_snapshot", return_value=SimpleNamespace(items={local_check.BUILDS_PATH: SimpleNamespace(oid=self.active)})), \
                 patch.object(local_check, "repository_identity", return_value="Example/Fixture"), patch.object(local_check, "checkout", return_value=fake_builds), \
                 patch.object(local_check, "require_clean"), patch.object(local_check, "require_declarations"), patch.object(local_check, "revalidate"), \
                 patch.object(local_check.guard, "run", return_value=self.comparison), patch.object(pipeline, "prepare_config", return_value=self.prepared), \
                 patch.object(pipeline, "restore_and_build", side_effect=evaluate), patch.object(pipeline, "run_unit_tests"):
                local_check.run(self.root)
            self.assertEqual(["prepare", "evaluate"], calls)
            calls.clear()
            with patch.dict(os.environ, environment, clear=True), patch.object(release_build, "initialize_submodules"), \
                 patch.object(release_build, "discover_projects", side_effect=evaluate), patch.object(release_build, "audited_command"):
                manifest = release_build.build_release(self.prepared, self.base / "release-payload", version="1.2.3", sha=self.head, repository="Example/Fixture")
            self.assertEqual(["prepare", "evaluate"], calls)
            self.assertEqual([], manifest["packages"])

    def test_published_apply_and_reuse_reject_overrides_and_clean_only_owned_state(self):
        self.prepared.document["buildProjects"] = [{"id": "case", "project": "compat.csproj", "versions": {"HexalithEventStoreVersion": "4.2.0"}}]
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.prepared.document))
        self.head = commit(self.root)
        self.authority = preparation.accepted_authority(self.prepared, repository="Example/Fixture", comparison=self.comparison)
        for mode in ("apply", "reuse", "mapping"):
            environment = self.prepare()
            channel = self.base / (mode + "-env")
            original = dict(os.environ)
            with patch.dict(os.environ, {"RUNNER_TEMP": str(self.base), "GITHUB_ENV": str(channel)}, clear=False):
                if mode == "apply":
                    for key in preparation.VALIDATOR.KEYS - {"Configuration"}:
                        # Each refusal destroys its own lifetime, so prepare the
                        # next control after restoring the foreign environment.
                        environment = self.prepare()
                        with patch.dict(os.environ, {key: "/foreign/import", "UNRELATED": "preserve"}):
                            with self.assertRaisesRegex(ValueError, "ambient"):
                                with preparation.applied(self.prepared, self.authority, environment):
                                    self.fail("overridden producer applied")
                            self.assertEqual("/foreign/import", os.environ[key])
                            self.assertEqual("preserve", os.environ["UNRELATED"])
                            self.assertFalse(environment.workspace.parent.exists())
                    continue
                real_prepare = preparation.prepare_environment
                with patch.object(preparation, "accepted_authority", return_value=self.authority), patch.object(preparation, "prepare_environment", return_value=environment):
                    preparation.ensure_for_job(self.prepared, repository="Example/Fixture")
                owned = environment.workspace.parent
                if mode == "reuse":
                    os.environ[next(iter(preparation.VALIDATOR.IMPORTS))] = "/foreign/import"
                else:
                    # Reuse must refuse a changed mapping and discard the
                    # recorded producer even if the receipt is otherwise valid.
                    changed = json.loads(os.environ[cases.VARIABLE])
                    changed["case"]["versions"]["HexalithEventStoreVersion"] = "9.9.9"
                    os.environ[cases.VARIABLE] = json.dumps(changed)
                with self.assertRaises(ValueError):
                    preparation.ensure_for_job(self.prepared, repository="Example/Fixture")
                self.assertFalse(owned.exists())
                self.assertNotIn(preparation.STATE_VARIABLE, os.environ)
                self.assertNotIn("HexalithBuildsPublishedToolRoot", os.environ)
                self.assertNotIn(cases.VARIABLE, os.environ)
                os.environ.clear()
                os.environ.update(original)
        # A foreign receipt/producer path is never owned, even with a familiar
        # file name. Its files and environment remain intact on refusal.
        foreign = self.base / "foreign"
        foreign.mkdir()
        receipt = foreign / "state.json"
        receipt.write_text("{}")
        with patch.dict(os.environ, {preparation.STATE_VARIABLE: str(receipt), "HexalithBuildsPublishedToolRoot": str(foreign), "RUNNER_TEMP": str(self.base)}):
            with self.assertRaises(ValueError):
                preparation.ensure_for_job(self.prepared, repository="Example/Fixture")
            self.assertTrue(receipt.exists())
            self.assertEqual(str(foreign), os.environ["HexalithBuildsPublishedToolRoot"])

    def test_nonempty_cohorts_run_through_ci_local_and_release_and_stop_on_failure(self):
        import local_check
        (self.root / "compat").mkdir()
        (self.root / "compat/Consumer.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Hexalith.EventStore.Contracts" Version="$(HexalithEventStoreVersion)" /></ItemGroup></Project>')
        (self.root / "Ship.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"/>')
        (self.root / "Fixture.slnx").write_text('<Solution><Project Path="compat/Consumer.csproj" /></Solution>')
        self.prepared.document["buildProjects"] = [{"id": "sdk-" + version.replace(".", "-"), "project": "compat/Consumer.csproj", "versions": {"HexalithEventStoreVersion": version}} for version in ("4.1.0", "4.2.0")]
        (self.root / ".github/hexalith-ci.json").write_text(json.dumps(self.prepared.document))
        self.head = commit(self.root)
        self.authority = preparation.accepted_authority(self.prepared, repository="Example/Fixture", comparison=self.comparison)
        real_prepare = published.prepare
        def controlled(prepared, workspace):
            return real_prepare(prepared, workspace, read=lambda p: self.metadata[p], download=lambda _: self.payload, run=self.runner)
        created = "2026-11-19T23:59:59Z"
        def assert_instant(flags):
            # The release passes one publisher instant to every declared-build evaluation and command.
            if route == "release":
                self.assertEqual(["-p:ContainerProvenanceCreated=" + created],
                                 [flag for flag in flags if flag.startswith("-p:ContainerProvenanceCreated=")])
                self.assertIn("-p:ContainerGenerateLabelsImageCreated=false", flags)
        def evaluate(*args, **kwargs):
            assert_instant(kwargs["flags"])
            values = {name: "" for name in kwargs["properties"]}
            values.update(dict(flag[3:].split("=", 1) for flag in kwargs["flags"] if flag.startswith("-p:")))
            values["TargetFramework"] = "net10.0"
            return values
        clean = dict(os.environ)
        for route in ("ci", "local", "release"):
            for fail in (False, True):
                events = []
                def outputs():
                    mapping = json.loads(os.environ[cases.VARIABLE])
                    for value in mapping.values():
                        path = Path(value["artifactsPath"]) / "selection.txt"
                        self.assertEqual(value["versions"]["HexalithEventStoreVersion"], path.read_text())
                def command(arguments, root, *_rest, **_kwargs):
                    action = arguments[1]
                    artifact_flags = [flag for flag in arguments if flag.startswith("-p:ArtifactsPath=")]
                    if artifact_flags:
                        assert_instant(arguments)
                        directory = Path(artifact_flags[0].split("=", 1)[1])
                        version = next(flag.split("=", 1)[1] for flag in arguments if flag.startswith("-p:HexalithEventStoreVersion="))
                        events.append(action + ":" + version)
                        if action == "restore":
                            (directory / "obj").mkdir(parents=True, exist_ok=True)
                            (directory / "obj/project.assets.json").write_text(json.dumps({"libraries": {"Hexalith.EventStore.Contracts/" + version: {"type": "package"}}}))
                        elif fail:
                            raise pipeline.PipelineError("controlled cohort failure", 37)
                        else:
                            (directory / "selection.txt").write_text(version)
                    elif action == "pack":
                        outputs()
                        events.append("pack")
                        destination = Path(arguments[arguments.index("--output") + 1])
                        destination.mkdir(parents=True)
                        (destination / "Fixture.Package.1.2.3.nupkg").write_bytes(package(self.head, version="1.2.3"))
                    return ""
                def unit(*args, **kwargs):
                    outputs()
                    events.append("unit")
                    return pipeline.TestProjects((), (), ())
                with self.subTest(route=route, fail=fail), patch.dict(os.environ, dict(clean, RUNNER_TEMP=str(self.base), GITHUB_ENV=str(self.base / "cohort-env")), clear=True), \
                     patch.object(published, "prepare", side_effect=controlled), patch.object(preparation, "accepted_authority", return_value=self.authority), \
                     patch.object(pipeline, "evaluated_properties", side_effect=evaluate), patch.object(pipeline, "run_command", side_effect=command), \
                     patch.object(pipeline, "run_unit_tests", side_effect=unit):
                    if route == "ci":
                        with patch.object(pipeline, "prepare_config", return_value=self.prepared), patch.object(pipeline, "write_outputs"), patch.object(pipeline, "initialize_submodules"), patch.object(pipeline, "write_stage_outputs"), patch.object(sys, "argv", ["ci", "--repository", "Example/Fixture", "--phase", "all"]):
                            self.assertEqual(37 if fail else 0, pipeline.main())
                    elif route == "local":
                        fake = SimpleNamespace(root=self.root, run=lambda *args, **kwargs: (str(self.root).encode() + b"\n") if args[-1] == "--show-toplevel" else b"false")
                        with patch.object(local_check, "git_at", return_value=fake), patch.object(local_check, "commit", side_effect=lambda git, ref, label: self.active if ref.startswith("refs/tags/") else self.head), patch.object(local_check.state, "tree_snapshot", return_value=SimpleNamespace(items={local_check.BUILDS_PATH: SimpleNamespace(oid=self.active)})), patch.object(local_check, "repository_identity", return_value="Example/Fixture"), patch.object(local_check, "checkout", return_value=SimpleNamespace(root=self.builds)), patch.object(local_check, "require_clean"), patch.object(local_check, "require_declarations"), patch.object(local_check, "revalidate"), patch.object(local_check.guard, "run", return_value=self.comparison), patch.object(pipeline, "prepare_config", return_value=self.prepared):
                            if fail:
                                with self.assertRaises(pipeline.PipelineError):
                                    local_check.run(self.root)
                            else:
                                local_check.run(self.root)
                    else:
                        destination = self.base / ("payload-" + str(fail))
                        with patch.object(release_build, "initialize_submodules"), patch.object(release_build, "discover_projects", return_value=(release_build.Project("Ship.csproj", "Fixture.Package", "", "", ()),)), patch.object(release_build, "audited_command", side_effect=command):
                            if fail:
                                with self.assertRaises(pipeline.PipelineError):
                                    release_build.build_release(self.prepared, destination, version="1.2.3", sha=self.head, repository="Example/Fixture", clock=lambda: created)
                                self.assertFalse((destination / "release-manifest.json").exists())
                            else:
                                manifest = release_build.build_release(self.prepared, destination, version="1.2.3", sha=self.head, repository="Example/Fixture", clock=lambda: created)
                                self.assertEqual(1, len(manifest["packages"]))
                    self.assertIn("build:4.1.0", events)
                    if fail:
                        self.assertNotIn("restore:4.2.0", events)
                        self.assertFalse({"unit", "pack"}.intersection(events))
                    else:
                        self.assertIn("build:4.2.0", events)
                        self.assertIn("pack" if route == "release" else "unit", events)

    def test_official_markers_run_tag_manifest_and_closed_binding_refuse_changes(self):
        for key in self.metadata:
            values = copy.deepcopy(self.metadata)
            if key.startswith("releases"):
                values[key]["body"] += "changed"
            elif key.startswith("git"):
                values[key]["object"]["sha"] = "0" * 40
            else:
                values[key]["conclusion"] = "failure"
            with self.subTest(key=key), self.assertRaises(ValueError):
                published.verify_handoff(self.value, read=lambda p: values[p])
        for mutate in (lambda v: v.update(candidateAcceptance=True), lambda v: v.update(extra="forbidden"),
                       lambda v: v["publication"].update(manifestJson="{}"),
                       lambda v: v["producer"].update(repository="https://foreign.invalid")):
            value = copy.deepcopy(self.value)
            mutate(value)
            with self.assertRaises(ValueError):
                published.validate_binding(value)


class DeclaredProjectTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="declared-sdk-")
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "source"
        initialize(self.root)
        (self.root / "global.json").write_text('{"sdk":{"version":"10.0.404","rollForward":"disable"}}')
        (self.root / "compat").mkdir()
        self.project = self.root / "compat/Consumer.csproj"
        self.project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
<IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include="Hexalith.EventStore.Contracts" Version="$(HexalithEventStoreVersion)" /></ItemGroup><Target Name="ObserveSelection" AfterTargets="Build">
<WriteLinesToFile File="$(ArtifactsPath)/selection.txt" Lines="$(HexalithEventStoreVersion)" Overwrite="true" />
<Error Condition="'$(UseHexalithProjectReferences)' != 'false'" Text="Package mode required" />
</Target></Project>''')
        (self.root / "compat/Consumer.cs").write_text("public class Consumer {}")
        commit(self.root)
        # Synthetic package controls never share the user's genuine cache.
        private_packages = patch.dict(os.environ, {"NUGET_PACKAGES": str(self.base / "packages")})
        private_packages.start()
        self.addCleanup(private_packages.stop)
        self.feed = self.base / "feed"
        self.feed.mkdir()
        for version in VERSIONS:
            data = zip_bytes([("Hexalith.EventStore.Contracts.nuspec", '<package><metadata><id>Hexalith.EventStore.Contracts</id><version>' + version + '</version><authors>Fixture</authors><description>Local source qualification</description></metadata></package>')])
            (self.feed / ("Hexalith.EventStore.Contracts." + version + ".nupkg")).write_bytes(data)
        self.nuget = self.base / "NuGet.Config"
        self.nuget.write_text('<configuration><packageSources><clear/><add key="fixture" value="' + str(self.feed) + '"/></packageSources></configuration>')
        self.prepared = SimpleNamespace(root=self.root, document={"moduleManifest": "module.json", "buildProjects": [
            {"id": "sdk-" + v.replace(".", "-"), "project": "compat/Consumer.csproj", "versions": {"HexalithEventStoreVersion": v}} for v in VERSIONS]})
        workspace = self.base / "preparation/producer"
        workspace.mkdir(parents=True)
        self.environment = preparation.BuildEnvironment(workspace, {}, {}, {}, {})
        self.addCleanup(cases._MAPPINGS.clear)
        self.addCleanup(cases._ENVIRONMENTS.clear)
        # This suite qualifies real SDK isolation; authenticated producer state
        # is independently exercised by HistoricalTests above.
        validation = patch.object(preparation, "verify_environment")
        validation.start()
        self.addCleanup(validation.stop)

    def test_real_sdk_six_exact_selections_and_disjoint_outputs(self):
        with cases.output_scope(self.prepared, self.environment):
            mapping = json.loads(os.environ[cases.VARIABLE])
            cases.build_declared(self.prepared, pipeline.build_flags(self.nuget), pipeline.run_command)
            self.assertEqual(6, len({value["artifactsPath"] for value in mapping.values()}))
            for entry in mapping.values():
                directory = Path(entry["artifactsPath"])
                self.assertEqual(entry["versions"]["HexalithEventStoreVersion"], (directory / "selection.txt").read_text().strip())
                self.assertEqual(1, len(list((directory / "bin").rglob("Consumer.dll"))))
                self.assertTrue(list((directory / "obj").rglob("project.assets.json")))
            self.assertFalse((self.root / "compat/bin").exists())
            self.assertFalse((self.root / "compat/obj").exists())
        self.assertNotIn(cases.VARIABLE, os.environ)
        self.assertFalse((self.environment.workspace.parent / "build-projects").exists())

    def test_failure_stops_successors_and_retains_release_policy(self):
        events = []
        def fail(arguments, root):
            events.append(arguments)
            if arguments[1] == "build":
                raise pipeline.PipelineError("controlled case build failure", 37)
            directory = Path(next(value.removeprefix("-p:ArtifactsPath=") for value in arguments if value.startswith("-p:ArtifactsPath="))) / "obj"
            directory.mkdir(parents=True)
            (directory / "project.assets.json").write_text(json.dumps({"libraries": {"Hexalith.EventStore.Contracts/" + VERSIONS[0]: {"type": "package"}}}))
        values = {name: "" for name in ("TargetFramework", "TargetFrameworks", "IsTestProject", "XunitTestProject", *ci_config.BUILD_VERSIONS)}
        values["HexalithEventStoreVersion"] = VERSIONS[0]
        flags = release_build.release_flags("1.2.3", SHA, "Example/Fixture")
        with cases.output_scope(self.prepared, self.environment), patch.object(pipeline, "evaluated_properties", side_effect=lambda *_a, **kw: dict(values, **dict(f[3:].split("=", 1) for f in kw["flags"] if f.startswith("-p:")))):
            with self.assertRaisesRegex(ValueError, "controlled case") as error:
                cases.build_declared(self.prepared, flags, fail)
        self.assertEqual(37, error.exception.returncode)
        self.assertEqual(["restore", "build"], [command[1] for command in events])
        for command in events:
            self.assertTrue(set(flags) <= set(command))
            self.assertIn("-p:NuGetAudit=true", command)
            self.assertIn("-p:CustomAfterMicrosoftCommonTargets=" + str(release_build.TARGETS), command)

    def test_finite_input_preflight_and_restored_identity_refusals(self):
        original = self.project.read_text()
        shared = self.root / "Shared.props"
        shared.write_text('<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>')
        self.project.write_text(original.replace('</Project>', '<Import Project="../Shared.props" /></Project>'))
        commit(self.root)
        cases.preflight(self.prepared)
        for xml in ('<ItemGroup><ProjectReference Include="../Child.csproj"><SetTargetFramework>TargetFramework=net10.0;NuGetAudit=false</SetTargetFramework></ProjectReference></ItemGroup>',
                    '<ItemGroup><Compile Include="../../foreign.cs" /></ItemGroup>',
                    '<ItemGroup><Reference Include="Foreign"><HintPath>../../foreign.dll</HintPath></Reference></ItemGroup>',
                    '<ItemGroup><Analyzer Include="../../foreign.dll" /></ItemGroup>',
                    '<UsingTask TaskName="Hidden" AssemblyFile="foreign.dll" />',
                    '<Target Name="Hidden"><CustomTask /></Target>',
                    '<ItemGroup><PackageReference Include="Hexalith.EventStore.Contracts" VersionOverride="9.9.9" /></ItemGroup>',
                    '<ItemGroup><PackageReference Include="Hexalith.EventStore.Contracts" Version="9.9.9" /></ItemGroup>'):
            self.project.write_text(original.replace('</Project>', xml + '</Project>'))
            commit(self.root)
            with self.subTest(xml=xml), self.assertRaises(ValueError):
                cases.preflight(self.prepared)
        self.project.write_text(original.replace('Microsoft.NET.Sdk', 'Foreign.Sdk/1.2.3'))
        commit(self.root)
        with self.assertRaises(ValueError):
            cases.preflight(self.prepared)
        self.project.write_text(original)
        implicit = self.root / "Directory.Build.targets"
        implicit.write_text('<Project><Target Name="Disable" BeforeTargets="CoreCompile"><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Target></Project>')
        commit(self.root)
        with patch.object(pipeline, "run_command") as command, self.assertRaises(ValueError):
            prepared = SimpleNamespace(root=self.root, solution="Fixture.slnx", document=dict(self.prepared.document, solution="Fixture.slnx"))
            pipeline.restore_and_build(prepared)
        command.assert_not_called()
        with patch.object(release_build, "discover_projects") as discover, patch.object(release_build, "initialize_submodules"), patch.object(preparation, "accepted_authority", return_value={}), patch.object(preparation, "prepare_environment", return_value=self.environment), patch.object(preparation, "applied", return_value=cases.output_scope(self.prepared, self.environment)), self.assertRaises(ValueError):
            release_build.build_release(prepared, self.base / "blocked", version="1.2.3", sha=SHA, repository="Example/Fixture")
        discover.assert_not_called()
        artifacts = self.base / "wrong-assets"
        artifacts.mkdir()
        (artifacts / "project.assets.json").write_text(json.dumps({"libraries": {"Hexalith.EventStore.Contracts/9.9.9": {"type": "package"}}}))
        with self.assertRaisesRegex(ValueError, "restored package differs"):
            cases.verify_packages(artifacts, {"HexalithEventStoreVersion": "4.2.0"})

    def test_uncommitted_foreign_symlink_policy_import_and_test_inputs_fail(self):
        for xml in ('<PropertyGroup><NuGetAudit>false</NuGetAudit></PropertyGroup>',
                    '<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>',
                    '<Import Project="/foreign/targets" />', '<Import Project="$(Foreign)/targets" />'):
            original = self.project.read_text()
            self.project.write_text(original.replace("</Project>", xml + "</Project>"))
            commit(self.root)
            with self.subTest(xml=xml), self.assertRaises(ValueError):
                cases.validate_project(self.root, "compat/Consumer.csproj")
            self.project.write_text(original)
            commit(self.root)
        (self.root / "compat/Untracked.csproj").write_text("<Project />")
        with self.assertRaisesRegex(ValueError, "committed"):
            cases.validate_project(self.root, "compat/Untracked.csproj")
        link = self.root / "compat/Linked.csproj"
        link.symlink_to(self.project)
        with self.assertRaisesRegex(ValueError, "symbolic"):
            cases.validate_project(self.root, "compat/Linked.csproj")
        with cases.output_scope(self.prepared, self.environment):
            mapping = json.loads(os.environ[cases.VARIABLE])
            mapping[next(iter(mapping))]["artifactsPath"] = "/arbitrary/build-projects/sdk-3-130-0"
            with patch.dict(os.environ, {cases.VARIABLE: json.dumps(mapping)}), self.assertRaisesRegex(ValueError, "mapping"):
                cases.build_declared(self.prepared, pipeline.build_flags(self.nuget), pipeline.run_command)


class StrictPackageTests(unittest.TestCase):
    def test_authenticated_manifest_round_trip_and_strict_public_payload(self):
        original = package(SHA)
        manifest = {"sha": SHA, "version": "2.3.5", "packages": [{"id": "Fixture.Package", "path": "packages/Fixture.Package.2.3.5.nupkg", "verifyPublishedPackage": True}], "containers": []}
        payload = artifact(SHA, manifest=manifest)
        policies, files = {}, {}
        inventory = release_draft.artifact_inventory(payload, sha=SHA, version="2.3.5", digest=digest(payload),
            repository="Example/Fixture", files=files, package_policies=policies)
        self.assertEqual({"Fixture.Package": package_archive.body_inventory(original)}, policies)
        values = {"inventory": inventory, "package-policies": policies, "version": "2.3.5", "sha": SHA,
            "package-payloads": {"Fixture.Package": release_publish.publication.payload_map(original)},
            "repository": "Example/Fixture", "artifact-run": "123", "artifact-id": "456",
            "artifact-digest": digest(payload)}
        public_files = package_archive.members(original)
        public_files[".signature.p7s"] = b"controlled signature"
        recompressed = io.BytesIO()
        with zipfile.ZipFile(recompressed, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, body in public_files.items():
                archive.writestr(name, body)
        signed = recompressed.getvalue()
        commands = []
        def command(arguments, **kwargs):
            commands.append((arguments, kwargs))
            return SimpleNamespace(returncode=0)
        def download(data):
            response = io.BytesIO(data)
            response.status = 200
            response.headers = {}
            return lambda *_args, **_kwargs: response
        with patch.dict(os.environ, {"NUGET_API_KEY": "secret-control", "GITHUB_TOKEN": "secret-control", "PYTHONPATH": "/module"}):
            release_publish.verify_packages(values, open_url=download(signed), attempts=1, interval=0, run=command)
        self.assertEqual(values["package-payloads"]["Fixture.Package"], values["package-publication"]["packages"][0]["files"])
        self.assertEqual(sha(signed), values["package-publication"]["packages"][0]["archiveSha256"])
        self.assertEqual(1, len(commands))
        args, kwargs = commands[0]
        self.assertEqual(["dotnet", "nuget", "verify"], args[:3])
        self.assertEqual("--all", args[-1])
        self.assertFalse(any("TOKEN" in key or "KEY" in key or key == "PYTHONPATH" for key in kwargs["env"]))
        self.assertFalse(Path(kwargs["cwd"]).exists())
        for change in ("unsigned", "changed", "removed", "added", "duplicate", "unsafe", "signature"):
            members = dict(public_files)
            if change == "unsigned":
                del members[".signature.p7s"]
            elif change == "changed":
                members["lib/net10.0/Fixture.dll"] = b"changed"
            elif change == "removed":
                del members["lib/net10.0/Fixture.dll"]
            elif change == "added":
                members["lib/net10.0/Other.dll"] = b"added"
            elif change == "unsafe":
                members["../unsafe"] = b"unsafe"
            data = zip_bytes([*members.items(), *(([("lib/net10.0/Fixture.dll", b"duplicate")]) if change == "duplicate" else [])])
            with self.subTest(change=change), self.assertRaises(ValueError):
                release_publish.verify_packages(values, open_url=download(data), attempts=1, interval=0,
                    run=lambda *_a, **_k: SimpleNamespace(returncode=1 if change == "signature" else 0))
        manifest["packages"][0]["verifyPublishedPackage"] = False
        payload = artifact(SHA, manifest=manifest)
        with self.assertRaises(ValueError):
            release_draft.artifact_inventory(payload, sha=SHA, version="2.3.5", digest=digest(payload), repository="Example/Fixture")

    def test_real_evaluated_opt_in_opt_out_and_discovery_exclusion(self):
        from test_release_build import fixture
        with tempfile.TemporaryDirectory(prefix="strict-evaluated-") as temporary:
            base = Path(temporary)
            prepared = fixture(base / "source")
            props = prepared.root / "Package.props"
            original = props.read_text()
            flags = release_build.release_flags("2.3.5", SHA, "Hexalith/Hexalith.Builds")
            for value in ("", "false", "False", "true"):
                props.write_text(original.replace("</PropertyGroup>",
                    "<HexalithVerifyPublishedPackage>" + value + "</HexalithVerifyPublishedPackage></PropertyGroup>"))
                projects = release_build.discover_projects(prepared, flags)
                self.assertEqual(value == "true", projects[0].verify_published)
            # A real release with false has exactly the legacy entry keys.
            props.write_text(original.replace("</PropertyGroup>",
                "<HexalithVerifyPublishedPackage>false</HexalithVerifyPublishedPackage></PropertyGroup>"))
            manifest = release_build.build_release(prepared, base / "legacy", version="2.3.5", sha=SHA,
                repository="Hexalith/Hexalith.Builds")
            self.assertEqual({"id", "path"}, set(manifest["packages"][0]))
            props.write_text(original.replace("</PropertyGroup>",
                "<HexalithVerifyPublishedPackage>true</HexalithVerifyPublishedPackage></PropertyGroup>"))
            manifest = release_build.build_release(prepared, base / "enrolled", version="2.3.5", sha=SHA,
                repository="Hexalith/Hexalith.Builds")
            self.assertIs(True, manifest["packages"][0]["verifyPublishedPackage"])
            excluded = SimpleNamespace(root=prepared.root, document=dict(prepared.document, buildProjects=[{
                "id": "auxiliary", "project": projects[0].path, "versions": {"HexalithEventStoreVersion": "4.2.0"}}]))
            self.assertEqual((), release_build.discover_projects(excluded, flags))
            props.write_text(original.replace("</PropertyGroup>",
                "<HexalithVerifyPublishedPackage Condition=\"'$(TargetFramework)' == 'net10.0'\">true</HexalithVerifyPublishedPackage></PropertyGroup>"))
            with self.assertRaisesRegex(ValueError, "differs across"):
                release_build.discover_projects(prepared, flags)

    def test_child_retaining_stdout_cannot_extend_process_deadline(self):
        with tempfile.TemporaryDirectory(prefix="child-pipe-") as temporary:
            directory = Path(temporary)
            script = "import subprocess,sys; from pathlib import Path; child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(5)']); Path('child.pid').write_text(str(child.pid))"
            started = time.monotonic()
            with self.assertRaises(subprocess.TimeoutExpired):
                package_archive.bounded_process([sys.executable, "-c", script], cwd=directory,
                    env=package_archive.private_environment(directory), timeout=0.3)
            self.assertLess(time.monotonic() - started, 2)
            pid = int((directory / "child.pid").read_text())
            status = Path('/proc') / str(pid) / 'stat'
            deadline = time.monotonic() + 0.5
            while status.exists() and status.read_text().split()[2] != 'Z' and time.monotonic() < deadline:
                time.sleep(0.01)
            if status.exists():
                self.assertEqual('Z', status.read_text().split()[2])

    def test_real_dotnet_rejects_invalid_signature(self):
        data = zip_bytes([*package_archive.members(package(SHA)).items(), (".signature.p7s", b"invalid cryptographic signature")])
        with self.assertRaisesRegex(ValueError, "signature verification failed"):
            package_archive.verify_signature(data)

    def test_legacy_manifest_and_legacy_verification_have_no_new_policy(self):
        payload = artifact(SHA)
        policies = {}
        inventory = release_draft.artifact_inventory(payload, sha=SHA, version="2.3.5", digest=digest(payload), repository="Example/Fixture", package_policies=policies)
        self.assertEqual({}, policies)
        response = io.BytesIO(package(SHA))
        response.status = 200
        response.headers = {}
        with patch.object(package_archive, "verify_signature", side_effect=AssertionError("unexpected strict verification")):
            values = {"inventory": inventory, "version": "2.3.5", "sha": SHA,
                "package-payloads": {"Fixture.Package": release_publish.publication.payload_map(package(SHA))},
                "repository": "Example/Fixture", "artifact-run": "123", "artifact-id": "456",
                "artifact-digest": digest(payload)}
            release_publish.verify_packages(values, open_url=lambda *_a, **_k: response, attempts=1, interval=0)
        self.assertEqual(values["package-payloads"]["Fixture.Package"], values["package-publication"]["packages"][0]["files"])


if __name__ == "__main__":
    unittest.main()
