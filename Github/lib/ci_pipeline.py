#!/usr/bin/env python3
"""Shared, stdlib-only CI stages 2–3, also importable by the future local check.

Workflow-owned composites provide this file and the NuGet configuration. The
module's Builds gitlink supplies product imports only, never executable CI code.
All commands run with the module as cwd so its global.json controls the SDK.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

# -I excludes the script directory. Only this workflow-owned directory is added.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import BUILDS_REPOSITORY, ConfigError, _read_json, load_config, validate_config


NUGET_CONFIG = Path(__file__).resolve().parents[2] / "NuGet.ci.Config"
PROPERTIES = ("IsTestProject", "TargetFramework", "TargetFrameworks", "IsCrossTargetingBuild", "XunitTestProject", "TargetPath")


class PipelineError(ConfigError):
    """Discovery or preparation failed closed, or a product command failed."""

    def __init__(self, message: str, returncode: int = 1):
        super().__init__(message)
        self.returncode = returncode if returncode > 0 else 128 - returncode


@dataclass(frozen=True)
class PreparedConfig:
    """Validated module context, independent of GitHub Actions."""

    workspace: Path
    root: Path
    document: dict[str, Any]
    solution: str
    global_json: str
    submodules: tuple[str, ...]


@dataclass(frozen=True)
class TestProjects:
    """Ordinal repository-relative project paths for each disposition."""

    unit: tuple[str, ...]
    integration: tuple[str, ...]
    excluded: tuple[str, ...]


def build_flags(nuget_config: Path = NUGET_CONFIG) -> list[str]:
    """Return mandatory flags shared by restore, build, evaluation and tests."""
    return ["-noAutoResponse", "-p:Configuration=Release", "-warnaserror",
            "-p:UseHexalithProjectReferences=false", "-p:NuGetAudit=false",
            f"-p:RestoreConfigFile={nuget_config.resolve()}",
            "-p:EnableNETAnalyzers=true", "-p:RunAnalyzers=true",
            "-p:RunAnalyzersDuringBuild=true"]


def test_flags(runner: str) -> list[str]:
    """Adapt engine switches to SDK 10 MTP's in-process MSBuild evaluation.

    MTP forwards unknown switches to test applications: -noAutoResponse and
    -warnaserror cause an invalid-option handshake failure. Its in-process
    MSBuild API does not parse automatic response files. Keep all properties,
    and express warnings-as-errors as properties in this one command path.
    """
    flags = build_flags()
    if runner == "Microsoft.Testing.Platform":
        flags = [flag for flag in flags if flag not in ("-noAutoResponse", "-warnaserror")]
        flags.extend(("-p:TreatWarningsAsErrors=true", "-p:MSBuildTreatWarningsAsErrors=true"))
    return flags


def run_command(arguments: list[str], root: Path, *, capture: bool = False) -> str:
    """Execute without a shell and preserve a failing product's exit status."""
    environment = os.environ.copy()
    environment.update(DOTNET_CLI_UI_LANGUAGE="en-US", DOTNET_NOLOGO="1")
    try:
        result = subprocess.run(arguments, cwd=root, env=environment, text=True,
                                stdout=subprocess.PIPE if capture else None,
                                stderr=subprocess.PIPE if capture else None, check=False)
    except OSError as error:
        raise PipelineError(f"cannot execute {arguments[0]}: {error}") from error
    if result.returncode:
        if capture:
            print(result.stdout, end="", file=sys.stderr)
            print(result.stderr, end="", file=sys.stderr)
        raise PipelineError(f"{arguments[0]} {arguments[1]} failed ({result.returncode})",
                            result.returncode)
    return result.stdout if capture else ""


def contained_path(root: Path, value: str, *, kind: str) -> Path:
    """Resolve an existing module-owned path without escaping through symlinks."""
    if (not value or value.startswith("/") or "\\" in value or "${{" in value
            or re.match(r"[A-Za-z]:", value) or any(part in ("", ".", "..") for part in value.split("/"))
            or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in value)):
        raise PipelineError(f"unsafe {kind} path: {value!r}")
    resolved = (root / value).resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise PipelineError(f"{kind} escapes its repository: {value!r}")
    return resolved


def prepare_config(workspace: Path, *, repository: str, module_root: str = "",
                   expected_source: str = "") -> PreparedConfig:
    """Load the strict configuration before any SDK or product command runs."""
    workspace = workspace.resolve()
    if (module_root or expected_source) and repository != BUILDS_REPOSITORY:
        raise PipelineError("module-root and expected-source are test-only Builds inputs")
    if expected_source and not module_root:
        raise PipelineError("expected-source requires module-root")
    root = contained_path(workspace, module_root, kind="module-root") if module_root else workspace
    if not root.is_dir():
        raise PipelineError(f"module root does not exist: {root}")
    if expected_source:
        source = contained_path(workspace, expected_source, kind="expected-source")
        if not source.is_dir():
            raise PipelineError(f"expected-source does not exist: {source}")
    config = load_config(root / ".github/hexalith-ci.json", repository=repository, module_root=module_root)
    if config["solution"] == "none":
        return PreparedConfig(workspace, root, config, "none", "", ())
    solution = contained_path(root, config["solution"], kind="solution")
    if not solution.is_file():
        raise PipelineError(f"solution does not exist: {solution}")
    global_json = root / "global.json"
    sdk = _read_json(global_json)
    if (not isinstance(sdk, dict) or not isinstance(sdk.get("sdk"), dict)
            or not isinstance(sdk["sdk"].get("version"), str) or not sdk["sdk"]["version"]):
        raise PipelineError(f"{global_json}: sdk.version is required")
    test_runner(root)  # Reject malformed runner metadata before setup-dotnet.
    prefix = f"{module_root}/" if module_root else ""
    paths = ([] if repository == BUILDS_REPOSITORY and not module_root
             else ["references/Hexalith.Builds"])
    paths.extend(config["submodules"])
    submodules = tuple(dict.fromkeys(prefix + path for path in paths))
    return PreparedConfig(workspace, root, config, prefix + config["solution"],
                          prefix + "global.json", submodules)


def initialize_submodules(prepared: PreparedConfig) -> None:
    """Initialize only explicit gitlinks declared in the checkout's root file."""
    if not prepared.submodules:
        return
    text = run_command(["git", "config", "--file", ".gitmodules", "--get-regexp",
                        r"^submodule\..*\.path$"], prepared.workspace, capture=True)
    declared = [line.split(None, 1)[1] for line in text.splitlines() if line.strip()]
    if len(set(declared)) != len(declared):
        raise PipelineError("duplicate paths in root .gitmodules")
    for path in prepared.submodules:
        contained_path(prepared.workspace, path, kind="submodule")
        if path not in declared:
            raise PipelineError(f"submodule is not declared in root .gitmodules: {path}")
        entry = run_command(["git", "ls-files", "--stage", "--", path], prepared.workspace, capture=True)
        if len(entry.splitlines()) != 1 or not entry.startswith("160000 "):
            raise PipelineError(f"declared submodule is not a direct root gitlink: {path}")
    run_command(["git", "-c", "protocol.file.allow=never", "-c", "submodule.recurse=false",
                 "submodule", "update", "--init", "--checkout", "--",
                 *prepared.submodules], prepared.workspace)


def test_runner(root: Path) -> str:
    """Use the SDK's global.json runner selection, with ordinary VSTest default."""
    document = _read_json(root / "global.json")
    if not isinstance(document, dict):
        raise PipelineError("global.json must contain an object")
    metadata = document.get("test", {})
    if not isinstance(metadata, dict):
        raise PipelineError("global.json test must contain an object")
    runner = metadata.get("runner", "VSTest")
    if runner not in ("VSTest", "Microsoft.Testing.Platform"):
        raise PipelineError(f"unsupported global.json test runner: {runner!r}")
    return runner


def solution_projects(root: Path, solution: str) -> tuple[str, ...]:
    """Ask the selected SDK for solution membership; never search source files."""
    solution_path = contained_path(root, solution, kind="solution")
    text = run_command(["dotnet", "sln", str(solution_path), "list"], root, capture=True)
    lines = text.strip().splitlines()
    if lines == ["No projects found in the solution."]:
        return ()
    if len(lines) < 2 or lines[0].strip() != "Project(s)" or set(lines[1].strip()) != {"-"}:
        raise PipelineError("unsupported dotnet sln list output; project discovery failed")
    projects = []
    for line in lines[2:]:
        path = line.strip().replace("\\", "/")
        # SDK paths are relative to the solution directory, not the module root.
        project = (solution_path.parent / path).resolve()
        if not project.is_relative_to(root.resolve()) or not project.is_file() or project.suffix != ".csproj":
            raise PipelineError(f"unsupported or missing solution project: {path!r}")
        projects.append(project.relative_to(root.resolve()).as_posix())
    if len(projects) != len(set(projects)):
        raise PipelineError("duplicate project in solution discovery")
    return tuple(sorted(projects))


def evaluated_properties(root: Path, project: str, *, framework: str = "") -> dict[str, str]:
    """Read real MSBuild evaluation in the same package-mode Release context."""
    arguments = ["dotnet", "msbuild", project, "-nologo", "-verbosity:quiet",
                 "-getProperty:" + ",".join(PROPERTIES), *build_flags()]
    if framework:
        arguments.append(f"-p:TargetFramework={framework}")
    output = run_command(arguments, root, capture=True)
    try:
        document = json.loads(output)
        values = document["Properties"]
        if not isinstance(values, dict) or any(not isinstance(values.get(key), str) for key in PROPERTIES):
            raise ValueError("missing or non-string properties")
        for name in ("IsTestProject", "XunitTestProject"):
            if values[name].lower() not in ("", "true", "false"):
                raise ValueError(f"{name} must be true, false or empty")
        return values
    except (ValueError, KeyError, TypeError) as error:
        raise PipelineError(f"{project}: invalid MSBuild discovery metadata: {error}") from error


def discover_test_projects(root: Path, solution: str) -> tuple[str, ...]:
    """Evaluate every solution project, including each cross-targeting inner build."""
    tests = []
    for project in solution_projects(root, solution):
        outer = evaluated_properties(root, project)
        frameworks = outer["TargetFrameworks"].split(";") if outer["TargetFrameworks"] else []
        if outer["IsCrossTargetingBuild"].lower() not in ("", "true", "false"):
            raise PipelineError(f"{project}: invalid IsCrossTargetingBuild")
        if outer["IsCrossTargetingBuild"].lower() == "true" and not frameworks:
            raise PipelineError(f"{project}: cross-targeting build has no TargetFrameworks")
        if any(not framework or framework.strip() != framework for framework in frameworks):
            raise PipelineError(f"{project}: invalid TargetFrameworks")
        values = [evaluated_properties(root, project, framework=framework) for framework in frameworks]
        flags = [value["IsTestProject"].lower() == "true" for value in values]
        if flags and len(set(flags)) != 1:
            raise PipelineError(f"{project}: IsTestProject differs across target frameworks")
        is_test = flags[0] if flags else outer["IsTestProject"].lower() == "true"
        if flags and outer["IsTestProject"] and (outer["IsTestProject"].lower() == "true") != is_test:
            raise PipelineError(f"{project}: outer and inner IsTestProject disagree")
        if is_test:
            tests.append(project)
    return tuple(tests)


def classify_projects(projects: tuple[str, ...], document: dict[str, Any], *,
                      repository: str, module_root: str = "") -> TestProjects:
    """Apply AD-13 names and only integration/enabled-action exclusions."""
    validate_config(document, repository=repository, module_root=module_root)
    known = set(projects)
    overrides = set(document["tests"]["integration"]) | document["tests"]["exclude"].keys()
    if overrides - known:
        raise PipelineError("test overrides name unknown test projects: " + ", ".join(sorted(overrides - known)))
    unit, integration, excluded = [], [], []
    for project in sorted(known):
        name = Path(project).stem
        if project in document["tests"]["exclude"]:
            excluded.append(project)
        elif (project in document["tests"]["integration"] or "E2E" in name
              or name.endswith(("IntegrationTests", "Integration.Tests", "AppHost.Tests"))):
            integration.append(project)
        else:
            unit.append(project)
    return TestProjects(tuple(unit), tuple(integration), tuple(excluded))


def restore_and_build(prepared: PreparedConfig) -> None:
    """Restore once, then build Release with analyzers and mandatory settings."""
    if prepared.solution == "none":
        return
    solution = prepared.document["solution"]
    run_command(["dotnet", "restore", solution, *build_flags()], prepared.root)
    run_command(["dotnet", "build", solution, "--no-restore", *build_flags()], prepared.root)


@contextmanager
def complete_xunit_vstest_configuration(root: Path, project: str, properties: dict[str, str]):
    """Override only generated output configs, restoring exact bytes on failure.

    The xUnit v3 VSTest adapter forwards false as an absent executable switch;
    the child can therefore reload stopOnFail=true from its output config despite
    the inline RunSettings override. Keep the source configuration untouched.
    """
    originals = {}
    try:
        frameworks = properties["TargetFrameworks"].split(";") if properties["TargetFrameworks"] else []
        targets = [evaluated_properties(root, project, framework=value) for value in frameworks] if frameworks else [properties]
        for values in targets:
            if not values["TargetPath"]:
                raise PipelineError(f"{project}: missing evaluated test TargetPath")
            assembly = Path(values["TargetPath"])
            specific = assembly.with_name(assembly.stem + ".xunit.runner.json")
            config = specific if specific.is_file() else assembly.parent / "xunit.runner.json"
            if not config.is_file() or config in originals:
                continue
            output_directory = assembly.parent.resolve()
            resolved = config.resolve()
            source_directory = (root / project).resolve().parent
            if (resolved.parent != output_directory or not resolved.is_relative_to(root.resolve())
                    or source_directory.is_relative_to(output_directory)):
                raise PipelineError(f"{config}: refusing to override a source runner configuration")
            if run_command(["git", "ls-files", "--", resolved.name], output_directory, capture=True):
                raise PipelineError(f"{config}: refusing to override a tracked runner configuration")
            document = _read_json(config)
            if not isinstance(document, dict):
                raise PipelineError(f"{config}: xUnit configuration must be an object")
            originals[config] = config.read_bytes()
            document["stopOnFail"] = False
            config.write_text(json.dumps(document), encoding="utf-8")
        yield
    finally:
        for path, content in originals.items():
            path.write_bytes(content)


def execute_unit_projects(root: Path, projects: tuple[str, ...], *, runner: str) -> None:
    """Finish every test in a project; stop before the next failing-project successor."""
    if runner not in ("VSTest", "Microsoft.Testing.Platform"):
        raise PipelineError(f"unsupported test runner: {runner!r}")
    for project in sorted(projects):
        print(f"Unit tests: {project}", flush=True)
        selector = ["--project", project] if runner == "Microsoft.Testing.Platform" else [project]
        options = []
        properties = evaluated_properties(root, project)
        xunit = properties["XunitTestProject"].lower() == "true"
        if xunit:
            # Override only xUnit's early-stop setting; preserve its other settings
            # and leave non-xUnit applications free of framework-specific options.
            options = (["--stop-on-fail", "off"] if runner == "Microsoft.Testing.Platform"
                       else ["--", "xUnit.StopOnFail=false"])
        arguments = ["dotnet", "test", *selector, "--no-build", "--no-restore", *test_flags(runner), *options]
        if xunit and runner == "VSTest":
            with complete_xunit_vstest_configuration(root, project, properties):
                run_command(arguments, root)
        else:
            run_command(arguments, root)


def run_unit_tests(prepared: PreparedConfig, *, repository: str, module_root: str = "") -> TestProjects:
    """Discover, classify and execute only stage 3; stage 4 remains future work."""
    if prepared.solution == "none":
        return TestProjects((), (), ())
    projects = discover_test_projects(prepared.root, prepared.document["solution"])
    classified = classify_projects(projects, prepared.document, repository=repository, module_root=module_root)
    print(json.dumps({"unit": classified.unit, "integration": classified.integration,
                      "excluded": classified.excluded}), flush=True)
    execute_unit_projects(prepared.root, classified.unit, runner=test_runner(prepared.root))
    return classified


def write_outputs(prepared: PreparedConfig) -> None:
    """Expose validated workspace-relative inputs for future Windows prepare."""
    values = {"solution": prepared.solution, "global-json": prepared.global_json,
              "submodules": json.dumps(prepared.submodules)}
    if os.environ.get("GITHUB_OUTPUT"):
        with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
            for name, value in values.items():
                # JSON encoding handles submodule strings; path values were validated.
                if "\n" in value or "\r" in value:
                    raise PipelineError(f"invalid workflow output: {name}")
                stream.write(f"{name}={value}\n")
    print(json.dumps(values), flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workspace", type=Path, default=os.environ.get("GITHUB_WORKSPACE", "."))
    parser.add_argument("--repository", default=os.environ.get("CI_REPOSITORY"), required=not os.environ.get("CI_REPOSITORY"))
    parser.add_argument("--module-root", default=os.environ.get("CI_MODULE_ROOT", ""))
    parser.add_argument("--expected-source", default=os.environ.get("CI_EXPECTED_SOURCE", ""))
    parser.add_argument("--phase", choices=("prepare", "build", "unit", "all"), default="all")
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise PipelineError("Python 3.12 or later is required")
        prepared = prepare_config(args.workspace, repository=args.repository,
                                  module_root=args.module_root, expected_source=args.expected_source)
        if args.phase in ("prepare", "all"):
            initialize_submodules(prepared)
            write_outputs(prepared)
        if args.phase in ("build", "all"):
            restore_and_build(prepared)
        if args.phase in ("unit", "all"):
            run_unit_tests(prepared, repository=args.repository, module_root=args.module_root)
        return 0
    except ConfigError as error:
        print(error, file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    raise SystemExit(main())
