"""Current G-6 contracts. Historical v1 rules remain in the original validator."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import importlib.util
import json
import re
import shlex
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path


_catalog_spec = importlib.util.spec_from_file_location("hexalith_evaluated_catalog", Path(__file__).with_name("evaluated_catalog.py"))
assert _catalog_spec is not None and _catalog_spec.loader is not None
_catalog_module = importlib.util.module_from_spec(_catalog_spec)
_catalog_spec.loader.exec_module(_catalog_module)


ARTIFACT_KINDS = {
    "fixture-diagnostics", "source-state", "consumer-audit", "observed-versions", "command-record", "apphost-outcomes",
    "resolved-packages", "cleanup", "limitations", "attempts", "eventstore-observations",
    "eventstore-qualification-results", "eventstore-support-results", "eventstore-capture-validation",
    "baseline-governance", "evidence-schema", "evidence-validator", "current-validator", "mutation-tests",
    "current-mutation-tests", "central-catalog", "qualification-runner",
}
FIELDS = {
    "schema", "baseline", "capturedUtc", "repositories", "artifacts", "observedVersions",
    "testCounts", "approval", "status", "technicalValidity", "usableAsPrerequisite",
    "closure", "acceptance", "containment", "rollback",
}

CANONICAL_ARTIFACTS = {
    "baseline-governance": "references/Hexalith.Builds/Tools/runtime-toolchain-baseline-2026-10-01.json",
    "evidence-schema": "references/Hexalith.Builds/schemas/hexalith.runtime-toolchain-evidence.v2.json",
    "evidence-validator": "references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py",
    "current-validator": "references/Hexalith.Builds/Tools/runtime_toolchain_v2.py",
    "mutation-tests": "references/Hexalith.Builds/Tools/test-runtime-toolchain-evidence-validator.py",
    "current-mutation-tests": "references/Hexalith.Builds/Tools/test_runtime_toolchain_v2.py",
    "central-catalog": "references/Hexalith.Builds/Props/Directory.Packages.props",
    "qualification-runner": "tools/qualification/run_g6_qualification.py",
}
BUILD_ARGUMENTS = ["--configuration", "Debug", "--no-incremental", "-m:1", "-p:UseHexalithProjectReferences=true", "-v:minimal"]
LIVE_PROJECT = "tests/Hexalith.EventStore.Server.LiveSidecar.Tests/Hexalith.EventStore.Server.LiveSidecar.Tests.csproj"
SUPPORT_PROJECT = "tests/Hexalith.EventStore.Server.Tests/Hexalith.EventStore.Server.Tests.csproj"
LIVE_DLL = "references/Hexalith.EventStore/tests/Hexalith.EventStore.Server.LiveSidecar.Tests/bin/Debug/net10.0/Hexalith.EventStore.Server.LiveSidecar.Tests.dll"
SUPPORT_DLL = "references/Hexalith.EventStore/tests/Hexalith.EventStore.Server.Tests/bin/Debug/net10.0/Hexalith.EventStore.Server.Tests.dll"
FIXTURE_CLASSES = ["Hexalith.EventStore.Server.LiveSidecar.Tests.Fixtures." + name for name in (
    "Oq8QualificationOverridesTests", "DockerPublishedPortResolverTests", "Oq8DiscoveryConfigurationTests",
    "Oq8InvocationDiagnosticHandlerTests", "Oq8OwnedContainerLaunchTests")]
POSTGRES_IMAGE = "postgres@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636"


def relevant_source(relative: str) -> bool:
    path = Path(relative)
    if any(part in {"_bmad", "_bmad-output", ".agents", ".claude", ".codex", "docs", "evidence"} for part in path.parts):
        return False
    return path.suffix.lower() in {".cs", ".csproj", ".props", ".targets", ".slnx", ".py", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".cmd", ".bat", ".yml", ".yaml", ".json", ".razor", ".cshtml", ".js", ".jsx", ".ts", ".tsx", ".css", ".html", ".resx", ".config", ".env"} or path.name in {".editorconfig", ".gitattributes", ".gitmodules", "Dockerfile", ".dockerignore"}


def declared_version(api, item, catalog: dict, relative: str, properties: dict | None = None) -> str:
    package = item.get("Include") or item.get("Update")
    api.require(item.get("VersionOverride") is None and item.find("VersionOverride") is None,
                f"Controlled VersionOverride is forbidden: {relative}::{package}")
    values = [value for value in [item.get("Version"), item.findtext("Version")] if value is not None]
    api.require(len(set(values)) <= 1, f"Conflicting version declarations: {relative}::{package}")
    version = values[0] if values else catalog.get(package)
    api.require(isinstance(version, str) and bool(version.strip()), f"Missing package version: {relative}::{package}")
    if version in {"$(HexalithEventStorePackageVersion)", "$(HexalithEventStoreVersion)"} and controlled_field(package or "") == "eventStorePackageVersion":
        version = (properties or {}).get("HexalithEventStoreVersion", catalog.get(package))
    if isinstance(version, str):
        version = re.sub(r"\$\(([^)]+)\)", lambda match: (properties or {}).get(match[1], match[0]), version)
    api.require(isinstance(version, str) and "$" not in version and "@(" not in version,
                f"Unresolved controlled version: {relative}::{package}")
    return version.strip()


def apphost_sdk_versions(api, document: ET.Element, properties: dict, project_path: Path | None = None) -> set[str]:
    """Evaluate controlled SDK declarations without SDK restoration or application startup."""
    try:
        if project_path is not None:
            return _catalog_module.apphost_sdk_versions(project_path, properties)
        with tempfile.TemporaryDirectory(prefix="g6-sdk-document-") as temporary:
            path = Path(temporary) / "consumer.csproj"
            path.write_text(ET.tostring(document, encoding="unicode"))
            return _catalog_module.apphost_sdk_versions(path, properties)
    except (ValueError, OSError, ET.ParseError) as error:
        raise api.ValidationError(str(error)) from error


def required_packages(api, workspace: Path, baseline: dict, relative: str) -> set[str]:
    """Minimum effective Debug/source declarations plus SDK and known transitive consumers."""
    path = workspace / relative
    required = set()
    if relative in baseline["consumerInventory"]["fileBasedAppHosts"]:
        required = {package for package, _ in re.findall(r"(?m)^#:?(?:sdk|package)\s+(\S+)@(\S+)\s*$", path.read_text()) if controlled_field(package)}
    else:
        document = ET.fromstring(api.comment_free_text(path))
        for item in document.iter():
            item.tag = item.tag.rsplit("}", 1)[-1]
        def active(condition):
            if not condition:
                return True
            expression = re.sub(r"\$\((UseHexalithProjectReferences|Hexalith\w+FromSource)\)", "true", condition)
            expression = expression.replace("$(Configuration)", "Debug")
            match = re.fullmatch(r"\s*'([^']*)'\s*(==|!=)\s*'([^']*)'\s*", expression)
            api.require(match is not None and "$" not in expression, f"Unresolved dependency condition: {relative}")
            return (match[1] == match[3]) == (match[2] == "==")
        def visit(element, enabled=True):
            if not any(item.tag == "PackageReference" and controlled_field(item.get("Include") or item.get("Update") or "") for item in element.iter()):
                return
            enabled = enabled and active(element.get("Condition"))
            package = element.get("Include") or element.get("Update")
            if enabled and element.tag == "PackageReference" and controlled_field(package or ""):
                required.add(package)
            for child in element:
                visit(child, enabled)
        visit(document)
        sdk_properties = {"HexalithAspireAppHostSdkVersion": baseline["tuple"]["aspireSdk"]}
        if apphost_sdk_versions(api, document, sdk_properties, path):
            required.add("Aspire.AppHost.Sdk")
    if "Aspire.AppHost.Sdk" in required:
        required.remove("Aspire.AppHost.Sdk")
        required.add("Aspire.Hosting.AppHost")
    if relative == "references/Hexalith.McpCli/src/Hexalith.McpCli/Hexalith.McpCli.csproj":
        required.add("Dapr.Client")
    return required


def expected_commands(api, baseline: dict, selectors: list[str], capture: str) -> dict[str, list[str]]:
    """Closed executable argument contracts; paths are normalized after shell parsing."""
    qualifier = api.QUALIFICATION_IDENTITY
    version_probe = "import subprocess; [subprocess.run(c,check=True) for c in [['dotnet','--version'],['aspire','--version'],['dapr','--runtime-path','[scratch]/runtime','--version']]]"
    result = {
        "install isolated tools": ["python3", "tools/qualification/run_g6_qualification.py", "--baseline", CANONICAL_ARTIFACTS["baseline-governance"], "--install-tools", "[scratch]"],
        "observe exact tool versions": ["python3", "-c", version_probe],
        "fresh EventStore qualifier build": ["dotnet", "build", LIVE_PROJECT, *BUILD_ARGUMENTS],
        "fresh EventStore support build": ["dotnet", "build", SUPPORT_PROJECT, *BUILD_ARGUMENTS],
        "real PostgreSQL two-sidecar stop/restart qualifier": ["dotnet", LIVE_DLL, "-method", qualifier, "-noColor", "-result-ctrf", "[scratch]/qualifier-ctrf.json"],
        "21 exact deterministic support selectors": ["dotnet", SUPPORT_DLL, *[arg for selector in selectors for arg in ("-method", selector)], "-noColor", "-result-ctrf", "[scratch]/support-ctrf.json"],
        "fixture override tests": ["dotnet", LIVE_DLL, *[arg for name in FIXTURE_CLASSES for arg in ("-class", name)], "-noColor"],
        "strict EventStore capture validation": ["python3", "tools/validate-oq8-platform-evidence.py", "--capture-directory", capture, "--ctrf", "[scratch]/qualifier-ctrf.json", "--support-ctrf", "[scratch]/support-ctrf.json", "--expected-runtime-version", baseline["tuple"]["daprRuntime"], "--expected-configuration", "Debug"],
        "G-6 mutation controls": ["python3", CANONICAL_ARTIFACTS["mutation-tests"]],
        "root workflow pin contract": ["pwsh", "-NoProfile", "-File", "tests/tools/run-ci-workflow-gates.ps1"],
        "isolated container cleanup controls": ["python3", "tests/tools/test_g6_qualification_runner.py"],
        "McpCli transitive Dapr consumer": ["dotnet", "build", "references/Hexalith.McpCli/src/Hexalith.McpCli/Hexalith.McpCli.csproj", *BUILD_ARGUMENTS],
    }
    return result


def controlled_field(package: str) -> str | None:
    if package == "Aspire.AppHost.Sdk" or package.startswith("Aspire.Hosting"):
        return "aspireSdk"
    if package == "CommunityToolkit.Aspire.Hosting.Dapr":
        return "communityToolkitAspireDapr"
    if package.startswith("Dapr."):
        return "daprDotnetPackages"
    if package.startswith("Microsoft.FluentUI.AspNetCore.Components"):
        return "fluentUi"
    if package == "NSubstitute":
        return "nSubstitute"
    if package.startswith("Fluxor"):
        return "fluxor"
    if package.startswith("Hexalith.EventStore"):
        return "eventStorePackageVersion"
    return None


def tracked_files(api, workspace: Path, roots: list[str]) -> list[str]:
    files = []
    for root in roots:
        result = api.git(workspace / root, "ls-files", "--cached", "--others", "--exclude-standard", "-z")
        api.require(result.returncode == 0, f"Cannot inventory repository: {root}")
        for relative in result.stdout.split("\0"):
            path = Path(root) / relative
            if relative and "references" not in Path(relative).parts and (workspace / path).is_file():
                files.append(path.as_posix())
    return sorted(set(files))


def inventory(api, workspace: Path, baseline: dict) -> list[dict]:
    catalog = workspace / "references/Hexalith.Builds/Props/Directory.Packages.props"
    try:
        properties, evaluated_versions = _catalog_module.evaluate_catalog(catalog)
        conditioned_names = _catalog_module.conditioned_catalog_project_names(catalog)
    except (ValueError, OSError, ET.ParseError) as error:
        raise api.ValidationError(str(error)) from error
    versions = {package: version for package, version in evaluated_versions.items() if controlled_field(package)}
    catalog_by_consumer = {}
    exclusions = {(item["path"], item["package"], item["version"]) for item in baseline["consumerInventory"]["unqualifiedExclusions"]}
    entries = []
    for relative in tracked_files(api, workspace, baseline["consumerInventory"]["roots"]):
        path = workspace / relative
        if any(part in {"_bmad", "_bmad-output", ".agents", ".claude", ".codex", "docs"} for part in path.parts) or "/test/fixtures/" in "/" + relative:
            continue
        consumer_properties, consumer_versions = properties, versions
        if path.suffix in {".csproj", ".props", ".targets"} and path.stem in conditioned_names:
            if path.stem not in catalog_by_consumer:
                try:
                    selected_properties, selected_versions = _catalog_module.evaluate_catalog(
                        catalog, consumer_project_name=path.stem)
                except (ValueError, OSError, ET.ParseError) as error:
                    raise api.ValidationError(str(error)) from error
                catalog_by_consumer[path.stem] = (
                    selected_properties,
                    {package: version for package, version in selected_versions.items() if controlled_field(package)})
            consumer_properties, consumer_versions = catalog_by_consumer[path.stem]
        pins = []
        if path.name == "global.json":
            version = api.read_json(path).get("sdk", {}).get("version")
            api.require(version == baseline["tuple"]["dotnetSdk"], f"SDK drift: {relative}")
            pins.append({"package": "dotnetSdk", "version": version, "qualified": True})
        elif path.suffix in {".csproj", ".props", ".targets"}:
            try:
                document = ET.fromstring(api.comment_free_text(path))
            except ET.ParseError as error:
                raise api.ValidationError(f"Malformed consumer XML: {relative}") from error
            for sdk_version in sorted(apphost_sdk_versions(api, document, consumer_properties, path)):
                pins.append({"package": "Aspire.AppHost.Sdk", "version": sdk_version, "qualified": True})
            for item in document.iter():
                if item.tag not in {"PackageReference", "PackageVersion"}:
                    continue
                package = item.get("Include") or item.get("Update")
                field = controlled_field(package or "")
                if field is None:
                    continue
                version = declared_version(api, item, consumer_versions, relative, consumer_properties)
                excluded = (relative, package, version) in exclusions or (
                    "references/Hexalith.Builds/Props/Directory.Packages.props", package, version) in exclusions
                pins.append({"package": package, "version": version, "qualified": not excluded})
        elif relative in baseline["consumerInventory"]["fileBasedAppHosts"]:
            for package, version in re.findall(r"(?m)^#:?(?:sdk|package)\s+(\S+)@(\S+)\s*$", path.read_text()):
                if controlled_field(package):
                    excluded = (relative, package, version) in exclusions or (
                        "references/Hexalith.Builds/Props/Directory.Packages.props", package, version) in exclusions
                    pins.append({"package": package, "version": version, "qualified": not excluded})
        if not pins:
            continue
        for pin in pins:
            if pin["package"] == "dotnetSdk" or not pin["qualified"]:
                continue
            field = controlled_field(pin["package"])
            expected = baseline["qualification"][field] if field == "eventStorePackageVersion" else baseline["tuple"][field]
            api.require(pin["version"] == expected, f"Consumer pin drift: {relative}::{pin['package']} {pin['version']} != {expected}")
        entries.append({"path": relative, "pins": sorted(pins, key=lambda item: (item["package"], item["version"]))})
    return entries


def validate_baseline(api, workspace: Path, path: Path, baseline: dict) -> dict:
    api.exact_fields(baseline, {
        "schema", "approvedBy", "approvedOn", "ownerRoles", "tuple", "dispositions", "rollback",
        "containment", "pinAudit", "qualification", "consumerInventory",
    }, "Current baseline")
    api.require(baseline["schema"] == "hexalith.runtime-toolchain-baseline.v2", "Current baseline schema drift")
    legacy = {key: value for key, value in copy.deepcopy(baseline).items() if key not in {"qualification", "consumerInventory"}}
    legacy["schema"] = "hexalith.runtime-toolchain-baseline.v1"
    api._validate_baseline(workspace, path, document=legacy)
    qualification = api.exact_fields(baseline["qualification"], {
        "approvedAtUtc", "approvalScope", "specPath", "eventStorePackageVersion", "configuration",
        "dependencyMode", "acceptanceRequires", "frozenIntentSha256",
    }, "Qualification authorization")
    api.require_utc_timestamp(qualification["approvedAtUtc"], "Qualification approval")
    api.require(qualification["configuration"] == "Debug" and qualification["dependencyMode"] == "source", "Local qualification must use Debug/source")
    api.require(qualification["approvalScope"] == "spec, proposed tuple, and qualification run", "Run approval scope drift")
    api.require(qualification["acceptanceRequires"] == "named-decision-on-reviewed-packet-hash-and-committed-closure", "Acceptance scope drift")
    spec = api.resolve_artifact(workspace, qualification["specPath"], "approved spec").read_text()
    api.require(f"approved_at_utc: '{qualification['approvedAtUtc']}'" in spec and "approval_decision: 'Approve and stop'" in spec, "Run inputs are not approved by the bound spec")
    frozen = re.search(r"<frozen-after-approval[\s\S]*?</frozen-after-approval>", spec)
    api.require(frozen is not None and hashlib.sha256(frozen[0].encode()).hexdigest() == qualification["frozenIntentSha256"], "Approved frozen intent hash drift")
    api.require(qualification["eventStorePackageVersion"] == "3.110.0", "P1R EventStore binding drift")
    consumers = api.exact_fields(baseline["consumerInventory"], {"roots", "fileBasedAppHosts", "unqualifiedExclusions"}, "Consumer inventory")
    modules = [line.split("=", 1)[1].strip() for line in (workspace / ".gitmodules").read_text().splitlines() if line.strip().startswith("path =")]
    api.require(consumers["roots"] == [".", *modules], "Consumer roots must cover every root-declared submodule exactly")
    api.require(consumers["fileBasedAppHosts"] == ["references/Hexalith.Platform/apphost.cs"], "File-based AppHost coverage drift")
    for exclusion in consumers["unqualifiedExclusions"]:
        api.exact_fields(exclusion, {"path", "package", "version", "reason"}, "Unqualified exclusion")
        text = api.resolve_artifact(workspace, exclusion["path"], "excluded consumer").read_text()
        api.require(exclusion["version"] in text and exclusion["reason"].strip(), "Unqualified exclusion is ungrounded")
    inventory(api, workspace, baseline)
    return baseline


def source_manifest(api, workspace: Path, baseline: dict) -> list[dict]:
    """Hash the runtime/build/test/tool closure; packet-index documents are separately checked."""
    selected = []
    for relative in tracked_files(api, workspace, baseline["consumerInventory"]["roots"]):
        path = Path(relative)
        if relevant_source(relative):
            selected.append({"path": relative, "sha256": api.sha256(workspace / relative)})
    return selected



def root_source_dirty(api, workspace: Path, files: list[dict], repositories: list[dict]) -> bool:
    """Allow later metadata commits only when captured/current committed source stays identical."""
    root = next(item for item in repositories if item["path"] == ".")
    current = api.git(workspace, "rev-parse", "HEAD").stdout.strip()
    root_paths = {item["path"] for item in files if not item["path"].startswith("references/")}
    for revision in {root["revision"], current}:
        tree = api.git(workspace, "ls-tree", "-r", "--name-only", "-z", revision)
        committed_paths = {name for name in tree.stdout.split("\0") if name and not name.startswith("references/") and relevant_source(name)}
        if tree.returncode != 0 or committed_paths != root_paths:
            return True
        for item in files:
            if item["path"].startswith("references/"):
                continue
            committed = api.git(workspace, "show", f"{revision}:{item['path']}")
            digest = hashlib.sha256(committed.stdout.replace("\r\n", "\n").encode()).hexdigest()
            if committed.returncode != 0 or digest != item["sha256"]:
                return True
        for repository in repositories:
            if repository["path"] == ".":
                continue
            entry = api.git(workspace, "ls-tree", revision, "--", repository["path"])
            match = re.fullmatch(r"160000 commit ([0-9a-f]{40})\t[^\n]+\n?", entry.stdout)
            if entry.returncode != 0 or match is None or match[1] != repository["rootGitlink"]:
                return True
    return False


def validate_packet(api, workspace: Path, packet_path: Path, baseline_path: Path, baseline: dict, candidate: bool) -> None:
    packet = api.read_json(packet_path)
    api.exact_fields(packet, FIELDS, "Current packet")
    api.require(packet["schema"] == "hexalith.runtime-toolchain-evidence.v2", "Current packet schema drift")
    api.require(type(packet["technicalValidity"]) is bool, "Technical validity must be boolean")
    api.exact_fields(packet["closure"], {"committed", "sourceManifestSha256"}, "Current closure")
    api.require(type(packet["closure"]["committed"]) is bool, "Committed closure must be boolean")
    api.exact_fields(packet["testCounts"], {"qualification", "support"}, "Current packet test counts")
    for counts in packet["testCounts"].values():
        api.exact_fields(counts, {"selectors", "total", "passed", "failed", "skipped"}, "Current packet count")
        api.require(all(type(value) is int and value >= 0 for value in counts.values()), "Packet test counts must be nonnegative integers")
    api.require(packet["status"] == ("pending" if candidate else "accepted"), "Current packet acceptance is pending")
    api.require_utc_timestamp(packet["capturedUtc"], "Capture timestamp")
    api.require(packet["capturedUtc"] >= baseline["qualification"]["approvedAtUtc"], "Capture predates run authorization")
    api.require(baseline_path.resolve() == (workspace / CANONICAL_ARTIFACTS["baseline-governance"]).resolve(), "Current baseline path is not canonical")
    api.require(packet["baseline"] == {"path": baseline_path.relative_to(workspace).as_posix(), "sha256": api.sha256(baseline_path)}, "Current baseline hash mismatch")
    api.require(packet["approval"] == api.baseline_approval(baseline), "Run approval mismatch")
    api.require(packet["observedVersions"] == baseline["tuple"], "Observed version tuple drift")
    api.require(packet["containment"] == api.EXPECTED_CONTAINMENT, "Current containment drift")
    api.require_rollback(packet["rollback"], "Current rollback")
    api.require(packet["rollback"] == baseline["rollback"], "Rollback differs from baseline")
    artifacts = {}
    for item in packet["artifacts"]:
        api.exact_fields(item, {"kind", "path", "sha256"}, "Current artifact")
        api.require(item["kind"] not in artifacts, "Duplicate current artifact kind")
        if item["kind"] in CANONICAL_ARTIFACTS:
            api.require(item["path"] == CANONICAL_ARTIFACTS[item["kind"]], "Policy/tool artifact path is not canonical: " + item["kind"])
        path = api.resolve_artifact(workspace, item["path"], "current artifact")
        api.require(item["sha256"] == api.sha256(path), f"Current artifact hash mismatch: {item['path']}")
        # Source and policy inputs contain identifier names, never captured secret values. Every
        # observation, command, limitation and closure result is scanned, including the packet.
        if item["kind"] not in {"baseline-governance", "evidence-schema", "evidence-validator", "current-validator", "mutation-tests", "current-mutation-tests", "central-catalog", "qualification-runner"}:
            api.require(not any(pattern.search(path.read_text()) for pattern in api.FORBIDDEN), f"Current secret/private-path sentinel: {item['path']}")
        artifacts[item["kind"]] = path
    api.require(set(artifacts) == ARTIFACT_KINDS, "Current artifact set is incomplete")
    api.require(not any(pattern.search(packet_path.read_text()) for pattern in api.FORBIDDEN), "Packet sentinel matched")
    state = api.read_json(artifacts["source-state"])
    api.exact_fields(state, {"schema", "files", "repositories"}, "Current source state")
    api.require(state["schema"] == "hexalith.runtime-toolchain-source-state.v2", "Source-state schema drift")
    api.require(state["files"] == source_manifest(api, workspace, baseline), "Full source closure drift")
    api.require(state["repositories"] == packet["repositories"], "Repository crosswalk drift")
    api.require([item["path"] for item in packet["repositories"]] == baseline["consumerInventory"]["roots"], "Repository bindings omit a root consumer")
    for repository in packet["repositories"]:
        api.exact_fields(repository, {"name", "path", "revision", "rootGitlink", "dirty"}, "Current repository binding")
        current = api.git(workspace / repository["path"], "rev-parse", "HEAD").stdout.strip()
        api.require(re.fullmatch("[0-9a-f]{40}", current) is not None and re.fullmatch("[0-9a-f]{40}", repository["revision"]) is not None, "Checkout revision is malformed")
        if repository["path"] == ".":
            api.require(api.git(workspace, "merge-base", "--is-ancestor", repository["revision"], current).returncode == 0, "Captured root revision is not an ancestor of current HEAD")
        else:
            api.require(repository["revision"] == current, "Checkout revision drift")
        if repository["path"] != ".":
            entry = api.git(workspace, "ls-files", "--stage", "--", repository["path"]).stdout
            match = re.fullmatch(r"160000 ([0-9a-f]{40}) 0\t[^\n]+\n?", entry)
            api.require(match is not None and repository["rootGitlink"] == match[1], "Root gitlink observation drift")
        else:
            api.require(repository["rootGitlink"] is None, "Root repository has no enclosing gitlink")
        actual_dirty = root_source_dirty(api, workspace, state["files"], packet["repositories"]) if repository["path"] == "." else bool(api.git(workspace / repository["path"], "status", "--porcelain", "--untracked-files=normal").stdout.strip())
        api.require(type(repository["dirty"]) is bool and repository["dirty"] == actual_dirty, "Dirty source disposition contradicts Git")
    audit = api.read_json(artifacts["consumer-audit"])
    api.require(audit == {"schema": "hexalith.runtime-toolchain-consumer-audit.v2", "consumers": inventory(api, workspace, baseline), "exclusions": baseline["consumerInventory"]["unqualifiedExclusions"]}, "Current consumer audit drift")
    versions = api.read_json(artifacts["observed-versions"])
    api.exact_fields(versions, {"schema", "observedUtc", "tuple", "toolsLogSha256"}, "Current observed versions")
    api.require(versions["schema"] == "hexalith.runtime-toolchain-observed-versions.v2", "Observed-versions schema drift")
    api.require_utc_timestamp(versions["observedUtc"], "Tool observation timestamp")
    api.require(baseline["qualification"]["approvedAtUtc"] <= versions["observedUtc"] <= packet["capturedUtc"], "Tool observations predate approval or follow capture")
    api.require(versions.get("tuple") == packet["observedVersions"], "Version artifact contradicts observed tuple")
    commands = api.read_json(artifacts["command-record"])["commands"]
    purposes = [item["purpose"] for item in commands]
    api.require(len(purposes) == len(set(purposes)), "Command purposes must be unique")
    for command in commands:
        api.exact_fields(command, {"purpose", "command", "exitCode", "outcome", "logPath", "logSha256"}, "Current command")
        api.require(type(command["exitCode"]) is int, "Command exit status must be an integer")
        log = api.resolve_artifact(workspace, command["logPath"], "command log")
        api.require(command["logSha256"] == api.sha256(log), "Command log hash mismatch")
        api.require(not any(pattern.search(log.read_text()) for pattern in api.FORBIDDEN), "Command log sentinel matched")
    required = {"install isolated tools", "observe exact tool versions", "fresh EventStore qualifier build", "fresh EventStore support build", "real PostgreSQL two-sidecar stop/restart qualifier", "21 exact deterministic support selectors", "strict EventStore capture validation", "G-6 mutation controls", "root workflow pin contract", "fixture override tests", "isolated container cleanup controls", "McpCli transitive Dapr consumer"}
    api.require(required <= set(purposes), "Required qualification command is missing")
    critical = {item["purpose"]: item for item in commands if item["purpose"] in required}
    support_selectors = api.read_json(artifacts["eventstore-support-results"]).get("selectors")
    api.require(isinstance(support_selectors, list) and all(isinstance(selector, str) for selector in support_selectors), "Support selectors must be retained")
    capture_path = artifacts["eventstore-observations"].parent.relative_to(workspace).as_posix()
    expected = expected_commands(api, baseline, support_selectors, capture_path)
    for item in api.read_json(artifacts["apphost-outcomes"])["projects"]:
        if item["path"] in baseline["consumerInventory"]["fileBasedAppHosts"]:
            expected[item["purpose"]] = ["dotnet", "build", item["path"], "--configuration", "Debug", "--no-incremental", "-v:minimal", "-p:BaseIntermediateOutputPath=[scratch]/platform-obj/"]
        else:
            expected[item["purpose"]] = ["dotnet", "build", item["path"], *BUILD_ARGUMENTS]
    for command in commands:
        if command["purpose"] in expected and command["exitCode"] == 0:
            try:
                arguments = shlex.split(command["command"])
            except ValueError as error:
                raise api.ValidationError("Malformed required command") from error
            arguments = [argument.removeprefix("[workspace]/") for argument in arguments]
            api.require(arguments == expected[command["purpose"]], "Required command arguments drift: " + command["purpose"])
    mcp_project = "references/Hexalith.McpCli/src/Hexalith.McpCli/Hexalith.McpCli.csproj"
    mcp_command = critical["McpCli transitive Dapr consumer"]["command"]
    api.require("dotnet build " in mcp_command and mcp_project in mcp_command and "--configuration Debug" in mcp_command and "-p:UseHexalithProjectReferences=true" in mcp_command, "Actual McpCli Debug/source consumer build is missing")
    tools_command = critical["observe exact tool versions"]
    api.require(versions["toolsLogSha256"] == tools_command["logSha256"], "Observed tool-log crosswalk drift")
    tools_text = api.resolve_artifact(workspace, tools_command["logPath"], "tool observations").read_text()
    exact_observations = [("dotnetSdk", r"(?m)^{}\s*$"), ("aspireCli", r"(?m)^{}(?:\+[0-9a-zA-Z.-]+)?\s*$"),
                          ("daprCli", r"(?m)^CLI version:\s*{}\s*$"), ("daprRuntime", r"(?m)^Runtime version:\s*{}\s*$")]
    for field, pattern in exact_observations:
        api.require(re.search(pattern.format(re.escape(baseline["tuple"][field])), tools_text) is not None, f"Exact observed tool version is ungrounded: {field}")
    outcomes = api.read_json(artifacts["apphost-outcomes"])["projects"]
    expected_hosts = [item["path"] for item in baseline["pinAudit"]["appHostProjects"]] + baseline["consumerInventory"]["fileBasedAppHosts"]
    api.require([item["path"] for item in outcomes] == expected_hosts, "AppHost outcome coverage drift")
    for item in outcomes:
        api.require(item["configuration"] == "Debug" and item["dependencyMode"] == ("packages" if item["path"] in baseline["consumerInventory"]["fileBasedAppHosts"] else "source"), "AppHost local mode drift")
        api.require(item["purpose"] in purposes and item["exitCode"] == next(command["exitCode"] for command in commands if command["purpose"] == item["purpose"]), "AppHost outcome contradicts command record")
    resolved = api.read_json(artifacts["resolved-packages"])["projects"]
    api.require({item["path"] for item in resolved} >= {*expected_hosts, mcp_project}, "Resolved packages omit an AppHost or McpCli consumer")
    for item in resolved:
        api.require(item["packages"], "Resolved project packages are missing")
        minimum = required_packages(api, workspace, baseline, item["path"])
        api.require(minimum <= {package["id"] for package in item["packages"]}, "Required resolved dependencies are missing: " + item["path"])
        for package in item["packages"]:
            field = controlled_field(package["id"])
            api.require(type(package.get("qualified")) is bool, "Resolved package qualification must be boolean")
            if not package["qualified"]:
                exclusions = baseline["consumerInventory"]["unqualifiedExclusions"]
                api.require(any(exclusion["package"] == package["id"] and exclusion["version"] == package["version"]
                                and (exclusion["path"] == "references/Hexalith.Builds/Props/Directory.Packages.props"
                                     or item["path"].startswith(str(Path(exclusion["path"]).parent) + "/"))
                                for exclusion in exclusions), "Resolved package has an unauthorized exclusion")
                continue
            if field is None:
                continue
            expected = baseline["qualification"][field] if field == "eventStorePackageVersion" else baseline["tuple"][field]
            api.require(package["version"] == expected, f"Resolved package drift: {item['path']}::{package['id']}")
    cleanup = api.read_json(artifacts["cleanup"])
    api.exact_fields(cleanup, {"schema", "before", "after", "ownedContainers", "removedContainerIds", "ownedProcessesStopped", "scratchRemoved", "fixtureScratchRemoved", "ownedProcessGroups", "daprNamespace", "sidecarNamespaceObservations"}, "Cleanup")
    api.require(cleanup["before"] == cleanup["after"], "Shared resources changed during qualification")
    owned = cleanup["ownedContainers"]
    images = {"placement": "daprio/dapr:1.18.2", "scheduler": "daprio/dapr:1.18.2", "redis": "redis:7.4", "postgresql": POSTGRES_IMAGE}
    api.require(isinstance(owned, list) and len(owned) == 4 and {item.get("role") for item in owned} == set(images), "Owned resource roles are incomplete")
    for item in owned:
        api.exact_fields(item, {"name", "role", "id", "image"}, "Owned container")
        api.require(isinstance(item["id"], str) and re.fullmatch(r"[0-9a-f]{64}", item["id"]) is not None and item["image"] == images[item["role"]], "Owned container identity/image is invalid")
    ids = [item["id"] for item in owned]
    api.require(len(set(ids)) == 4 and len(cleanup["removedContainerIds"]) == 4 and set(cleanup["removedContainerIds"]) == set(ids), "Owned container cleanup is incomplete or duplicated")
    api.require(cleanup["ownedProcessesStopped"] is True and cleanup["scratchRemoved"] is True and cleanup["fixtureScratchRemoved"] is True, "Owned process/scratch cleanup failed")
    namespace = cleanup["daprNamespace"]
    api.require(isinstance(namespace, str) and re.fullmatch(r"g6-oq8-[a-z0-9][a-z0-9-]{0,48}", namespace) is not None, "Private Dapr namespace is missing")
    launches = cleanup["sidecarNamespaceObservations"]
    api.require(isinstance(launches, list) and len(launches) >= 4 and {item["appId"] for item in launches} == {"sample", "eventstore"}
                and {item["node"] for item in launches} == {"sample", "eventstore-1", "eventstore-2"}
                and all(item["daprNamespace"] == namespace and type(item["processId"]) is int and item["processId"] > 0 for item in launches), "Owned sidecar namespace isolation is incomplete")
    for item in launches:
        api.exact_fields(item, {"node", "appId", "processId", "daprNamespace", "nameResolver", "discoveryRegistryIdentitySha256", "discoveryConfigurationSha256", "privateDiscoveryRegistryObserved"}, "Sidecar discovery observation")
        api.require(item["nameResolver"] == "sqlite" and item["privateDiscoveryRegistryObserved"] is True,
                    "Private SQLite discovery was not observed on every launch")
        for field in ("discoveryRegistryIdentitySha256", "discoveryConfigurationSha256"):
            api.require(isinstance(item[field], str) and re.fullmatch(r"[0-9a-f]{64}", item[field]) is not None,
                        "Private discovery identity/configuration binding is missing")
    api.require(len({item["discoveryRegistryIdentitySha256"] for item in launches}) == 1
                and len({item["discoveryConfigurationSha256"] for item in launches}) == 1
                and len({item["processId"] for item in launches}) == len(launches)
                and any(sum(item["node"] == name for item in launches) >= 2 for name in ("eventstore-1", "eventstore-2")),
                "Restarted sidecars must share the private discovery registry/configuration")
    attempt_document = api.read_json(artifacts["attempts"])
    attempts = attempt_document["attempts"]
    errors = attempt_document.get("errors")
    api.require(isinstance(errors, list) and all(isinstance(error, str) for error in errors), "Runner errors must be recorded")
    api.require(attempts and all(type(item["exitCode"]) is int for item in attempts), "Actual attempt outcomes are missing")
    api.require(attempts[-1]["exitCode"] == critical["real PostgreSQL two-sidecar stop/restart qualifier"]["exitCode"], "Attempt outcome contradicts qualifier command")
    limitations = api.read_json(artifacts["limitations"])
    api.require(isinstance(limitations.get("items"), list) and limitations["items"], "Qualification limitations are required")
    api.require(limitations.get("publishedEventStoreArchivesQualified") is False, "Checkout qualification cannot qualify published archives")
    all_pass = not errors and all(item["exitCode"] == 0 for item in commands) and all(item["exitCode"] == 0 for item in outcomes)
    api.require(packet["technicalValidity"] == all_pass, "Technical validity contradicts actual command outcomes")
    if all_pass:
        qualification = api.read_json(artifacts["eventstore-qualification-results"])
        support = api.read_json(artifacts["eventstore-support-results"])
        q = api.result_summary(qualification["summary"], "Current qualifier")
        s = api.result_summary(support["summary"], "Current support")
        api.require(q == {"tests": 1, "passed": 1, "failed": 0, "skipped": 0} and s == {"tests": 33, "passed": 33, "failed": 0, "skipped": 0}, "Current qualification/support did not pass without skips")
        api.require(len(support["selectors"]) == len(set(support["selectors"])) == 21, "Current support selectors drift")
        api.require(qualification["test"]["name"] == api.QUALIFICATION_IDENTITY, "Qualifier identity drift")
        spec = importlib.util.spec_from_file_location("g6_oq8_capture_validator", workspace / "references/Hexalith.EventStore/tools/validate-oq8-platform-evidence.py")
        api.require(spec is not None and spec.loader is not None, "OQ8 capture validator is missing")
        oq8 = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(oq8)
        try:
            oq8.validate_observations(artifacts["eventstore-observations"], baseline["tuple"]["daprRuntime"], oq8.POSTGRES_IMAGE, expected_configuration="Debug")
            oq8.validate_focused_document(qualification, oq8.FOCUSED_CURRENT_COMMAND.replace("/Release/", "/Debug/"))
            oq8.validate_support_document(support, oq8.SUPPORT_CURRENT_COMMAND.replace("/Release/", "/Debug/"))
        except (oq8.EvidenceError, AttributeError) as error:
            raise api.ValidationError(f"Current OQ8 semantic proof failed: {error}") from error
        observation = api.read_json(artifacts["eventstore-observations"])
        api.require(observation["capturedOn"] == packet["capturedUtc"][:10] and observation["capturedOn"] >= baseline["qualification"]["approvedAtUtc"][:10], "OQ8 observation is stale for this authorized run")
        api.require(observation["observations"]["authority_change"]["deterministicSupportOracles"] == support["selectors"], "Support oracle crosswalk drift")
        receipt = api.read_json(artifacts["eventstore-capture-validation"])
        api.require(receipt == {"schemaVersion": 1, "validation": "passed", "observationsSha256": api.sha256(artifacts["eventstore-observations"]), "testResultsSha256": api.sha256(artifacts["eventstore-qualification-results"]), "deterministicSupportSha256": api.sha256(artifacts["eventstore-support-results"])}, "Capture receipt drift")
    api.require(all_pass, "Current technical qualification failed; retained failures cannot pass a G-6 gate")
    api.require(packet["testCounts"] == {"qualification": {"selectors": 1, "total": 1, "passed": 1, "failed": 0, "skipped": 0}, "support": {"selectors": 21, "total": 33, "passed": 33, "failed": 0, "skipped": 0}}, "Current packet counts mismatch")
    closed = all(not item["dirty"] and (item["path"] == "." or item["revision"] == item["rootGitlink"]) for item in packet["repositories"])
    api.require(packet["closure"] == {"committed": closed, "sourceManifestSha256": api.manifest_digest(state["files"])}, "Committed closure contradicts source")
    if candidate:
        api.require(packet["acceptance"] is None and packet["usableAsPrerequisite"] is False, "Pending candidate may not claim acceptance or usability")
    else:
        api.require(all_pass and closed, "Acceptance requires passing qualification and committed exact-gitlink closure")
        acceptance = api.exact_fields(packet["acceptance"], {"approvedBy", "approvedAtUtc", "decision", "reviewedPacketSha256"}, "Named acceptance")
        api.require(acceptance["approvedBy"] in api.G6_APPROVERS and acceptance["decision"] == "accept", "Named packet decision is unapproved")
        api.require_utc_timestamp(acceptance["approvedAtUtc"], "Packet acceptance")
        reviewed = copy.deepcopy(packet)
        reviewed.update(status="pending", acceptance=None, usableAsPrerequisite=False)
        digest = hashlib.sha256((json.dumps(reviewed, ensure_ascii=False, indent=2) + "\n").encode()).hexdigest()
        api.require(acceptance["reviewedPacketSha256"] == digest and acceptance["approvedAtUtc"] >= packet["capturedUtc"], "Named decision does not bind this reviewed packet")
        api.require(packet["usableAsPrerequisite"] is True, "Accepted packet usability contradicts acceptance")
