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
    closed(document, REQUIRED, frozenset(("buildsTag", "aspire", "job-timeout-minutes", "moduleManifest", "windowsCheckout", "additionalSdkVersion")), "$")
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
    closed(tests, frozenset(("integration", "exclude", "minimumExpectedTests", "failSkips")), frozenset(), "$/tests")
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
    jobs = document["jobs"]
    if not isinstance(jobs, dict):
        fail("$/jobs", "expected an object")
    for job, options in jobs.items():
        location = f"$/jobs/{job}"
        name(job, location)
        if job == "ci" or job in HOOKS:
            fail(location, "job name is reserved for CI or a fixed hook")
        if isinstance(options, dict) and options.get("trigger") == "manual":
            if job == "none":
                fail(location, "none is reserved for the manual dispatch default")
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
                        or secret.startswith(("GITHUB_", "ACTIONS_", "RUNNER_"))):
                    fail(f"{location}/secrets/{index}", "expected an explicit non-reserved uppercase secret name")
        else:
            closed(options, frozenset(("runner",)), frozenset(), location)
            if options["runner"] not in ("windows-latest", "macos-latest"):
                fail(f"{location}/runner", "expected windows-latest or macos-latest")
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
        *document["submodules"], *tests["integration"], *tests["exclude"],
    ), source=source)
    if document["solution"] == "none":
        if document.get("aspire"):
            fail("$/aspire", "must be false when solution is none")
        for key in ("moduleManifest", "windowsCheckout"):
            if document.get(key) is not None:
                fail(f"$/{key}", "must be null when solution is none")
        for key in ("dapr", "browsers", "hooks", "jobs", "npmWorkspaces", "dotnetTools"):
            if document[key]:
                fail(f"$/{key}", "must be empty or false when solution is none")
        if tests["integration"] or tests["exclude"] or tests["minimumExpectedTests"] or tests["failSkips"]:
            fail("$/tests", "test collections must be empty and failSkips false when solution is none")
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
