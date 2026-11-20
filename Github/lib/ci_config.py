#!/usr/bin/env python3
"""Load complete CI configuration using only Python >=3.12's standard library.

Callers supply trusted repository context, never values read from the document.
The schema identity comes from this loader's own checkout. Validation returns
the supplied values unchanged and never interprets schema defaults.
"""

from __future__ import annotations

import argparse
from functools import lru_cache
import json
from pathlib import Path
import re
import sys
from typing import Any


SCHEMA_PATH = Path(__file__).resolve().parents[2] / "schemas/hexalith.ci.v1.json"
BUILDS_REPOSITORY = "Hexalith/Hexalith.Builds"
HOOKS = frozenset(("after-unit-tests", "after-integration-tests", "nightly"))
LANGUAGES = frozenset(("c-cpp", "csharp", "go", "java-kotlin", "javascript-typescript",
                       "python", "ruby", "rust", "swift"))
REQUIRED = frozenset(("schema", "solution", "dapr", "browsers", "registry", "codeql",
                      "tests", "hooks", "jobs", "submodules", "npmWorkspaces", "dotnetTools", "ciTimeoutMinutes"))
ACTION_NAME = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")
SECRET_NAME = re.compile(r"[A-Z_][A-Z0-9_]*")
RESOURCE_NAME = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")
STABLE_TAG = re.compile(r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
BUILD_VERSIONS = frozenset(("HexalithEventStoreVersion", "HexalithTenantsVersion", "HexalithMemoriesVersion"))
SDK_VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")


class ConfigError(ValueError):
    """A configuration cannot be safely consumed."""

    def __init__(self, message: str):
        # Config values and path/key locations must not introduce log lines or
        # GitHub Actions commands. Keep ordinary diagnostics byte-for-byte.
        escaped = "".join(f"\\u{ord(char):04x}" if ord(char) < 32 or 127 <= ord(char) <= 159
                          or char in "\u2028\u2029" else char for char in message)
        super().__init__(escaped)


def _object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ConfigError(f"duplicate key {key!r}")
        result[key] = value
    return result


def _constant(value: str) -> Any:
    raise ConfigError(f"non-JSON constant {value!r}")


def _read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_object, parse_constant=_constant)
    except (OSError, UnicodeError, ValueError, RecursionError) as error:
        raise ConfigError(f"{path}: {error}") from error


@lru_cache(maxsize=1)
def schema_identity() -> str:
    """Return the schema identity shipped beside this loader, independent of cwd."""
    identity = _read_json(SCHEMA_PATH)["properties"]["schema"]["const"]
    if not isinstance(identity, str) or not identity:
        raise ConfigError(f"{SCHEMA_PATH}: missing schema identity")
    return identity


def validate_config(document: Any, *, repository: str, module_root: str = "",
                    source: str = ".github/hexalith-ci.json") -> dict[str, Any]:
    """Validate a parsed document and return it unchanged.

    ``repository`` is the trusted owner/name of the running repository.
    ``module_root`` is the trusted sample root, when present. Only Builds with
    no module root may omit buildsTag. Filesystem/action existence belongs to
    delivery's per-module checks; enabled exclusion references are checked here.
    """
    def fail(location: str, message: str) -> None:
        raise ConfigError(f"{source}: {location}: {message}")

    def closed(value: Any, required: frozenset[str], optional: frozenset[str], location: str) -> None:
        if not isinstance(value, dict):
            fail(location, "expected an object")
        if any(not isinstance(key, str) for key in value):
            fail(location, "object keys must be strings")
        missing = required - value.keys()
        unknown = value.keys() - required - optional
        if missing:
            fail(location, f"missing required keys: {', '.join(sorted(missing))}")
        if unknown:
            fail(location, f"unknown keys: {', '.join(sorted(unknown))}")

    def path(value: Any, location: str, suffix: str = "") -> None:
        if not isinstance(value, str) or not value:
            fail(location, "expected a nonempty repository-relative path")
        if (value.startswith("/") or re.match(r"[A-Za-z]:", value) or "\\" in value
                or "//" in value or value.endswith("/") or "${{" in value
                or any(part in (".", "..") for part in value.split("/"))
                or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in value)):
            fail(location, "unsafe repository-relative path")
        if suffix and not value.endswith(suffix):
            fail(location, f"expected a {suffix} path")

    def name(value: Any, location: str) -> None:
        if not isinstance(value, str) or ACTION_NAME.fullmatch(value) is None:
            fail(location, "expected a lowercase kebab-case action name")

    def array(value: Any, location: str) -> None:
        if not isinstance(value, list):
            fail(location, "expected an array")
        for index, item in enumerate(value):
            if item in value[:index]:
                fail(f"{location}/{index}", "duplicate item")

    if not isinstance(repository, str) or not repository or not isinstance(module_root, str):
        fail("$", "trusted repository and module_root strings are required")
    if not isinstance(document, dict):
        fail("$", "expected an object")
    if "schema" in document and document["schema"] != schema_identity():
        value = document["schema"]
        raise ConfigError(f"{source}: unsupported schema {value}. CI/CD is owned by Hexalith.CiCd.")
    closed(document, REQUIRED, frozenset(("buildsTag", "aspire", "job-timeout-minutes",
                                         "moduleManifest", "windowsCheckout", "additionalSdkVersion",
                                         "releaseTimeoutMinutes", "nightlyTimeoutMinutes", "prerequisites",
                                         "publishedBuilds", "buildProjects", "docs")), "$")
    if "docs" in document and document["docs"] not in ("off", "warn", "fail"):
        fail("$/docs", "expected off, warn or fail")
    if "additionalSdkVersion" in document:
        version = document["additionalSdkVersion"]
        if not isinstance(version, str) or SDK_VERSION.fullmatch(version) is None:
            fail("$/additionalSdkVersion", "expected an exact stable MAJOR.MINOR.PATCH SDK version")
    if "buildsTag" not in document:
        if repository != BUILDS_REPOSITORY or module_root:
            fail("$/buildsTag", "required outside Builds' own repository, including samples")
    elif not isinstance(document["buildsTag"], str) or STABLE_TAG.fullmatch(document["buildsTag"]) is None:
        fail("$/buildsTag", "expected an exact stable vMAJOR.MINOR.PATCH release tag")
    if document["solution"] != "none":
        path(document["solution"], "$/solution", ".slnx")
    if document.get("moduleManifest") is not None:
        path(document["moduleManifest"], "$/moduleManifest", ".json")
    if document.get("publishedBuilds") is not None:
        path(document["publishedBuilds"], "$/publishedBuilds", ".json")
        if document.get("moduleManifest"):
            fail("$/publishedBuilds", "mutually exclusive with moduleManifest")
    cases = document.get("buildProjects", [])
    array(cases, "$/buildProjects")
    if len(cases) > 32:
        fail("$/buildProjects", "at most 32 projects are supported")
    identifiers, selections, isolated_projects = set(), set(), set()
    for index, case in enumerate(cases):
        location = f"$/buildProjects/{index}"
        isolated = isinstance(case, dict) and "isolatedContract" in case
        closed(case, frozenset(("id", "project", "isolatedContract" if isolated else "versions")), frozenset(), location)
        name(case["id"], location + "/id")
        path(case["project"], location + "/project", ".csproj")
        if (case["project"].startswith(("references/", ".github/", ".git/"))
                or any(char in case["project"] for char in ':;,%$@*?<>"|')):
            fail(location + "/project", "unsafe or foreign compatibility project")
        project = case["project"].casefold()
        if isolated:
            path(case["isolatedContract"], location + "/isolatedContract", ".json")
            if (case["isolatedContract"].startswith(("references/", ".github/", ".git/"))
                    or any(char in case["isolatedContract"] for char in ':;,%$@*?<>"|')):
                fail(location + "/isolatedContract", "unsafe or foreign isolated contract")
            if not document.get("moduleManifest"):
                fail(location + "/isolatedContract", "requires authenticated managed module preparation")
            if any(selected[0] == project for selected in selections):
                fail(location, "duplicate isolated project")
            isolated_projects.add(project)
            selection = (project, ("isolatedContract", case["isolatedContract"].casefold()))
        else:
            versions = case["versions"]
            if not isinstance(versions, dict) or not versions or versions.keys() - BUILD_VERSIONS:
                fail(location + "/versions", "only finite named EventStore/Tenants/Memories catalog versions are supported")
            for key, value in versions.items():
                if not isinstance(value, str) or SDK_VERSION.fullmatch(value) is None:
                    fail(location + "/versions/" + key, "expected an exact stable version")
            if project in isolated_projects:
                fail(location, "duplicate isolated project")
            selection = (project, tuple(sorted(versions.items())))
        if case["id"] in identifiers or selection in selections:
            fail(location, "duplicate id or project/version selection")
        identifiers.add(case["id"])
        selections.add(selection)
    if cases and not (document.get("moduleManifest") or document.get("publishedBuilds")):
        fail("$/buildProjects", "requires authenticated producer preparation")
    for key in ("dapr", "browsers", "registry", "dotnetTools"):

        if type(document[key]) is not bool:
            fail(f"$/{key}", "expected a boolean")
    if "aspire" in document and type(document["aspire"]) is not bool:
        fail("$/aspire", "expected a boolean")
    if document.get("aspire") and not document["dapr"]:
        fail("$/aspire", "requires dapr=true")
    if "job-timeout-minutes" in document:
        minutes = document["job-timeout-minutes"]
        if (type(minutes) not in (int, float) or not 1 <= minutes <= 360
                or (type(minutes) is float and not minutes.is_integer())):
            fail("$/job-timeout-minutes", "expected an integer from 1 to 360")
    if type(document["ciTimeoutMinutes"]) is not int or not 1 <= document["ciTimeoutMinutes"] <= 360:
        fail("$/ciTimeoutMinutes", "expected an integer from 1 to 360")
    for key in ("releaseTimeoutMinutes", "nightlyTimeoutMinutes"):
        if key in document and (type(document[key]) is not int or not 1 <= document[key] <= 360):
            fail(f"$/{key}", "expected an integer from 1 to 360")

    def prerequisites(value: Any, location: str) -> None:
        closed(value, frozenset(), frozenset(("nodeVersion", "npmVersion", "apt", "tlsTrust", "daprProfile")), location)
        for key in ("nodeVersion", "npmVersion"):
            if key in value and (not isinstance(value[key], str)
                                 or re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", value[key]) is None):
                fail(f"{location}/{key}", "expected an exact numeric version")
        if ("nodeVersion" in value) != ("npmVersion" in value):
            fail(location, "nodeVersion and npmVersion must be supplied together")
        for key in ("apt", "tlsTrust"):
            if key in value and type(value[key]) is not bool:
                fail(f"{location}/{key}", "expected a boolean")
        if "daprProfile" in value and value["daprProfile"] != "catalog":
            fail(f"{location}/daprProfile", "expected the catalog profile; other tuples require separate qualification")

    if "prerequisites" in document:
        prerequisites(document["prerequisites"], "$/prerequisites")
    for key in ("codeql", "hooks", "submodules", "npmWorkspaces"):
        array(document[key], f"$/{key}")
    for index, language in enumerate(document["codeql"]):
        if not isinstance(language, str) or language not in LANGUAGES:
            fail(f"$/codeql/{index}", "expected a supported CodeQL language; actions is forbidden")
    for index, hook in enumerate(document["hooks"]):
        if not isinstance(hook, str) or hook not in HOOKS:
            fail(f"$/hooks/{index}", "expected after-unit-tests, after-integration-tests or nightly")
    for index, submodule in enumerate(document["submodules"]):
        path(submodule, f"$/submodules/{index}")
    for index, workspace in enumerate(document["npmWorkspaces"]):
        path(workspace, f"$/npmWorkspaces/{index}")
    tests = document["tests"]
    closed(tests, frozenset(("integration", "exclude", "minimumExpectedTests", "failSkips")),
           frozenset(("additional", "reports")), "$/tests")
    if "additional" in tests:
        array(tests["additional"], "$/tests/additional")
        for index, project in enumerate(tests["additional"]):
            path(project, f"$/tests/additional/{index}", ".csproj")
    array(tests["integration"], "$/tests/integration")
    for index, project in enumerate(tests["integration"]):
        path(project, f"$/tests/integration/{index}", ".csproj")
    if not isinstance(tests["exclude"], dict):
        fail("$/tests/exclude", "expected an object mapping projects to enabled actions")
    if not isinstance(tests["minimumExpectedTests"], dict):
        fail("$/tests/minimumExpectedTests", "expected an object mapping projects to positive integers")
    for project, minimum in tests["minimumExpectedTests"].items():
        location = f"$/tests/minimumExpectedTests/{project}"
        path(project, location, ".csproj")
        if type(minimum) is not int or minimum <= 0:
            fail(location, "expected a positive integer")
        if project in tests["exclude"]:
            fail(location, "minimum cannot apply to an excluded project")
    if type(tests["failSkips"]) is not bool:
        fail("$/tests/failSkips", "expected a boolean")
    if "reports" in tests:
        reports = tests["reports"]
        closed(reports, frozenset(), frozenset(("trx", "coverage")), "$/tests/reports")
        if "trx" in reports and type(reports["trx"]) is not bool:
            fail("$/tests/reports/trx", "expected a boolean")
        if "coverage" in reports:
            coverage = reports["coverage"]
            location = "$/tests/reports/coverage"
            closed(coverage, frozenset(("projects",)), frozenset(("lineScope", "minimumLine", "lineComparison",
                                                               "branchTargets", "minimumBranch")), location)
            for key in ("projects", "lineScope", "branchTargets"):
                if key not in coverage:
                    continue
                array(coverage[key], f"{location}/{key}")
                for index, item in enumerate(coverage[key]):
                    candidate = item[:-1] if key == "lineScope" and isinstance(item, str) and item.endswith("/") else item
                    path(candidate, f"{location}/{key}/{index}", ".csproj" if key == "projects" else "")
            if not coverage["projects"]:
                fail(f"{location}/projects", "at least one coverage project is required")
            if any(project in tests["exclude"] for project in coverage["projects"]):
                fail(f"{location}/projects", "coverage cannot apply to excluded projects")
            for key, scope in (("minimumLine", "lineScope"), ("minimumBranch", "branchTargets")):
                if key in coverage:
                    if type(coverage[key]) not in (int, float) or not 0 <= coverage[key] <= 100:
                        fail(f"{location}/{key}", "expected a percentage from 0 to 100")
                    if not coverage.get(scope):
                        fail(f"{location}/{key}", f"requires a nonempty {scope}")
            if coverage.get("lineComparison", "at-least") not in ("greater-than", "at-least"):
                fail(f"{location}/lineComparison", "expected greater-than or at-least")
    jobs = document["jobs"]
    if not isinstance(jobs, dict):
        fail("$/jobs", "expected an object")
    if len(jobs) > 64:
        fail("$/jobs", "at most 64 custom jobs are supported")
    for job, options in jobs.items():
        location = f"$/jobs/{job}"
        name(job, location)
        if job == "ci" or job in HOOKS:
            fail(location, "job name is reserved for CI or a fixed hook")
        if job == "none" and isinstance(options, dict) and options.get("trigger") == "manual":
            fail(location, "none is reserved for the manual dispatch default")
        if isinstance(options, dict) and "preparation" in options and isinstance(options.get("secrets"), list):
            closed(options, frozenset(("runner", "trigger", "environment", "secrets",
                                       "preparation", "timeoutMinutes")), frozenset(), location)
            runner = options["runner"]
            closed(runner, frozenset(("group", "labels")), frozenset(), f"{location}/runner")
            for key, value in (("runner/group", runner["group"]), ("environment", options["environment"])):
                if not isinstance(value, str) or RESOURCE_NAME.fullmatch(value) is None:
                    fail(f"{location}/{key}", "expected a literal lowercase kebab-case resource name")
            if runner["labels"] != ["self-hosted", "linux", "x64"]:
                fail(f"{location}/runner/labels", "expected self-hosted, linux, x64 in that order")
            if options["preparation"] != "build-only":
                fail(f"{location}/preparation", "manual qualification requires build-only preparation")
            timeout = options["timeoutMinutes"]
            if type(timeout) is not int or not 1 <= timeout <= 180:
                fail(f"{location}/timeout-minutes", "expected an integer from 1 to 180")
            array(options["secrets"], f"{location}/secrets")
            if not options["secrets"]:
                fail(f"{location}/secrets", "manual qualification requires explicitly named secrets")
            for index, secret in enumerate(options["secrets"]):
                if (not isinstance(secret, str) or SECRET_NAME.fullmatch(secret) is None
                        or secret.startswith(("GITHUB_", "ACTIONS_", "RUNNER_"))
                        or secret in ("HEXALITH_JOB_NAME", "HEXALITH_JOB_PARAMETERS", "HEXALITH_JOB_NEEDS",
                                      "HEXALITH_QUALIFICATION_PARAMETERS", "HEXALITH_AUTHORITY_SHA256", "HEXALITH_SOURCE_SHA")):
                    fail(f"{location}/secrets/{index}", "expected an explicit non-reserved uppercase secret name")
            if options["trigger"] != "manual":
                fail(f"{location}/trigger", "build-only qualification requires a manual job")
            continue
        closed(options, frozenset(("runner",)), frozenset(("timeoutMinutes", "trigger", "matrix", "maxParallel",
               "needs", "runAfterFailure", "resourceProfile", "environment", "secrets", "setup", "preparation")), location)
        runner = options["runner"]
        private = isinstance(runner, dict)
        trigger = options.get("trigger", "ci")
        if trigger not in ("ci", "nightly", "manual"):
            fail(f"{location}/trigger", "expected ci, nightly or manual")
        if private:
            closed(runner, frozenset(("group", "labels")), frozenset(), f"{location}/runner")
            if (not isinstance(runner["group"], str)
                    or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", runner["group"]) is None
                    or runner["labels"] != ["self-hosted", "linux", "x64"]):
                fail(f"{location}/runner", "expected an explicit private Linux runner group and self-hosted/linux/x64 labels")
            if trigger != "manual" or not options.get("environment"):
                fail(f"{location}/runner", "private execution requires a manual job and protected environment")
            if "setup" in options:
                fail(f"{location}/setup", "private execution preserves owner-prepared fixtures and cannot override setup")
        elif runner not in ("ubuntu-latest", "ubuntu-24.04", "windows-latest", "macos-latest"):
            fail(f"{location}/runner", "expected a supported hosted runner or protected private Linux group")
        linux = private or runner in ("ubuntu-latest", "ubuntu-24.04")
        if "timeoutMinutes" in options and (type(options["timeoutMinutes"]) is not int
                                            or not 1 <= options["timeoutMinutes"] <= (180 if private else 360)):
            fail(f"{location}/timeoutMinutes", f"expected an integer from 1 to {180 if private else 360}")
        if "preparation" in options and (not private or trigger != "manual" or options["preparation"] != "build-only"
                                         or not isinstance(options.get("secrets"), dict)):
            fail(f"{location}/preparation", "build-only preparation requires a private manual job with mapped secrets")
        if "environment" in options:
            name(options["environment"], f"{location}/environment")
        if "resourceProfile" in options:
            if options["resourceProfile"] != "mutation" or not linux or private:
                fail(f"{location}/resourceProfile", "mutation requires a hosted Linux job")
        if "matrix" in options:
            matrix = options["matrix"]
            closed(matrix, frozenset(("include",)), frozenset(), f"{location}/matrix")
            array(matrix["include"], f"{location}/matrix/include")
            if not 1 <= len(matrix["include"]) <= 64:
                fail(f"{location}/matrix/include", "expected 1 to 64 isolated cases")
            identifiers = set()
            for index, row in enumerate(matrix["include"]):
                row_location = f"{location}/matrix/include/{index}"
                if not isinstance(row, dict) or "id" not in row:
                    fail(row_location, "expected a record with a unique id")
                for key, value in row.items():
                    name(key, row_location)
                    if (not isinstance(value, str) or len(value) > 4096 or "${{" in value
                            or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in value)):
                        fail(row_location, "matrix values must be bounded literal strings without controls or expressions")
                name(row["id"], f"{row_location}/id")
                if row["id"] in identifiers:
                    fail(f"{row_location}/id", "duplicate matrix id")
                identifiers.add(row["id"])
        if "maxParallel" in options and ("matrix" not in options or type(options["maxParallel"]) is not int
                                         or not 1 <= options["maxParallel"] <= 64):
            fail(f"{location}/maxParallel", "requires a matrix and an integer from 1 to 64")
        if "runAfterFailure" in options and type(options["runAfterFailure"]) is not bool:
            fail(f"{location}/runAfterFailure", "expected a boolean")
        if "needs" in options:
            array(options["needs"], f"{location}/needs")
            for dependency in options["needs"]:
                name(dependency, f"{location}/needs")
                if dependency == job or dependency not in jobs:
                    fail(f"{location}/needs", "dependencies must name other enabled jobs")
                if not isinstance(jobs[dependency], dict) or jobs[dependency].get("trigger", "ci") != trigger:
                    fail(f"{location}/needs", "dependencies must use the same trigger")
        if options.get("runAfterFailure") and not options.get("needs"):
            fail(f"{location}/runAfterFailure", "requires an explicit dependency")
        if "secrets" in options:
            secrets = options["secrets"]
            if not isinstance(secrets, dict) or not private or trigger != "manual" or not options.get("environment"):
                fail(f"{location}/secrets", "secrets require protected private manual execution")
            for variable, secret in secrets.items():
                if (not isinstance(variable, str) or re.fullmatch(r"[A-Z][A-Z0-9_]*", variable) is None
                        or variable.startswith(("GITHUB_", "ACTIONS_", "RUNNER_", "CI_", "PYTHON", "NODE_", "DOTNET_"))
                        or variable in ("PATH", "HOME", "TMPDIR", "TMP", "TEMP", "BASH_ENV", "ENV",
                                        "LD_PRELOAD", "LD_LIBRARY_PATH", "SHELLOPTS", "BASHOPTS",
                                        "HEXALITH_JOB_NAME", "HEXALITH_JOB_PARAMETERS", "HEXALITH_JOB_NEEDS",
                                        "HEXALITH_QUALIFICATION_PARAMETERS", "HEXALITH_AUTHORITY_SHA256", "HEXALITH_SOURCE_SHA")
                        or not isinstance(secret, str) or re.fullmatch(r"[A-Z][A-Z0-9_]*", secret) is None):
                    fail(f"{location}/secrets", "expected safe environment names mapped to literal GitHub secret names")
        if "setup" in options:
            setup = options["setup"]
            closed(setup, frozenset(), frozenset(("dapr", "browsers", "registry", "aspire", "npmWorkspaces",
                                                 "dotnetTools", "prerequisites")), f"{location}/setup")
            if not linux:
                fail(f"{location}/setup", "environment overrides require Linux")
            for key in ("dapr", "browsers", "registry", "aspire", "dotnetTools"):
                if key in setup and type(setup[key]) is not bool:
                    fail(f"{location}/setup/{key}", "expected a boolean")
            if setup.get("aspire", document.get("aspire", False)) and not setup.get("dapr", document["dapr"]):
                fail(f"{location}/setup/aspire", "requires dapr=true")
            if "npmWorkspaces" in setup:
                array(setup["npmWorkspaces"], f"{location}/setup/npmWorkspaces")
                for index, item in enumerate(setup["npmWorkspaces"]):
                    path(item, f"{location}/setup/npmWorkspaces/{index}")
            if "prerequisites" in setup:
                prerequisites(setup["prerequisites"], f"{location}/setup/prerequisites")
    pending = set(jobs)
    while pending:
        ready = {job for job in pending if not (set(jobs[job].get("needs", [])) & pending)}
        if not ready:
            fail("$/jobs", "custom job dependencies contain a cycle")
        pending -= ready
    if (any("versions" in case for case in cases) or document.get("publishedBuilds")) and any(options["runner"] in ("windows-latest", "macos-latest") for options in jobs.values()):
        fail("$/jobs", "published preparation and buildProjects support Linux jobs only")
    for project, action in tests["exclude"].items():
        location = f"$/tests/exclude/{project}"
        path(project, location, ".csproj")
        name(action, location)
        if action not in document["hooks"] and action not in jobs:
            fail(location, f"{action!r} is not an enabled hook or job")
    validate_windows_checkout(document.get("windowsCheckout"), protected=(
        "global.json", ".gitmodules", ".github", "references/Hexalith.Builds",
        *([document["solution"]] if document["solution"] != "none" else []),
        *([document["moduleManifest"]] if document.get("moduleManifest") else []),
        *([document["publishedBuilds"]] if document.get("publishedBuilds") else []),
        *(case["project"] for case in cases),
        *document["submodules"], *tests["integration"], *tests["exclude"], *tests.get("additional", []),
    ), source=source)
    if document["solution"] == "none":
        if document.get("aspire"):
            fail("$/aspire", "must be false when solution is none")
        for key in ("moduleManifest", "publishedBuilds", "windowsCheckout"):
            if document.get(key) is not None:
                fail(f"$/{key}", "must be null when solution is none")
        if cases:
            fail("$/buildProjects", "must be empty when solution is none")
        for key in ("dapr", "browsers", "hooks", "jobs", "npmWorkspaces", "dotnetTools"):
            if document[key]:
                fail(f"$/{key}", "must be empty or false when solution is none")
        if tests["integration"] or tests["exclude"] or tests["minimumExpectedTests"] or tests["failSkips"]:
            fail("$/tests", "test collections must be empty and failSkips false when solution is none")
        if "additional" in tests or "reports" in tests or document.get("prerequisites"):
            fail("$/tests", "additional tests, reporting and prerequisites require a solution")
    return document


def validate_windows_checkout(value: Any, *, protected: tuple[str, ...] = (),
                              source: str = "windows-checkout") -> Any:
    """Validate the closed, immutable archive exclusion contract without defaults.

    This same function validates CI documents and the trusted Windows input.
    Object/tree bindings and product-path coverage require Git metadata at checkout.
    """
    def fail(message: str) -> None:
        raise ConfigError(f"{source}: $/windowsCheckout: {message}")

    if value is None:
        return value
    if not isinstance(value, dict) or set(value) != {"exclude"}:
        fail("expected only an exclude array")
    exclusions = value["exclude"]
    if not isinstance(exclusions, list) or not exclusions:
        fail("exclude must be a nonempty array")
    paths = []
    for entry in exclusions:
        if not isinstance(entry, dict) or set(entry) != {"path", "tree"}:
            fail("each exclusion requires only path and tree")
        path, tree = entry["path"], entry["tree"]
        if not isinstance(path, str) or not path or "${{" in path:
            fail("expected a safe repository-relative directory path")
        for part in path.split("/"):
            if (not part or part in (".", "..") or part.endswith((".", " "))
                    or any(char in part for char in '\\:<>"|?*')
                    or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in part)
                    or re.fullmatch(r"(?i)(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?", part)
                    or part.lower() == ".git"):
                fail(f"unsafe Windows directory path: {path!r}")
        if not isinstance(tree, str) or re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", tree) is None:
            fail("tree must be a full lowercase Git object ID")
        for other in [*paths, *protected]:
            first, second = path.casefold(), other.casefold()
            if first == second or first.startswith(second + "/") or second.startswith(first + "/"):
                fail(f"overlapping, aliased or protected exclusion: {path!r} and {other!r}")
        paths.append(path)
    return value


def load_config(path: str | Path, *, repository: str, module_root: str = "") -> dict[str, Any]:
    """Strictly read UTF-8 JSON, validate it with trusted context, and return it."""
    return validate_config(_read_json(Path(path)), repository=repository, module_root=module_root, source=str(path))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path, help="complete hexalith-ci.json document")
    parser.add_argument("--repository", required=True, help="trusted owner/repository context")
    parser.add_argument("--module-root", default="", help="trusted sample module root, if any")
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ConfigError("Python 3.12 or later is required")
        document = load_config(args.path, repository=args.repository, module_root=args.module_root)
        print(json.dumps(document, ensure_ascii=True, sort_keys=True))
        return 0
    except ConfigError as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
