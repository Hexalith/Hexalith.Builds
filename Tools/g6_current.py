#!/usr/bin/env python3
"""Audit the current G-6 tuple and validate compact, current-source evidence.

This v3 contract does not reinterpret historical v1/v2 packets. Audit issues
are reported as data so a failed or unavailable run can retain its evidence.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import re
import subprocess
import tempfile
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


_catalog_spec = importlib.util.spec_from_file_location("hexalith_evaluated_catalog", Path(__file__).with_name("evaluated_catalog.py"))
assert _catalog_spec is not None and _catalog_spec.loader is not None
_catalog_module = importlib.util.module_from_spec(_catalog_spec)
_catalog_spec.loader.exec_module(_catalog_module)
from typing import Any


TUPLE_FIELDS = (
    "dotnetSdk", "aspireSdk", "aspireCli", "communityToolkitAspireDapr",
    "daprCli", "daprRuntime", "daprDotnetPackages", "fluentUi", "nSubstitute", "fluxor",
)
PACKAGE_FIELDS = {
    "CommunityToolkit.Aspire.Hosting.Dapr": "communityToolkitAspireDapr",
    "Dapr.Client": "daprDotnetPackages",
    "Microsoft.FluentUI.AspNetCore.Components": "fluentUi",
    "NSubstitute": "nSubstitute",
    "Fluxor": "fluxor",
}
REQUIRED_PURPOSES = {
    "fresh EventStore qualifier build", "fresh EventStore support build",
    "real PostgreSQL two-sidecar stop/restart qualifier",
    "21 exact deterministic support selectors",
}
LOG_PROOFS = {
    "fresh EventStore qualifier build": ("Build succeeded.", "0 Error(s)"),
    "fresh EventStore support build": ("Build succeeded.", "0 Error(s)"),
    "real PostgreSQL two-sidecar stop/restart qualifier": ("Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0",),
    "21 exact deterministic support selectors": ("Total: 33, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0",),
}
QUALIFIER_TEST = ("Hexalith.EventStore.Server.LiveSidecar.Tests.Actors.IdempotencyAdmissionOq8PostgresqlTests."
                  "ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants")
CAPTURE_FILES = {"observations.json", "test-results.json", "deterministic-support.json"}
SUPPORT_SELECTORS = 21
SUPPORT_CASES = 33
TOOLCHAIN_BASELINE = Path(__file__).with_name("runtime-toolchain-baseline-2026-10-01.json")
SHA = re.compile(r"[0-9a-f]{64}")
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?")
UTC = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z")


class G6Error(Exception):
    """An incomplete or contradictory G-6 audit or result."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise G6Error(message)


def canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def digest(value: Any) -> str:
    return hashlib.sha256(canonical(value)).hexdigest()


def file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pairs_unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(path: Path) -> dict[str, Any]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=pairs_unique)
    except (OSError, ValueError) as error:
        raise G6Error(f"cannot read {path}: {error}") from error
    require(isinstance(document, dict), f"{path} must contain an object")
    return document


def git(path: Path, *args: str) -> str:
    result = subprocess.run(["git", "-C", str(path), *args], capture_output=True, text=True, check=False)
    require(result.returncode == 0, f"git {' '.join(args)} failed in {path}: {result.stderr.strip()}")
    return result.stdout.strip()


def workspace_path(workspace: Path, relative: str) -> Path:
    require(relative and not Path(relative).is_absolute() and ".." not in Path(relative).parts,
            f"invalid workspace path: {relative}")
    path = (workspace / relative).resolve()
    require(path.is_relative_to(workspace.resolve()), f"path escapes workspace: {relative}")
    return path


def cli_workspace_path(workspace: Path, value: Path, context: str) -> Path:
    """Confine explicit CLI file arguments, including symlinks, to their selected workspace."""
    candidate = (workspace / value).resolve()
    require(candidate != workspace and candidate.is_relative_to(workspace),
            f"{context} escapes the selected workspace: {value}")
    return candidate


def policy_document(path: Path) -> dict[str, Any]:
    policy = read_json(path)
    require(policy.get("schema") == "hexalith.g6-current-policy.v1", "unsupported G-6 policy schema")
    require(set(policy) == {"schema", "tuple", "approval", "exceptions", "resolvedProjects", "materialFiles", "materialPrefixes", "reuseEnabled"},
            "G-6 policy fields drifted")
    require(policy["reuseEnabled"] is False, "material-input reuse requires separate closure review")
    require(set(policy["tuple"]) == set(TUPLE_FIELDS), "controlled tuple fields drifted")
    require(all(isinstance(value, str) and VERSION.fullmatch(value) for value in policy["tuple"].values()),
            "controlled tuple contains an invalid version")
    approval = policy["approval"]
    require(set(approval) == {"decision", "approvedBy", "approvedAtUtc", "reference", "tupleSha256"},
            "approval fields drifted")
    require(approval["decision"] in {"approved", "pending"}, "invalid tuple decision")
    require(approval["tupleSha256"] == digest(policy["tuple"]), "approval is not bound to the policy tuple")
    if approval["decision"] == "approved":
        require(isinstance(approval["approvedBy"], str) and len(approval["approvedBy"].strip().split()) >= 2
                and isinstance(approval["reference"], str) and approval["reference"].strip(),
                "named tuple owner decision is missing")
        require(isinstance(approval["approvedAtUtc"], str) and UTC.fullmatch(approval["approvedAtUtc"]),
                "tuple approval time is missing")
        dt.datetime.fromisoformat(approval["approvedAtUtc"].replace("Z", "+00:00"))
    exceptions = policy["exceptions"]
    require(exceptions == {
        "daprSupportTable": "approved-explicit-exception",
        "communityToolkitAspireDapr": ("approved-prerelease-exception" if approval["decision"] == "approved"
                                        else "pending-prerelease-exception"),
        "Dapr.Workflow": "catalog-only-unselected", "sourceModeOnly": True,
    }, "G-6 support/preview/source-mode exceptions drifted")
    for field in ("resolvedProjects", "materialFiles", "materialPrefixes"):
        values = policy[field]
        require(isinstance(values, list) and values and len(values) == len(set(values))
                and all(isinstance(item, str) and item for item in values), f"{field} must be a unique nonempty list")
    return policy


def repository_paths(workspace: Path) -> list[str]:
    modules = workspace / ".gitmodules"
    require(modules.is_file(), "root .gitmodules is missing")
    return [line.split("=", 1)[1].strip() for line in modules.read_text().splitlines()
            if line.strip().startswith("path =")]


def source_state(workspace: Path) -> dict[str, Any]:
    modules = repository_paths(workspace)
    links = []
    for relative in modules:
        path = workspace_path(workspace, relative)
        require((path / ".git").exists(), f"root-declared submodule is unavailable: {relative}")
        links.append({"path": relative, "sha": git(path, "rev-parse", "HEAD")})
    return {"rootSha": git(workspace, "rev-parse", "HEAD"), "gitlinks": links}


def tracked_material(workspace: Path, policy: dict[str, Any]) -> dict[str, Any]:
    names = set()
    for relative in [".", *repository_paths(workspace)]:
        root = workspace if relative == "." else workspace_path(workspace, relative)
        output = subprocess.run(["git", "-C", str(root), "ls-files", "--cached", "--others", "--exclude-standard", "-z"], capture_output=True, check=False)
        require(output.returncode == 0, f"cannot inventory material files in {relative}")
        for name in output.stdout.decode("utf-8").split("\0"):
            if not name or (relative == "." and name.startswith("references/")):
                continue
            path = name if relative == "." else f"{relative}/{name}"
            if path in policy["materialFiles"] or any(path.startswith(prefix) for prefix in policy["materialPrefixes"]):
                if workspace_path(workspace, path).is_file():
                    names.add(path)
    missing = sorted(set(policy["materialFiles"]) - names)
    require(not missing, f"required material files are missing: {missing}")
    for prefix in policy["materialPrefixes"]:
        require(any(name.startswith(prefix) for name in names), f"material prefix has no tracked source: {prefix}")
    files = [{"path": name, "sha256": file_hash(workspace_path(workspace, name))} for name in sorted(names)]
    return {"fingerprint": digest(files), "fileCount": len(files)}


def evaluated_catalog(workspace: Path) -> tuple[dict[str, str], dict[str, str]]:
    try:
        return _catalog_module.evaluate_catalog(workspace / "references/Hexalith.Builds/Props/Directory.Packages.props")
    except (ValueError, OSError, ET.ParseError) as error:
        raise G6Error(str(error)) from error


def package_versions(workspace: Path) -> dict[str, str]:
    return evaluated_catalog(workspace)[1]


def catalog_properties(workspace: Path) -> dict[str, str]:
    return evaluated_catalog(workspace)[0]


def apphost_sdk_versions(document: ET.Element, properties: dict[str, str], project_path: Path | None = None) -> set[str]:
    """Evaluate only active controlled SDK declarations in their consuming project context."""
    try:
        if project_path is not None:
            return _catalog_module.apphost_sdk_versions(project_path, properties)
        with tempfile.TemporaryDirectory(prefix="g6-sdk-document-") as temporary:
            path = Path(temporary) / "consumer.csproj"
            path.write_text(ET.tostring(document, encoding="unicode"))
            return _catalog_module.apphost_sdk_versions(path, properties)
    except (ValueError, OSError, ET.ParseError) as error:
        raise G6Error(str(error)) from error


def one_match(pattern: str, value: str, label: str) -> str:
    found = set(re.findall(pattern, value, re.MULTILINE))
    require(len(found) == 1, f"{label} must resolve to one effective version; found {sorted(found)}")
    return next(iter(found))


def effective_tuple(workspace: Path) -> dict[str, str]:
    properties, catalog = evaluated_catalog(workspace)
    workflow = (workspace / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    apphost = (workspace / "src/Hexalith.Projects.AppHost/Hexalith.Projects.AppHost.csproj").read_text(encoding="utf-8")
    dapr_packages = {catalog.get("Dapr.Client"), catalog.get("Dapr.Workflow")}
    require(len(dapr_packages) == 1 and None not in dapr_packages, "Dapr .NET package versions disagree")
    return {
        "dotnetSdk": read_json(workspace / "global.json")["sdk"]["version"],
        "aspireSdk": one_match(r"(.+)", "\n".join(apphost_sdk_versions(ET.fromstring(apphost), properties, workspace / "src/Hexalith.Projects.AppHost/Hexalith.Projects.AppHost.csproj")), "Aspire SDK"),
        "aspireCli": one_match(r"dotnet tool install --global Aspire\.Cli --version ([^\s]+)", workflow, "Aspire CLI"),
        "communityToolkitAspireDapr": catalog["CommunityToolkit.Aspire.Hosting.Dapr"],
        "daprCli": one_match(r"dapr-version:\s*'([^']+)'", workflow, "Dapr CLI"),
        "daprRuntime": one_match(r"dapr-runtime-version:\s*'([^']+)'", workflow, "Dapr runtime"),
        "daprDotnetPackages": next(iter(dapr_packages)),
        "fluentUi": catalog["Microsoft.FluentUI.AspNetCore.Components"],
        "nSubstitute": catalog["NSubstitute"],
        "fluxor": catalog["Fluxor"],
    }


def controlled_field(package: str) -> str | None:
    if package.startswith("Aspire.Hosting"):
        return "aspireSdk"
    if package.startswith("Dapr."):
        return "daprDotnetPackages"
    if package.startswith("Microsoft.FluentUI.AspNetCore.Components"):
        return "fluentUi"
    if package.startswith("Fluxor"):
        return "fluxor"
    return PACKAGE_FIELDS.get(package)


def hosting_preview_exclusions(aspire_sdk: str) -> set[tuple[str, str]]:
    """Retain exact dependency exceptions without qualifying their runtime behavior."""
    versions = {
        "13.6.0": "13.6.0-preview.1.26479.8",
        "13.6.1": "13.6.1-preview.1.26506.6",
        "13.6.4": "13.6.4-preview.1.26523.11",
    }
    version = versions.get(aspire_sdk)
    return {(package, version) for package in ("Aspire.Hosting.Keycloak", "Aspire.Hosting.Kubernetes")} if version else set()


def resolved_graph(workspace: Path, policy: dict[str, Any], tuple_values: dict[str, str]) -> tuple[list[dict], list[str]]:
    graph, issues = [], []
    exclusions = hosting_preview_exclusions(tuple_values["aspireSdk"])
    for relative in policy["resolvedProjects"]:
        project = workspace_path(workspace, relative)
        assets = project.parent / "obj/project.assets.json"
        if not assets.is_file():
            issues.append(f"missing restored graph: {relative}")
            graph.append({"path": relative, "packages": []})
            continue
        document = read_json(assets)
        packages = []
        for identity, metadata in document.get("libraries", {}).items():
            if metadata.get("type") != "package" or "/" not in identity:
                continue
            package, version = identity.rsplit("/", 1)
            field = controlled_field(package)
            if field is None:
                continue
            packages.append({"id": package, "version": version})
            if (package, version) in exclusions:
                continue
            if package == "Dapr.Workflow":
                issues.append(f"unselected Dapr.Workflow activated: {relative}")
            elif version != tuple_values[field]:
                issues.append(f"resolved package differs from effective {field}: {relative}::{package} {version}")
        packages.sort(key=lambda item: (item["id"], item["version"]))
        graph.append({"path": relative, "packages": packages})
        if not packages:
            issues.append(f"empty controlled resolved graph: {relative}")
    return graph, issues


def direct_pin_issues(workspace: Path, policy: dict[str, Any], tuple_values: dict[str, str]) -> list[str]:
    issues = []
    properties, catalog = evaluated_catalog(workspace)
    for package, version in catalog.items():
        field = controlled_field(package)
        if field and package != "Dapr.Workflow" and (package, version) not in hosting_preview_exclusions(tuple_values["aspireSdk"]) and version != tuple_values[field]:
            issues.append(f"central controlled package differs from effective {field}: {package} {version}")
    for relative in policy["resolvedProjects"]:
        project = workspace_path(workspace, relative)
        document = ET.parse(project).getroot()
        for sdk_version in apphost_sdk_versions(document, properties, project):
            if sdk_version != tuple_values["aspireSdk"]:
                issues.append(f"AppHost SDK differs from effective tuple: {relative} {sdk_version}")
        for item in document.iter("PackageReference"):
            package = item.get("Include") or item.get("Update")
            field = controlled_field(package or "")
            if field and (item.get("VersionOverride") or item.find("VersionOverride") is not None):
                issues.append(f"controlled package VersionOverride is forbidden: {relative}::{package}")
            direct = item.get("Version") or item.findtext("Version")
            if field and direct and "$" not in direct and direct != tuple_values[field]:
                issues.append(f"direct controlled package differs from effective tuple: {relative}::{package} {direct}")
    host = (workspace / "references/Hexalith.Platform/apphost.cs").read_text(encoding="utf-8")
    for package, version in re.findall(r"(?m)^#:(?:sdk|package)\s+(\S+)@(\S+)\s*$", host):
        field = "aspireSdk" if package == "Aspire.AppHost.Sdk" else controlled_field(package)
        if field and (package, version) not in hosting_preview_exclusions(tuple_values["aspireSdk"]) and version != tuple_values[field]:
            issues.append(f"Platform file host pin differs from effective tuple: {package} {version}")
    return issues


def builds_execution_sha(workspace: Path, scope: str) -> str:
    require(scope in {"ci", "release"}, "unknown Builds execution scope")
    workflow = (workspace / f".github/workflows/{scope}.yml").read_text(encoding="utf-8")
    active = "\n".join(line for line in workflow.splitlines() if not line.lstrip().startswith("#"))
    pattern = (r"uses:\s*Hexalith/Hexalith\.Builds/[^\s@]+@([0-9a-f]{40})" if scope == "ci" else
               r"repository:\s*Hexalith/Hexalith\.Builds\s*\n\s*ref:\s*([0-9a-f]{40})")
    pins = set(re.findall(pattern, active))
    require(len(pins) == 1, f"{scope} must pin one exact Builds execution SHA; found {sorted(pins)}")
    return next(iter(pins))


def audit(workspace: Path, policy_path: Path) -> dict[str, Any]:
    policy = policy_document(policy_path)
    effective = effective_tuple(workspace)
    graph, issues = resolved_graph(workspace, policy, effective)
    issues.extend(direct_pin_issues(workspace, policy, effective))
    source = source_state(workspace)
    for link in source["gitlinks"]:
        entry = git(workspace, "ls-tree", "HEAD", "--", link["path"]).split()
        if len(entry) < 3 or entry[2] != link["sha"]:
            issues.append(f"current checkout differs from root gitlink: {link['path']}")
    for field in TUPLE_FIELDS:
        if effective[field] != policy["tuple"][field]:
            issues.append(f"controlled tuple changed: {field} {effective[field]} != policy {policy['tuple'][field]}")
    if policy["approval"]["decision"] != "approved":
        issues.append("current tuple and Toolkit prerelease exception await named owner approval")
    return {
        "schema": "hexalith.g6-current-audit.v1",
        "policySha256": file_hash(policy_path),
        "source": source,
        "materialInputs": tracked_material(workspace, policy),
        "effectiveTuple": effective,
        "resolvedPackages": graph,
        "tupleApproved": not issues and policy["approval"]["decision"] == "approved",
        "issues": issues,
    }


def required_apphost_builds() -> dict[str, str]:
    """Map each selected AppHost build purpose to the project file its command must compile."""
    baseline = read_json(TOOLCHAIN_BASELINE)
    projects = [item["path"] for item in baseline["pinAudit"]["appHostProjects"]]
    projects += baseline["consumerInventory"]["fileBasedAppHosts"]
    required = {}
    for relative in projects:
        path = Path(relative)
        purpose = "AppHost build " + ("Hexalith.Platform" if path.suffix == ".cs" else path.parent.name)
        require(purpose not in required, f"selected AppHost purpose is ambiguous: {purpose}")
        required[purpose] = path.name
    return required


def retained_summary(document: dict[str, Any], label: str) -> dict[str, int]:
    summary = document.get("summary")
    require(isinstance(summary, dict) and set(summary) == {"tests", "passed", "failed", "skipped"}
            and all(type(value) is int and value >= 0 for value in summary.values()),
            f"retained {label} summary is malformed")
    return {"total": summary["tests"], "passed": summary["passed"],
            "failed": summary["failed"], "skipped": summary["skipped"]}


def validate_retained_tests(directory: Path, tests: dict[str, Any]) -> None:
    """Recheck qualifier 1/1 and support 33/33 from the retained test documents."""
    focused = read_json(directory / "test-results.json")
    require(retained_summary(focused, "qualifier") == {"total": 1, "passed": 1, "failed": 0, "skipped": 0},
            "retained qualifier result did not pass exactly 1 of 1")
    test = focused.get("test")
    require(isinstance(test, dict) and test.get("name") == QUALIFIER_TEST and test.get("status") == "passed",
            "retained qualifier identity or status drifted")
    support = read_json(directory / "deterministic-support.json")
    require(retained_summary(support, "support") == {"total": SUPPORT_CASES, "passed": SUPPORT_CASES, "failed": 0, "skipped": 0},
            f"deterministic support did not pass all {SUPPORT_CASES} cases")
    require("unrecognizedCases" not in support, "deterministic support retained unrecognized cases")
    selectors = support.get("selectors")
    methods = support.get("methods")
    require(isinstance(selectors, list) and len(selectors) == len(set(selectors)) == SUPPORT_SELECTORS
            and all(isinstance(item, str) and item.startswith("Hexalith.EventStore.Server.Tests.") for item in selectors),
            f"deterministic support does not bind {SUPPORT_SELECTORS} exact selectors")
    require(isinstance(methods, list) and all(isinstance(item, dict) for item in methods)
            and [item.get("identity") for item in methods] == selectors,
            "deterministic support methods do not match its selectors")
    require(all(type(item.get("observedCases")) is int and item["observedCases"] >= 1
                and item.get("passedCases") == item["observedCases"] for item in methods),
            "a deterministic support selector was not observed passing")
    require(sum(item["observedCases"] for item in methods) == SUPPORT_CASES,
            "deterministic support method cases do not total the passed summary")
    require(tests == {"qualifier": retained_summary(focused, "qualifier"), "support": retained_summary(support, "support")},
            "compact test counts contradict the retained test documents")


def validate_receipts(workspace: Path, evidence_path: Path, qualification: dict[str, Any]) -> None:
    """Recheck the retained proof, not merely the compact summary."""
    receipts = qualification["receipts"]
    require(set(receipts) == {"captureDirectory", "files", "logs", "cleanup"}, "receipt fields drifted")
    directory = workspace_path(workspace, receipts["captureDirectory"])
    run_directory = evidence_path.resolve().parent
    require(directory == run_directory / "capture", "OQ8 capture is not in this result's run directory")
    files = receipts["files"]
    require(isinstance(files, list) and {item.get("name") for item in files} == CAPTURE_FILES
            and len(files) == len(CAPTURE_FILES), "OQ8 capture receipt is incomplete")
    for item in files:
        require(set(item) == {"name", "sha256"} and SHA.fullmatch(item["sha256"]),
                "capture file receipt is malformed")
        path = directory / item["name"]
        require(path.is_file() and file_hash(path) == item["sha256"], f"capture file changed: {item['name']}")
    logs = receipts["logs"]
    require(isinstance(logs, list) and {item.get("purpose") for item in logs} >= REQUIRED_PURPOSES
            and len({item.get("purpose") for item in logs}) == len(logs), "critical command logs are incomplete")
    for item in logs:
        require(set(item) == {"purpose", "path", "sha256"} and SHA.fullmatch(item["sha256"]),
                "command log receipt is malformed")
        path = workspace_path(workspace, item["path"])
        require(path.is_relative_to(run_directory / "logs"),
                f"command log is not in this result's run directory: {item['purpose']}")
        require(path.is_file() and file_hash(path) == item["sha256"], f"command log changed: {item['purpose']}")
        if item["purpose"] in LOG_PROOFS:
            log_text = path.read_text(encoding="utf-8")
            require(all(marker in log_text for marker in LOG_PROOFS[item["purpose"]]),
                    f"critical command log lacks pass proof: {item['purpose']}")
    read_json(directory / "observations.json")
    validate_retained_tests(directory, qualification["tests"])
    cleanup_receipt = receipts["cleanup"]
    require(set(cleanup_receipt) == {"path", "sha256"} and SHA.fullmatch(cleanup_receipt["sha256"]),
            "cleanup receipt is malformed")
    cleanup_path = workspace_path(workspace, cleanup_receipt["path"])
    require(cleanup_path == run_directory / "cleanup.json" and cleanup_path.is_file()
            and file_hash(cleanup_path) == cleanup_receipt["sha256"],
            "cleanup receipt is missing, outside this run, or changed")
    cleanup = read_json(cleanup_path)
    require(cleanup.get("before") == cleanup.get("after") and cleanup.get("before") is not None,
            "shared resources changed during qualification")
    owned = cleanup.get("ownedContainers")
    removed = cleanup.get("removedContainerIds")
    require(isinstance(owned, list) and {item.get("role") for item in owned}
            == {"placement", "scheduler", "redis", "postgresql"},
            "isolated container roles are incomplete")
    ids = [item.get("id") for item in owned]
    require(len(ids) == len(set(ids)) == 4 and all(isinstance(item, str) and SHA.fullmatch(item) for item in ids)
            and isinstance(removed, list) and sorted(removed) == sorted(ids),
            "owned container IDs were not all removed")
    require(cleanup.get("ownedProcessesStopped") is True and cleanup.get("scratchRemoved") is True
            and cleanup.get("fixtureScratchRemoved") is True, "owned process or scratch cleanup failed")
    compact = qualification["cleanup"]
    require(compact == {"ownedProcessesStopped": True, "scratchRemoved": True,
                        "fixtureScratchRemoved": True, "sharedResourcesUnchanged": True,
                        "ownedContainersRemoved": True}, "compact cleanup contradicts retained receipt")


def validate(workspace: Path, policy_path: Path, evidence_path: Path, release: bool) -> None:
    policy = policy_document(policy_path)
    evidence = read_json(evidence_path)
    require(set(evidence) == {"schema", "audit", "qualification", "approval", "artifactSha256"},
            "compact evidence fields drifted")
    require(evidence["schema"] == "hexalith.g6-current-evidence.v1", "unsupported evidence schema")
    require(evidence["artifactSha256"] == digest({key: value for key, value in evidence.items() if key != "artifactSha256"}),
            "evidence artifact digest mismatch")
    current = audit(workspace, policy_path)
    captured = evidence["audit"]
    require(captured.get("schema") == current["schema"], "audit schema drifted")
    for field in ("policySha256", "materialInputs", "effectiveTuple", "resolvedPackages", "tupleApproved", "issues"):
        require(captured.get(field) == current[field], f"current audit {field} differs from evidence")
    require(current["tupleApproved"] is True, "current effective tuple or resolved graph is not approved")
    require(evidence["approval"] == policy["approval"] and policy["approval"]["decision"] == "approved",
            "named owner approval does not match this tuple")
    qualification = evidence["qualification"]
    require(set(qualification) == {"status", "runner", "fixture", "commands", "tests", "cleanup", "environment", "limitations", "receipts"},
            "qualification fields drifted")
    require(qualification["status"] == "qualified", "critical qualification is not qualified")
    require(isinstance(qualification["runner"], str) and qualification["runner"].strip(), "runner identity missing")
    require("IdempotencyAdmissionOq8PostgresqlTests.ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants" in qualification["fixture"],
            "two-sidecar PostgreSQL restart fixture identity missing")
    commands = qualification["commands"]
    require(isinstance(commands, list) and len(commands) == len({item.get("purpose") for item in commands}),
            "qualification commands missing or duplicated")
    purposes = {item.get("purpose") for item in commands}
    require(REQUIRED_PURPOSES <= purposes, "required live or deterministic command missing")
    for command in commands:
        require(set(command) == {"purpose", "command", "exitCode"} and command["exitCode"] == 0
                and isinstance(command["command"], str) and command["command"].strip(),
                f"failed or malformed command: {command.get('purpose')}")
    by_purpose = {command["purpose"]: command["command"] for command in commands}
    require("dotnet build" in by_purpose["fresh EventStore qualifier build"]
            and "LiveSidecar.Tests.csproj" in by_purpose["fresh EventStore qualifier build"],
            "qualifier build command identity drifted")
    require("dotnet build" in by_purpose["fresh EventStore support build"]
            and "Server.Tests.csproj" in by_purpose["fresh EventStore support build"],
            "support build command identity drifted")
    require("-method" in by_purpose["real PostgreSQL two-sidecar stop/restart qualifier"]
            and "ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants"
            in by_purpose["real PostgreSQL two-sidecar stop/restart qualifier"],
            "live qualifier command identity drifted")
    require("-method" in by_purpose["21 exact deterministic support selectors"]
            and "Server.Tests" in by_purpose["21 exact deterministic support selectors"],
            "deterministic support command identity drifted")
    for purpose, project in required_apphost_builds().items():
        require(purpose in by_purpose and "dotnet build" in by_purpose[purpose] and project in by_purpose[purpose],
                f"selected AppHost build command missing or drifted: {purpose}")
    tests = qualification["tests"]
    require(set(tests) == {"qualifier", "support"}, "critical test result set drifted")
    for label, expected in (("qualifier", 1), ("support", 33)):
        result = tests[label]
        require(set(result) == {"total", "passed", "failed", "skipped"} and result["total"] == expected
                and result["passed"] == expected and result["failed"] == result["skipped"] == 0,
                f"{label} failed, skipped, or unavailable")
    validate_receipts(workspace, evidence_path, qualification)
    cleanup = qualification["cleanup"]
    require(set(cleanup) == {"ownedProcessesStopped", "scratchRemoved", "fixtureScratchRemoved",
                             "sharedResourcesUnchanged", "ownedContainersRemoved"}
            and all(value is True for value in cleanup.values()), "isolated resource cleanup failed")
    environment = qualification["environment"]
    require(isinstance(environment, dict) and environment, "environment missing")
    scope = environment.get("executionScope")
    require(scope == ("release" if release else "ci"), "evidence execution scope does not match validation mode")
    require(environment.get("buildsExecutionSha") == builds_execution_sha(workspace, scope),
            "Builds execution SHA does not match current workflow pin")
    require(isinstance(qualification["limitations"], list), "limitations missing")
    for relative in [".", *repository_paths(workspace)]:
        root = workspace if relative == "." else workspace_path(workspace, relative)
        require(not git(root, "status", "--porcelain", "--untracked-files=normal"),
                f"qualified source checkout is dirty: {relative}")
    if release or not policy["reuseEnabled"]:
        require(captured.get("source") == current["source"], "release source SHA or gitlinks differ from live run")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="mode", required=True)
    for mode in ("audit", "validate"):
        command = sub.add_parser(mode)
        command.add_argument("--workspace", type=Path, default=Path("."))
        command.add_argument("--policy", type=Path, required=True)
        if mode == "audit":
            command.add_argument("--out", type=Path)
        else:
            command.add_argument("--evidence", type=Path, required=True)
            command.add_argument("--release", action="store_true")
    args = parser.parse_args(argv)
    try:
        workspace = args.workspace.resolve(strict=True)
        require(workspace.is_dir(), "the selected workspace must be a directory")
        policy = cli_workspace_path(workspace, args.policy, "policy path")
        if args.mode == "audit":
            target = cli_workspace_path(workspace, args.out, "audit output path") if args.out else None
            result = audit(workspace, policy)
            output = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
            if target is not None:
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open("w", encoding="utf-8") as stream:
                    stream.write(output)
            else:
                print(output, end="")
            return 0
        evidence = cli_workspace_path(workspace, args.evidence, "evidence path")
        validate(workspace, policy, evidence, args.release)
        print("G6-CURRENT-QUALIFIED: approved tuple, material inputs, critical proof, cleanup and source verified")
        return 0
    except (G6Error, KeyError, TypeError, ET.ParseError, OSError) as error:
        print(f"G6-CURRENT-NOT-VERIFIED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
