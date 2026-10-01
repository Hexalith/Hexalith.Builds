#!/usr/bin/env python3
"""Current G-6 mutation controls, using immutable historical captures only as test fixtures."""

from __future__ import annotations

import copy
import importlib.util
import json
import hashlib
import shutil
import subprocess
import tempfile
import types
from pathlib import Path
from unittest.mock import patch


TOOLS = Path(__file__).resolve().parent
FIXTURES = TOOLS.parent / "test/fixtures/runtime-toolchain-v2"
PROVENANCE_SHA256 = "f75bf867e002b981fa0d66781ecd6dd3d806d9846578f13dc1eaff86d250a168"
SPEC = importlib.util.spec_from_file_location("current_g6_test_api", TOOLS / "validate-runtime-toolchain-evidence.py")
API = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(API)
CURRENT = API.current_contract()


def write(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def assert_fixture_provenance():
    assert API.sha256(FIXTURES / "provenance.json") == PROVENANCE_SHA256
    manifest = API.read_json(FIXTURES / "provenance.json")
    for item in manifest["files"]:
        path = (FIXTURES / item["path"]).resolve()
        assert path.is_relative_to(FIXTURES.resolve()) and API.sha256(path) == item["sha256"]


def run_source_controls() -> int:
    """Real temporary Git history proves deletion, runtime inputs and both committed gitlinks."""
    with tempfile.TemporaryDirectory(prefix="g6-v2-source-controls-") as temporary:
        root = Path(temporary)
        def git(*args):
            return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True).stdout.strip()
        git("init", "-q")
        git("config", "user.name", "G6 synthetic fixture")
        git("config", "user.email", "g6-fixture@example.invalid")
        (root / "source.cs").write_text("class Source {}\n")
        (root / "runtime.json").write_text('{"enabled":true}\n')
        (root / "start.sh").write_text("#!/bin/sh\nexit 0\n")
        git("add", ".")
        git("commit", "-qm", "test: create synthetic source")
        child = root / "references/Fixture"
        child.mkdir(parents=True)
        def child_git(*args):
            return subprocess.run(["git", "-C", str(child), *args], capture_output=True, text=True, check=True).stdout.strip()
        child_git("init", "-q")
        child_git("config", "user.name", "G6 synthetic fixture")
        child_git("config", "user.email", "g6-fixture@example.invalid")
        (child / "source.cs").write_text("class ChildA {}\n")
        child_git("add", ".")
        child_git("commit", "-qm", "test: create synthetic child")
        child_a = child_git("rev-parse", "HEAD")
        (child / "source.cs").write_text("class ChildB {}\n")
        child_git("commit", "-qam", "test: change synthetic child")
        child_b = child_git("rev-parse", "HEAD")
        child_git("checkout", "-q", child_a)
        (root / ".gitmodules").write_text('[submodule "fixture"]\n\tpath = references/Fixture\n\turl = https://example.invalid/fixture.git\n')
        git("add", ".gitmodules")
        git("update-index", "--add", "--cacheinfo", "160000," + child_a + ",references/Fixture")
        git("commit", "-qm", "test: bind synthetic submodule")
        captured = git("rev-parse", "HEAD")
        baseline = {"consumerInventory": {"roots": ["."]}}
        source = CURRENT.source_manifest(API, root, baseline)
        repositories = [{"path": ".", "revision": captured, "rootGitlink": None}, {"path": "references/Fixture", "revision": child_a, "rootGitlink": child_a}]
        assert not CURRENT.root_source_dirty(API, root, source, repositories)
        (root / "README.md").write_text("metadata only\n")
        git("add", "README.md")
        git("commit", "-qm", "docs: synthetic metadata")
        assert not CURRENT.root_source_dirty(API, root, source, repositories)
        for name in ["runtime.json", "start.sh"]:
            path = root / name
            original = path.read_text()
            path.write_text(original + "changed\n")
            changed = CURRENT.source_manifest(API, root, baseline)
            assert changed != source and CURRENT.root_source_dirty(API, root, changed, repositories)
            path.write_text(original)
        (root / "source.cs").unlink()
        assert CURRENT.root_source_dirty(API, root, CURRENT.source_manifest(API, root, baseline), repositories)
        (root / "source.cs").write_text("class Source {}\n")
        git("update-index", "--cacheinfo", "160000," + child_b + ",references/Fixture")
        git("commit", "-qm", "test: change current committed gitlink")
        child_git("checkout", "-q", child_b)
        assert child_git("rev-parse", "HEAD") == git("ls-files", "--stage", "references/Fixture").split()[1] == child_b
        changed = [{"path": ".", "revision": captured, "rootGitlink": None}, {"path": "references/Fixture", "revision": child_b, "rootGitlink": child_b}]
        assert CURRENT.root_source_dirty(API, root, source, changed)  # captured coordinate disagrees; index/checkout agree
        current = git("rev-parse", "HEAD")
        git("update-index", "--cacheinfo", "160000," + child_a + ",references/Fixture")
        child_git("checkout", "-q", child_a)
        assert child_git("rev-parse", "HEAD") == git("ls-files", "--stage", "references/Fixture").split()[1] == child_a
        changed[0]["revision"] = current
        changed[1].update(revision=child_a, rootGitlink=child_a)
        assert CURRENT.root_source_dirty(API, root, source, changed)  # current committed coordinate disagrees
    return 5


def run_xml_version_controls() -> int:
    baseline = API.read_json(TOOLS / "runtime-toolchain-baseline-2026-10-01.json")
    with tempfile.TemporaryDirectory(prefix="g6-v2-xml-controls-") as temporary:
        root = Path(temporary)
        catalog = root / "references/Hexalith.Builds/Props/Directory.Packages.props"
        catalog.parent.mkdir(parents=True)
        catalog.write_text('<Project><PropertyGroup><HexalithEventStoreVersion Condition="\'$(HexalithEventStoreVersion)\' == \'\'">3.110.0</HexalithEventStoreVersion></PropertyGroup><ItemGroup><PackageVersion Include="Hexalith.Commons" Version="$(HexalithCommonsVersion)" /><PackageVersion Include="Hexalith.EventStore.Client" Version="$(HexalithEventStoreVersion)" /><PackageVersion Include="Dapr.Client"><Version>1.18.10</Version></PackageVersion></ItemGroup></Project>')
        project = root / "consumer.csproj"
        with patch.object(CURRENT, "tracked_files", return_value=["consumer.csproj"]):
            project.write_text('<Project><ItemGroup><PackageReference Include="Dapr.Client"><Version>1.18.10</Version></PackageReference></ItemGroup></Project>')
            assert CURRENT.inventory(API, root, baseline)[0]["pins"][0]["version"] == "1.18.10"
            project.write_text('<Project><ItemGroup><PackageReference Include="Hexalith.Commons" Version="$(HexalithCommonsVersion)" /><PackageReference Include="Hexalith.EventStore.Client" Version="$(HexalithEventStoreVersion)" /></ItemGroup></Project>')
            assert CURRENT.inventory(API, root, baseline)[0]["pins"] == [{"package": "Hexalith.EventStore.Client", "version": "3.110.0", "qualified": True}]
            for declaration in ['VersionOverride="1.18.9"', '><VersionOverride>1.18.9</VersionOverride></PackageReference', '><Version>$(UnknownVersion)</Version></PackageReference', '><Version>1.18.9</Version></PackageReference']:
                item = '<PackageReference Include="Dapr.Client" ' + declaration + (' />' if not declaration.startswith('>') else '>')
                if declaration.startswith('>'):
                    item = '<PackageReference Include="Dapr.Client"' + declaration + '>'
                project.write_text('<Project><ItemGroup>' + item + '</ItemGroup></Project>')
                try:
                    CURRENT.inventory(API, root, baseline)
                except API.ValidationError:
                    pass
                else:
                    raise AssertionError("Effective override/unresolved version passed")
    return 4


def run_file_directive_controls() -> int:
    """Use the actual inventory parser to prove exact preview exclusions in file-based hosts."""
    baseline = API.read_json(TOOLS / "runtime-toolchain-baseline-2026-10-01.json")
    with tempfile.TemporaryDirectory(prefix="g6-v2-file-directives-") as temporary:
        root = Path(temporary)
        catalog = root / "references/Hexalith.Builds/Props/Directory.Packages.props"
        catalog.parent.mkdir(parents=True)
        catalog.write_text('<Project><ItemGroup><PackageVersion Include="Aspire.Hosting.Keycloak" Version="13.6.0-preview.1.26479.8" /></ItemGroup></Project>')
        relative = "references/Hexalith.Platform/apphost.cs"
        host = root / relative
        host.parent.mkdir(parents=True)
        host.write_text("#:sdk Aspire.AppHost.Sdk@13.6.0\n#:package Aspire.Hosting.Keycloak@13.6.0-preview.1.26479.8\n")
        with patch.object(CURRENT, "tracked_files", return_value=[relative]):
            entries = CURRENT.inventory(API, root, baseline)
            preview = next(pin for pin in entries[0]["pins"] if pin["package"] == "Aspire.Hosting.Keycloak")
            assert preview["qualified"] is False
            for version in ("13.5.4-preview.1.26464.4", "13.7.0-preview.1.99999.1"):
                host.write_text("#:sdk Aspire.AppHost.Sdk@13.6.0\n#:package Aspire.Hosting.Keycloak@" + version + "\n")
                try:
                    CURRENT.inventory(API, root, baseline)
                except API.ValidationError:
                    pass
                else:
                    raise AssertionError("Unauthorized file-based preview exclusion passed: " + version)
    return 2


def run_required_package_controls() -> int:
    """Only conditions enclosing controlled PackageReferences affect minimum coverage."""
    baseline = API.read_json(TOOLS / "runtime-toolchain-baseline-2026-10-01.json")
    with tempfile.TemporaryDirectory(prefix="g6-v2-required-packages-") as temporary:
        root = Path(temporary)
        project = root / "AppHost.csproj"
        project.write_text('<Project Sdk="Aspire.AppHost.Sdk/13.6.0"><ItemGroup Condition="Exists(\'unavailable\')"><ProjectReference Include="source.csproj" /></ItemGroup><ItemGroup Condition="Exists(\'uncontrolled\')"><PackageReference Include="Hexalith.Commons" /></ItemGroup><ItemGroup><PackageReference Include="Dapr.Client" /></ItemGroup></Project>')
        assert CURRENT.required_packages(API, root, baseline, "AppHost.csproj") == {"Aspire.Hosting.AppHost", "Dapr.Client"}
        for body in ['<ItemGroup Condition="Exists(\'unavailable\')"><PackageReference Include="Dapr.Client" /></ItemGroup>', '<ItemGroup><PackageReference Include="Dapr.Client" Condition="Exists(\'unavailable\')" /></ItemGroup>']:
            project.write_text('<Project>' + body + '</Project>')
            try:
                CURRENT.required_packages(API, root, baseline, "AppHost.csproj")
            except API.ValidationError as error:
                assert "Unresolved dependency condition" in str(error)
            else:
                raise AssertionError("Unresolved relevant dependency condition passed")
    return 3


def run_controls() -> int:
    assert_fixture_provenance()
    count = run_file_directive_controls() + run_source_controls() + run_xml_version_controls() + run_required_package_controls()
    with tempfile.TemporaryDirectory(prefix="g6-v2-mutations-") as temporary:
        root = Path(temporary)
        baseline = API.read_json(TOOLS / "runtime-toolchain-baseline-2026-10-01.json")
        baseline["consumerInventory"]["roots"] = ["."]
        baseline["pinAudit"]["appHostProjects"] = [{"path": "src/Owner.AppHost/Owner.AppHost.csproj", "version": "13.6.0"}]
        baseline["consumerInventory"]["fileBasedAppHosts"] = ["references/Hexalith.Platform/apphost.cs"]
        baseline_path = root / CURRENT.CANONICAL_ARTIFACTS["baseline-governance"]
        write(baseline_path, baseline)
        source_text = "fixture source\n"
        source = [{"path": "source.cs", "sha256": hashlib.sha256(source_text.encode()).hexdigest()}]
        consumers = [{"path": "source.cs", "pins": []}]
        revision = "a" * 40
        repos = [{"name": "fixture", "path": ".", "revision": revision, "rootGitlink": None, "dirty": False}]
        fake_dirty = [False]
        fake_head = [revision]
        fake_metadata = [False]
        def git(repository, *arguments):
            code = 0
            if arguments[0] == "show":
                output = "changed committed source\n" if fake_dirty[0] else source_text
            elif arguments[0] == "ls-tree":
                output = "source.cs\0"
            elif arguments[0] == "status":
                output = " M README.md\n" if fake_metadata[0] else ""
            elif arguments[0] == "merge-base":
                output = ""
                code = 0 if arguments[2] == revision else 1
            else:
                output = fake_head[0] + "\n"
            return types.SimpleNamespace(stdout=output, returncode=code)
        api = types.SimpleNamespace(**vars(API))
        api.git = git
        captures = FIXTURES
        oq8_root = root / "references/Hexalith.EventStore"
        for relative in ["tools/validate-oq8-platform-evidence.py", "deploy/dapr/statestore-postgresql.yaml", "deploy/dapr/resiliency.yaml"]:
            destination = oq8_root / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(FIXTURES / relative, destination)
        documents = {kind: {"fixture": True} for kind in CURRENT.ARTIFACT_KINDS}
        documents.update({
            "source-state": {"schema": "hexalith.runtime-toolchain-source-state.v2", "files": source, "repositories": repos},
            "consumer-audit": {"schema": "hexalith.runtime-toolchain-consumer-audit.v2", "consumers": consumers, "exclusions": baseline["consumerInventory"]["unqualifiedExclusions"]},
            "observed-versions": {"schema": "hexalith.runtime-toolchain-observed-versions.v2", "observedUtc": "2026-10-01T12:00:00Z", "tuple": baseline["tuple"], "toolsLogSha256": ""},
            "cleanup": {"schema": "hexalith.runtime-toolchain-cleanup.v2", "before": {"fixture": 1}, "after": {"fixture": 1}, "ownedContainers": [{"name": role, "role": role, "id": str(n + 1) * 64, "image": CURRENT.POSTGRES_IMAGE if role == "postgresql" else "redis:7.4" if role == "redis" else "daprio/dapr:1.18.2"} for n, role in enumerate(["placement", "scheduler", "redis", "postgresql"])], "removedContainerIds": [str(n + 1) * 64 for n in range(4)], "ownedProcessesStopped": True, "scratchRemoved": True, "fixtureScratchRemoved": True, "ownedProcessGroups": [100], "daprNamespace": "g6-oq8-fixture", "sidecarNamespaceObservations": [{"node": name, "appId": "sample" if name == "sample" else "eventstore", "processId": 101 + n, "daprNamespace": "g6-oq8-fixture", "nameResolver": "sqlite", "discoveryRegistryIdentitySha256": "c" * 64, "discoveryConfigurationSha256": "d" * 64, "privateDiscoveryRegistryObserved": True} for n, name in enumerate(["sample", "eventstore-1", "eventstore-2", "eventstore-1"])]},
            "limitations": {"items": ["Debug checkout proof only; pending decision."], "publishedEventStoreArchivesQualified": False},
            "attempts": {"attempts": [{"attempt": 1, "exitCode": 0}], "errors": []},
        })
        required = ["install isolated tools", "observe exact tool versions", "fresh EventStore qualifier build", "fresh EventStore support build", "real PostgreSQL two-sidecar stop/restart qualifier", "21 exact deterministic support selectors", "strict EventStore capture validation", "G-6 mutation controls", "root workflow pin contract", "fixture override tests", "isolated container cleanup controls", "McpCli transitive Dapr consumer"]
        log = root / "command.log"
        log.write_text("10.0.401\n13.6.0+abcd\nCLI version: 1.18.0\nRuntime version: 1.18.2\n")
        documents["observed-versions"]["toolsLogSha256"] = API.sha256(log)
        commands = [{"purpose": purpose, "command": "dotnet fixture", "exitCode": 0, "outcome": "passed", "logPath": "command.log", "logSha256": API.sha256(log)} for purpose in required]
        hosts = [item["path"] for item in baseline["pinAudit"]["appHostProjects"]] + baseline["consumerInventory"]["fileBasedAppHosts"]
        outcomes = []
        for number, host in enumerate(hosts):
            purpose = "AppHost fixture " + str(number)
            commands.append({"purpose": purpose, "command": "dotnet build fixture", "exitCode": 0, "outcome": "passed", "logPath": "command.log", "logSha256": API.sha256(log)})
            outcomes.append({"path": host, "purpose": purpose, "exitCode": 0, "configuration": "Debug", "dependencyMode": "packages" if host in baseline["consumerInventory"]["fileBasedAppHosts"] else "source"})
        mcp_project = "references/Hexalith.McpCli/src/Hexalith.McpCli/Hexalith.McpCli.csproj"
        for host in hosts:
            path = root / host
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("#:sdk Aspire.AppHost.Sdk@13.6.0\n#:package CommunityToolkit.Aspire.Hosting.Dapr@13.6.0-beta.910\n#:package Aspire.Hosting.Keycloak@13.6.0-preview.1.26479.8\n" if path.suffix == ".cs" else '<Project Sdk="Aspire.AppHost.Sdk/13.6.0"><ItemGroup><PackageReference Include="CommunityToolkit.Aspire.Hosting.Dapr" /></ItemGroup></Project>')
        mcp = root / mcp_project
        mcp.parent.mkdir(parents=True, exist_ok=True)
        mcp.write_text('<Project />')
        documents["command-record"] = {"commands": commands}
        documents["apphost-outcomes"] = {"projects": outcomes}
        documents["resolved-packages"] = {"projects": [{"path": host, "packages": [{"id": "Dapr.Client", "version": "1.18.10", "qualified": True}] + ([{"id": "Aspire.Hosting.AppHost", "version": "13.6.0", "qualified": True}, {"id": "CommunityToolkit.Aspire.Hosting.Dapr", "version": "13.6.0-beta.910", "qualified": True}] if host != mcp_project else [])} for host in hosts + [mcp_project]]}
        for project in documents["resolved-packages"]["projects"]:
            if project["path"] in {"references/Hexalith.Builds/src/hosts/Hexalith.Builds.Module.AppHost/Hexalith.Builds.Module.AppHost.csproj", "references/Hexalith.Platform/apphost.cs"}:
                project["packages"].append({"id": "Aspire.Hosting.Keycloak", "version": "13.6.0-preview.1.26479.8", "qualified": False})
        for kind, filename in [("eventstore-observations", "observations.json"), ("eventstore-qualification-results", "test-results.json"), ("eventstore-support-results", "deterministic-support.json")]:
            documents[kind] = API.read_json(captures / filename)
        for kind in ["eventstore-qualification-results", "eventstore-support-results"]:
            documents[kind]["command"] = documents[kind]["command"].replace("/Release/", "/Debug/")
        documents["eventstore-observations"]["capturedOn"] = "2026-10-01"
        documents["eventstore-observations"]["captureKind"] = "debug-entry-binaries-test-seams-sidecar-postgresql"
        documents["eventstore-observations"]["executionConfiguration"]["shippedReleaseEntryAssemblies"] = False
        import shlex
        selectors = documents["eventstore-support-results"]["selectors"]
        expected = CURRENT.expected_commands(API, baseline, selectors, ".")
        for outcome in outcomes:
            expected[outcome["purpose"]] = ["dotnet", "build", outcome["path"], "--configuration", "Debug", "--no-incremental", "-v:minimal", "-p:BaseIntermediateOutputPath=[scratch]/platform-obj/"] if outcome["dependencyMode"] == "packages" else ["dotnet", "build", outcome["path"], *CURRENT.BUILD_ARGUMENTS]
        for command in commands:
            command["command"] = shlex.join(expected[command["purpose"]])
        documents["baseline-governance"] = baseline
        paths = {kind: root / CURRENT.CANONICAL_ARTIFACTS.get(kind, kind + ".json") for kind in documents}
        def restore():
            for kind, document in documents.items():
                write(paths[kind], document)
            receipt = {"schemaVersion": 1, "validation": "passed", "observationsSha256": API.sha256(paths["eventstore-observations"]), "testResultsSha256": API.sha256(paths["eventstore-qualification-results"]), "deterministicSupportSha256": API.sha256(paths["eventstore-support-results"])}
            write(paths["eventstore-capture-validation"], receipt)
            return {"schema": "hexalith.runtime-toolchain-evidence.v2", "baseline": {"path": baseline_path.relative_to(root).as_posix(), "sha256": API.sha256(baseline_path)}, "capturedUtc": "2026-10-01T12:00:00Z", "repositories": copy.deepcopy(repos), "artifacts": [{"kind": kind, "path": path.relative_to(root).as_posix(), "sha256": API.sha256(path)} for kind, path in paths.items()], "observedVersions": baseline["tuple"], "testCounts": {"qualification": {"selectors": 1, "total": 1, "passed": 1, "failed": 0, "skipped": 0}, "support": {"selectors": 21, "total": 33, "passed": 33, "failed": 0, "skipped": 0}}, "approval": API.baseline_approval(baseline), "status": "pending", "technicalValidity": True, "usableAsPrerequisite": False, "closure": {"committed": True, "sourceManifestSha256": API.manifest_digest(source)}, "acceptance": None, "containment": baseline["containment"], "rollback": baseline["rollback"]}
        packet_path = root / "packet.json"
        def validate(packet):
            write(packet_path, packet)
            CURRENT.validate_packet(api, root, packet_path, baseline_path, baseline, True)
        def mutate_artifact(packet, kind, edit):
            document = API.read_json(paths[kind])
            edit(document)
            write(paths[kind], document)
            next(item for item in packet["artifacts"] if item["kind"] == kind)["sha256"] = API.sha256(paths[kind])
            if kind in {"eventstore-observations", "eventstore-qualification-results", "eventstore-support-results"}:
                receipt = {"schemaVersion": 1, "validation": "passed", "observationsSha256": API.sha256(paths["eventstore-observations"]), "testResultsSha256": API.sha256(paths["eventstore-qualification-results"]), "deterministicSupportSha256": API.sha256(paths["eventstore-support-results"])}
                write(paths["eventstore-capture-validation"], receipt)
                next(item for item in packet["artifacts"] if item["kind"] == "eventstore-capture-validation")["sha256"] = API.sha256(paths["eventstore-capture-validation"])
        mutations = [
            ("integer technical validity", lambda p: p.update(technicalValidity=1)),
            ("integer committed closure", lambda p: p["closure"].update(committed=1)),
            ("boolean qualifier selectors", lambda p: p["testCounts"]["qualification"].update(selectors=True)),
            ("boolean qualifier failed count", lambda p: p["testCounts"]["qualification"].update(failed=False)),
            ("boolean support skipped count", lambda p: p["testCounts"]["support"].update(skipped=False)),
            ("duplicate removal identity", lambda p: mutate_artifact(p, "cleanup", lambda d: d["removedContainerIds"].__setitem__(1, d["removedContainerIds"][0]))),
            ("Release qualifier build", lambda p: mutate_artifact(p, "command-record", lambda d: next(c for c in d["commands"] if c["purpose"] == "fresh EventStore qualifier build").update(command=next(c for c in d["commands"] if c["purpose"] == "fresh EventStore qualifier build")["command"].replace("Debug", "Release")))),
            ("package qualifier build", lambda p: mutate_artifact(p, "command-record", lambda d: next(c for c in d["commands"] if c["purpose"] == "fresh EventStore qualifier build").update(command=next(c for c in d["commands"] if c["purpose"] == "fresh EventStore qualifier build")["command"].replace("UseHexalithProjectReferences=true", "UseHexalithProjectReferences=false")))),
            ("wrong support command selector", lambda p: mutate_artifact(p, "command-record", lambda d: next(c for c in d["commands"] if c["purpose"] == "21 exact deterministic support selectors").update(command=next(c for c in d["commands"] if c["purpose"] == "21 exact deterministic support selectors")["command"].replace(selectors[0], "other.method")))),
            ("empty owned roles", lambda p: mutate_artifact(p, "cleanup", lambda d: d.update(ownedContainers=[], removedContainerIds=[]))),
            ("missing owned role", lambda p: mutate_artifact(p, "cleanup", lambda d: d["ownedContainers"].pop())),
            ("duplicate owned identity", lambda p: mutate_artifact(p, "cleanup", lambda d: d["ownedContainers"][1].update(id=d["ownedContainers"][0]["id"]))),
            ("wrong owned image", lambda p: mutate_artifact(p, "cleanup", lambda d: d["ownedContainers"][0].update(image="redis:7.4"))),
            ("runner error contradicts success", lambda p: mutate_artifact(p, "attempts", lambda d: d.update(errors=["Source snapshot changed during execution"]))),
            ("missing implicit hosting dependency", lambda p: mutate_artifact(p, "resolved-packages", lambda d: d["projects"][0].update(packages=[x for x in d["projects"][0]["packages"] if x["id"] != "Aspire.Hosting.AppHost"]))),
            ("missing declared Toolkit dependency", lambda p: mutate_artifact(p, "resolved-packages", lambda d: d["projects"][0].update(packages=[x for x in d["projects"][0]["packages"] if x["id"] != "CommunityToolkit.Aspire.Hosting.Dapr"]))),
            ("missing McpCli Dapr dependency", lambda p: mutate_artifact(p, "resolved-packages", lambda d: next(r for r in d["projects"] if r["path"] == mcp_project).update(packages=[{"id": "NSubstitute", "version": "6.2.0", "qualified": True}]))),

            ("pending usability", lambda p: p.update(usableAsPrerequisite=True)),
            ("pending acceptance", lambda p: p.update(acceptance={"decision": "accept"})),
            ("unauthorized status", lambda p: p.update(status="accepted")),
            ("capture before authorization", lambda p: p.update(capturedUtc="2026-10-01T07:00:00Z")),
            ("tuple drift", lambda p: p["observedVersions"].update(aspireSdk="13.5.4")),
            ("missing artifact", lambda p: p["artifacts"].pop()),
            ("stale artifact", lambda p: p["artifacts"][0].update(sha256="0" * 64)),
            ("unapproved run", lambda p: p["approval"].update(approvedBy="Inferred Owner")),
            ("wrong checkout", lambda p: p["repositories"][0].update(revision="0" * 40)),
            ("false clean claim", lambda p: fake_dirty.__setitem__(0, True)),
            ("stale OQ8 capture", lambda p: mutate_artifact(p, "eventstore-observations", lambda d: d.update(capturedOn="2026-09-30"))),
            ("stale tool observation", lambda p: mutate_artifact(p, "observed-versions", lambda d: d.update(observedUtc="2026-09-30T12:00:00Z"))),
            ("tool-log crosswalk", lambda p: mutate_artifact(p, "observed-versions", lambda d: d.update(toolsLogSha256="0" * 64))),
            ("tool schema drift", lambda p: mutate_artifact(p, "observed-versions", lambda d: d.update(schema="old-schema"))),
            ("wrong source hash", lambda p: mutate_artifact(p, "source-state", lambda d: d["files"][0].update(sha256="0" * 64))),
            ("consumer omission", lambda p: mutate_artifact(p, "consumer-audit", lambda d: d.update(consumers=[]))),
            ("shared restart", lambda p: mutate_artifact(p, "cleanup", lambda d: d.update(after={"fixture": 2}))),
            ("unremoved container", lambda p: mutate_artifact(p, "cleanup", lambda d: d.update(removedContainerIds=[]))),
            ("namespace collision", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][0].update(daprNamespace="default"))),
            ("missing private resolver", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][0].pop("nameResolver"))),
            ("shared mDNS discovery", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][0].update(nameResolver="mdns"))),
            ("private registry absent", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][0].update(privateDiscoveryRegistryObserved=False))),
            ("divergent registry identity", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][-1].update(discoveryRegistryIdentitySha256="e" * 64))),
            ("missing resolver configuration", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][0].update(discoveryConfigurationSha256=None))),
            ("divergent resolver configuration", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"][-1].update(discoveryConfigurationSha256="e" * 64))),
            ("missing discovery restart", lambda p: mutate_artifact(p, "cleanup", lambda d: d["sidecarNamespaceObservations"].pop())),
            ("fixture scratch leak", lambda p: mutate_artifact(p, "cleanup", lambda d: d.update(fixtureScratchRemoved=False))),
            ("process leak", lambda p: mutate_artifact(p, "cleanup", lambda d: d.update(ownedProcessesStopped=False))),
            ("scratch leak", lambda p: mutate_artifact(p, "cleanup", lambda d: d.update(scratchRemoved=False))),
            ("unqualified bypass", lambda p: mutate_artifact(p, "resolved-packages", lambda d: d["projects"][0]["packages"][0].update(version="0.0.1", qualified=False))),
            ("older transitive catalog preview", lambda p: mutate_artifact(p, "resolved-packages", lambda d: next(r for r in d["projects"] if r["path"] == "references/Hexalith.Platform/apphost.cs")["packages"][-1].update(version="13.5.4-preview.1.26464.4"))),
            ("preview runtime qualification", lambda p: mutate_artifact(p, "resolved-packages", lambda d: next(r for r in d["projects"] if r["path"] == "references/Hexalith.Platform/apphost.cs")["packages"][-1].update(qualified=True))),
            ("resolved drift", lambda p: mutate_artifact(p, "resolved-packages", lambda d: d["projects"][0]["packages"][0].update(version="0.0.1"))),
            ("missing host", lambda p: mutate_artifact(p, "apphost-outcomes", lambda d: d["projects"].pop())),
            ("false archive qualification", lambda p: mutate_artifact(p, "limitations", lambda d: d.update(publishedEventStoreArchivesQualified=True))),
            ("contradictory attempt", lambda p: mutate_artifact(p, "attempts", lambda d: d["attempts"][0].update(exitCode=1))),
            ("secret-bearing limitation", lambda p: mutate_artifact(p, "limitations", lambda d: d.update(items=["Bearer private-value"]))),
            ("duplicate execution", lambda p: mutate_artifact(p, "eventstore-observations", lambda d: d["observations"]["writers_failover"].update(sampleExecutions=2))),
            ("restart replay mismatch", lambda p: mutate_artifact(p, "eventstore-observations", lambda d: d["observations"]["writers_failover"].update(restartedNodeReplayExact=False))),
            ("authority mismatch", lambda p: mutate_artifact(p, "eventstore-observations", lambda d: d["observations"]["authority_change"].update(canonicalAuthorityCount=2))),
            ("persistence mismatch", lambda p: mutate_artifact(p, "eventstore-observations", lambda d: d["observations"]["capture"]["after"].update(aggregateEventRows=0))),
            ("support identity drift", lambda p: mutate_artifact(p, "eventstore-support-results", lambda d: d["methods"][0].update(identity="other.method"))),
            ("support case drift", lambda p: mutate_artifact(p, "eventstore-support-results", lambda d: d["methods"][0].update(observedCases=2))),
            ("qualification skip", lambda p: mutate_artifact(p, "eventstore-qualification-results", lambda d: d["summary"].update(passed=0, skipped=1))),
            ("missing McpCli command", lambda p: mutate_artifact(p, "command-record", lambda d: d.update(commands=[c for c in d["commands"] if c["purpose"] != "McpCli transitive Dapr consumer"]))),
            ("failed McpCli command", lambda p: mutate_artifact(p, "command-record", lambda d: next(c for c in d["commands"] if c["purpose"] == "McpCli transitive Dapr consumer").update(exitCode=1))),
            ("missing McpCli packages", lambda p: mutate_artifact(p, "resolved-packages", lambda d: d.update(projects=[r for r in d["projects"] if r["path"] != mcp_project]))),
            ("additional failed command", lambda p: mutate_artifact(p, "command-record", lambda d: d["commands"].append(dict(d["commands"][0], purpose="additional actual command", exitCode=1)))),
            ("failed technical claim", lambda p: mutate_artifact(p, "command-record", lambda d: d["commands"][0].update(exitCode=1))),
        ]
        for purpose in required + [item["purpose"] for item in outcomes]:
            mutations.append(("command substitution " + purpose, lambda p, purpose=purpose: mutate_artifact(p, "command-record", lambda d: next(c for c in d["commands"] if c["purpose"] == purpose).update(command="echo passed"))))
        for kind in CURRENT.CANONICAL_ARTIFACTS:
            mutations.append(("policy redirection " + kind, lambda p, kind=kind: next(item for item in p["artifacts"] if item["kind"] == kind).update(path="command.log", sha256=API.sha256(log))))
        with patch.object(CURRENT, "source_manifest", return_value=source), patch.object(CURRENT, "inventory", return_value=consumers):
            validate(restore())
            # A named accepted packet can follow metadata-only evidence/acceptance commits.
            accepted = restore()
            reviewed_hash = hashlib.sha256((json.dumps(accepted, ensure_ascii=False, indent=2) + "\n").encode()).hexdigest()
            accepted.update(status="accepted", usableAsPrerequisite=True, acceptance={
                "approvedBy": "Jérôme Piquot", "approvedAtUtc": "2026-10-01T12:01:00Z",
                "decision": "accept", "reviewedPacketSha256": reviewed_hash})
            fake_head[0] = "b" * 40
            fake_metadata[0] = True
            write(packet_path, accepted)
            CURRENT.validate_packet(api, root, packet_path, baseline_path, baseline, False)
            fake_dirty[0] = True
            try:
                CURRENT.validate_packet(api, root, packet_path, baseline_path, baseline, False)
            except API.ValidationError:
                count += 1
            else:
                raise AssertionError("Accepted root source mutation passed")
            fake_dirty[0] = False
            fake_metadata[0] = False
            fake_head[0] = revision
            for label, mutation in mutations:
                fake_dirty[0] = False
                packet = restore()
                mutation(packet)
                try:
                    validate(packet)
                except API.ValidationError:
                    count += 1
                else:
                    raise AssertionError("Current mutation was accepted: " + label)
    print(f"G6-CURRENT-MUTATIONS-PASSED: {count} controls plus accepted metadata-commit positive control; historical captures used only as hermetic fixtures")
    return count


if __name__ == "__main__":
    run_controls()
