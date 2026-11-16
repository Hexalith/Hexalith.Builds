"""Fresh per-project test reports and scoped Cobertura gates for shared CI.

This module owns report paths, parsing and aggregation. Product scopes and
thresholds are data in the CiCd-owned configuration; no module script is run.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import xml.etree.ElementTree as ET

from ci_config import ConfigError, _read_json


MAX_REPORT_BYTES = 64 * 1024 * 1024


class ReportError(ConfigError):
    """Required test evidence is missing, malformed or below its product gate."""


def validate_coverage_sources(root: Path, policy: dict) -> None:
    """Declared coverage scopes must name the current product source tree."""
    for name, directory in (("lineScope", True), ("branchTargets", False)):
        for relative in policy.get(name, []):
            path = root / relative.rstrip("/")
            if (not path.resolve().is_relative_to(root.resolve())
                    or any(parent.is_symlink() for parent in (path, *path.parents))
                    or not (path.is_dir() if directory else path.is_file())):
                raise ReportError(f"coverage {name} source {'directory' if directory else 'file'} does not exist: {relative}")


def owned_directory(root: Path) -> Path:
    """Do not follow a module-supplied symlink when writing result files."""
    directory = root / "TestResults/shared-ci"
    for path in (root / "TestResults", directory):
        if path.is_symlink() or path.exists() and not path.is_dir():
            raise ReportError(f"test report directory must be a real directory, not a symlink/file: {path}")
        try:
            path.mkdir(exist_ok=True)
        except OSError as error:
            raise ReportError(f"cannot create test report directory {path}: {error}") from error
    return directory


def index_path(root: Path, stage: str) -> Path:
    if stage not in ("Unit", "Integration"):
        raise ReportError(f"unsupported report stage: {stage}")
    path = owned_directory(root) / (stage.lower() + ".json")
    if path.is_symlink() or path.exists() and not path.is_file():
        raise ReportError("test report index must be a regular file, not a symlink/directory")
    return path


def report_xml(path: Path) -> ET.Element:
    """Read only bounded regular XML; never expand DTD/entity declarations."""
    try:
        if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= MAX_REPORT_BYTES:
            raise ReportError(f"missing, empty or oversized required report: {path}")
        data = path.read_bytes()
    except OSError as error:
        raise ReportError(f"cannot read required report {path}: {error}") from error
    markup = data.replace(b"\0", b"").upper()
    if b"<!DOCTYPE" in markup or b"<!ENTITY" in markup:
        raise ReportError(f"DTD/entity declarations are forbidden in reports: {path}")
    try:
        document = ET.fromstring(data)
    except ET.ParseError as error:
        raise ReportError(f"malformed XML report {path}: {error}") from error
    for element in document.iter():
        element.tag = element.tag.rsplit("}", 1)[-1]
    return document


def validate_trx(path: Path, *, success: bool) -> None:
    document = report_xml(path)
    summaries = document.findall(".//ResultSummary")
    if document.tag != "TestRun" or len(summaries) != 1:
        raise ReportError(f"invalid TRX TestRun/ResultSummary: {path}")
    counters = summaries[0].findall("Counters")
    try:
        if len(counters) != 1:
            raise ValueError("expected one Counters element")
        values = {name: int(counters[0].attrib[name]) for name in ("total", "executed", "passed", "failed")}
        if (any(value < 0 for value in values.values()) or values["executed"] <= 0
                or values["executed"] > values["total"]
                or values["passed"] + values["failed"] > values["executed"]):
            raise ValueError("inconsistent or zero executed tests")
        results = document.findall("./Results/UnitTestResult")
        outcomes = [result.get("outcome") for result in results]
        not_executed = sum(outcome in ("NotExecuted", "NotRunnable", "Pending") for outcome in outcomes)
        if (not results or len(results) != values["total"]
                or len(results) - not_executed != values["executed"]
                or outcomes.count("Passed") != values["passed"]
                or outcomes.count("Failed") != values["failed"]
                or any(outcome not in ("Passed", "Failed", "Error", "Timeout", "Aborted", "Inconclusive",
                                       "NotExecuted", "NotRunnable", "Pending", "Disconnected", "Warning", "Completed")
                       for outcome in outcomes)):
            raise ValueError("incomplete results or counters disagree with outcomes")
        if success and (values["failed"] or summaries[0].get("outcome") not in ("Passed", "Completed")
                        or any(result.get("outcome") in ("Failed", "Error", "Timeout", "Aborted") for result in results)
                        or any(int(counters[0].get(name, "0")) for name in ("error", "timeout", "aborted"))):
            raise ValueError("failed results with a successful test exit")
    except (ValueError, KeyError) as error:
        raise ReportError(f"invalid or empty required TRX {path}: {error}") from error


def validate_cobertura(path: Path) -> None:
    document = report_xml(path)
    lines = document.findall(".//class/lines/line")
    if document.tag != "coverage" or not lines:
        raise ReportError(f"missing or empty Cobertura line data: {path}")
    try:
        for line in lines:
            if int(line.attrib["number"]) <= 0 or int(line.attrib["hits"]) < 0:
                raise ValueError("invalid line number or hits")
    except (ValueError, KeyError) as error:
        raise ReportError(f"invalid Cobertura line data {path}: {error}") from error


class ReportSession:
    """One stage's fresh result directories; an interrupted run never completes."""

    def __init__(self, root: Path, stage: str, projects: tuple[str, ...], policy: dict):
        self.root = root.resolve()
        self.stage = stage
        self.policy = policy
        validate_coverage_sources(root, policy.get("coverage", {}))
        self.index = index_path(root, stage)
        self.directory = Path(tempfile.mkdtemp(prefix=stage.lower() + "-", dir=owned_directory(root)))
        self.document = {"policy": policy, "projects": list(projects), "complete": False, "results": []}
        self.write()

    def write(self) -> None:
        self.index.write_text(json.dumps(self.document, sort_keys=True), encoding="utf-8")

    def directory_for(self, project: str, framework: str) -> Path:
        # The full path, not the basename, distinguishes same-named projects.
        identity = hashlib.sha256((project + "\0" + framework).encode()).hexdigest()
        directory = self.directory / identity
        directory.mkdir()
        return directory

    def options(self, project: str, directory: Path, *, runner: str, xunit: bool) -> list[str]:
        options = ["--results-directory", str(directory)]
        if self.policy.get("trx", False):
            if runner == "Microsoft.Testing.Platform":
                reporter = "report-xunit-trx" if xunit else "report-trx"
                options.extend(("--" + reporter, "--" + reporter + "-filename", "results.trx"))
            else:
                options.extend(("--logger", "trx;LogFileName=results.trx"))
        if project in self.policy.get("coverage", {}).get("projects", []):
            if runner == "Microsoft.Testing.Platform":
                options.extend(("--coverage", "--coverage-output-format", "cobertura",
                                "--coverage-output", "coverage.cobertura.xml"))
            else:
                options.extend(("--collect", "XPlat Code Coverage"))
        return options

    def record(self, project: str, framework: str, directory: Path, *, success: bool) -> None:
        paths, digests = [], {}
        for pattern, required in (("*.trx", self.policy.get("trx", False)),
                                  ("coverage.cobertura.xml", project in self.policy.get("coverage", {}).get("projects", []))):
            if not required:
                continue
            found = sorted(directory.rglob(pattern))
            if not found:
                raise ReportError(f"{project} ({framework or 'default'}): missing required {pattern} in {directory}")
            for path in found:
                if any(parent.is_symlink() for parent in (path, *path.parents)) or not path.resolve().is_relative_to(directory.resolve()):
                    raise ReportError(f"report escapes its fresh result directory: {path}")
                if pattern == "*.trx":
                    validate_trx(path, success=success)
                else:
                    validate_cobertura(path)
                relative = path.relative_to(self.root).as_posix()
                paths.append(relative)
                digests[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
        self.document["results"].append({"project": project, "framework": framework, "paths": paths, "sha256": digests})
        self.write()

    def finish(self) -> None:
        self.document["complete"] = True
        self.write()


def stage_manifest(root: Path, stage: str, policy: dict) -> dict:
    path = index_path(root, stage)
    document = _read_json(path)
    if (not isinstance(document, dict) or document.get("policy") != policy
            or document.get("complete") is not True or not isinstance(document.get("results"), list)
            or not isinstance(document.get("projects"), list)
            or any(not isinstance(project, str) for project in document["projects"])):
        raise ReportError(f"missing or incomplete {stage} report manifest: {path}")
    for result in document["results"]:
        if (not isinstance(result, dict) or set(result) != {"project", "framework", "paths", "sha256"}
                or result["project"] not in document["projects"] or not isinstance(result["framework"], str)
                or not isinstance(result["paths"], list) or not isinstance(result["sha256"], dict)
                or any(not isinstance(value, str) for value in result["paths"])
                or set(result["paths"]) != result["sha256"].keys()
                or any(not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None
                       for value in result["sha256"].values())):
            raise ReportError(f"malformed {stage} report manifest: {path}")
        required = []
        if policy.get("trx", False):
            required.append("trx")
        if result["project"] in policy.get("coverage", {}).get("projects", []):
            required.append("coverage")
        found = set()
        for relative in result["paths"]:
            report = root / relative
            if (not report.resolve().is_relative_to((root / "TestResults/shared-ci").resolve())
                    or any(parent.is_symlink() for parent in (report, *report.parents))):
                raise ReportError("test report escapes the shared result directory")
            if not report.is_file() or hashlib.sha256(report.read_bytes()).hexdigest() != result["sha256"][relative]:
                raise ReportError(f"test report changed after collection or is missing: {report}")
            if relative.endswith(".trx"):
                validate_trx(report, success=True)
                found.add("trx")
            elif relative.endswith("/coverage.cobertura.xml"):
                validate_cobertura(report)
                found.add("coverage")
            else:
                raise ReportError(f"unknown test report in {stage} manifest: {relative}")
        if set(required) - found:
            raise ReportError(f"required reports missing in {stage} manifest: {result['project']}")
    if set(document["projects"]) != {result["project"] for result in document["results"]}:
        raise ReportError(f"completed projects missing in {stage} report manifest")
    return document


def normalized_filename(root: Path, filename: str, coverage: dict) -> str:
    value = filename.replace("\\", "/")
    while value.startswith("./"):
        value = value[2:]
    if any(part in (".", "..") for part in value.split("/")) or "\x00" in value:
        raise ReportError("unsafe source filename in Cobertura report")
    prefix = root.resolve().as_posix() + "/"
    if value.startswith(prefix):
        value = value[len(prefix):]
    # Coverage producers may use source-link paths instead of the current root.
    for target in coverage.get("branchTargets", []):
        if value == target or value.endswith("/" + target):
            return target
    for scope in coverage.get("lineScope", []):
        scope = scope.rstrip("/") + "/"
        if value.startswith(scope):
            return value
        marker = "/" + scope
        if marker in value:
            return scope + value.split(marker, 1)[1]
    # Match the retained product validator's project-relative PDB rule before
    # applying source scopes: Product/File.cs and src/Product/File.cs are one
    # source file, including uncovered dependency-closure lines.
    value = value.lstrip("./")
    if value.startswith("src/"):
        return value
    prefixes = list(coverage.get("lineScope", []))
    prefixes.extend(str(Path(target).parent) + "/" for target in coverage.get("branchTargets", []))
    for scope in prefixes:
        if scope.startswith("src/") and value.startswith(scope.removeprefix("src/").rstrip("/") + "/"):
            return "src/" + value
    return value


def coverage_summary(root: Path, paths: list[Path], policy: dict, *, enforce: bool) -> dict:
    """Union lines and preserve the retained best-per-line branch policy.

    Cobertura's counts do not identify conditions. Two complementary 1/2
    reports therefore remain 1/2, never an invented 2/2 condition union.
    """
    validate_coverage_sources(root, policy)
    lines, covered = set(), set()
    branches = {}
    targets = set()
    observed_scopes = set()
    scopes = policy.get("lineScope", [])
    branch_targets = policy.get("branchTargets", [])
    for path in paths:
        validate_cobertura(path)
        for element in report_xml(path).findall(".//class"):
            filename = normalized_filename(root, element.get("filename", ""), policy)
            if not filename:
                raise ReportError(f"Cobertura class has no filename: {path}")
            scope_matches = [scope for scope in scopes if filename.startswith(scope.rstrip("/") + "/")]
            if filename in branch_targets:
                targets.add(filename)
            for line in element.findall("./lines/line"):
                key = (filename, int(line.attrib["number"]))
                if not scopes or scope_matches:
                    lines.add(key)
                    observed_scopes.update(scope_matches)
                    if int(line.attrib["hits"]) > 0:
                        covered.add(key)
                selected_branch = filename in branch_targets if branch_targets else not scopes or bool(scope_matches)
                if selected_branch and line.get("branch", "").lower() == "true":
                    match = re.fullmatch(r"[0-9.]+% \(([0-9]+)/([0-9]+)\)", line.get("condition-coverage", ""))
                    if not match:
                        raise ReportError(f"invalid Cobertura branch data: {filename}:{key[1]}")
                    count, total = map(int, match.groups())
                    if not 0 <= count <= total or total <= 0:
                        raise ReportError(f"invalid Cobertura branch counters: {filename}:{key[1]}")
                    previous = branches.get(key, (0, total))
                    if previous[1] != total:
                        raise ReportError(f"inconsistent Cobertura branch denominator: {filename}:{key[1]}")
                    branches[key] = (max(previous[0], count), total)
    branch_covered = sum(value[0] for value in branches.values())
    branch_valid = sum(value[1] for value in branches.values())
    result = {"linesCovered": len(covered), "linesValid": len(lines),
              "branchesCovered": branch_covered, "branchesValid": branch_valid}
    line_percent = 100 * len(covered) / len(lines) if lines else 0
    branch_percent = 100 * branch_covered / branch_valid if branch_valid else 0
    result.update(linePercent=line_percent, branchPercent=branch_percent)
    summary = (f"Coverage: lines {line_percent:.2f}% ({len(covered)}/{len(lines)}); "
               f"{'named ' if branch_targets else ''}branches {branch_percent:.2f}% ({branch_covered}/{branch_valid})")
    print(summary, flush=True)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as stream:
            stream.write(summary + "\n")
    if enforce:
        if not lines:
            raise ReportError("coverage has no scoped source lines")
        if "minimumLine" in policy:
            missing = set(scopes) - observed_scopes
            if missing:
                raise ReportError("coverage line scopes have no data: " + ", ".join(sorted(missing)))
            minimum = policy["minimumLine"]
            failed = line_percent <= minimum if policy.get("lineComparison", "at-least") == "greater-than" else line_percent < minimum
            if failed:
                raise ReportError(f"line coverage {line_percent:.2f}% does not satisfy {policy.get('lineComparison', 'at-least')} {minimum}%")
        if "minimumBranch" in policy:
            missing = set(policy.get("branchTargets", [])) - targets
            if missing:
                raise ReportError("coverage branch targets missing: " + ", ".join(sorted(missing)))
            if not branch_valid or branch_percent < policy["minimumBranch"]:
                raise ReportError(f"named branch coverage {branch_percent:.2f}% is below {policy['minimumBranch']}% or has no data")
    return result


def aggregate_reports(root: Path, policy: dict, stages: tuple[str, ...], *, enforce: bool) -> dict | None:
    manifests = [stage_manifest(root, stage, policy) for stage in stages]
    coverage = policy.get("coverage")
    if coverage is None:
        return None
    selected = set(coverage["projects"])
    seen, paths = set(), []
    for manifest in manifests:
        for result in manifest["results"]:
            if result["project"] not in selected:
                continue
            seen.add(result["project"])
            for relative in result["paths"]:
                if not relative.endswith("/coverage.cobertura.xml"):
                    continue
                path = root / relative
                paths.append(path)
    if enforce and selected - seen:
        raise ReportError("required coverage projects have no completed reports: " + ", ".join(sorted(selected - seen)))
    if not paths and not enforce:
        return None
    return coverage_summary(root, paths, coverage, enforce=enforce)
