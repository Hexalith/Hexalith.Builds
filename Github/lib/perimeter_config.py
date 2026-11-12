#!/usr/bin/env python3
"""Strict, dependency-free reader for the perimeter contract beside this code."""

from __future__ import annotations

import argparse
from functools import lru_cache
import json
from pathlib import Path
import re
import sys
from typing import Any


SCHEMA_PATH = Path(__file__).resolve().parents[2] / "schemas/hexalith.perimeter.v1.json"
FIELDS = {
    "whole": {"path"}, "whole-dir": {"path"},
    "gitlink": {"path", "buildsTag"},
    "gitmodules-section": {"path", "submodulePath", "url"},
    "json-superset": {"path", "pointer", "values"},
    "marker-block": {"path", "text"}, "absent-glob": {"pattern"},
    "absent-json": {"path", "selectors"}, "absent-line": {"path", "pattern"},
}
STABLE_TAG = re.compile(r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
BEGIN = "<!-- hexalith-cicd:begin -->"
END = "<!-- hexalith-cicd:end -->"


class ConfigError(ValueError):
    """An invalid perimeter, with single-line escaped diagnostics."""

    def __init__(self, message: str):
        super().__init__("".join(
            f"\\u{ord(char):04x}" if ord(char) < 32 or 127 <= ord(char) <= 159 or 0xD800 <= ord(char) <= 0xDFFF
            or char in "\u2028\u2029" else char for char in message))


def _object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ConfigError(f"duplicate key {key!r}")
        result[key] = value
    return result


def _constant(value: str) -> Any:
    raise ConfigError(f"non-JSON constant {value!r}")


def read_json(data: bytes | str, *, source: str) -> Any:
    """Decode strict UTF-8 JSON, including duplicate-key/constant checks."""
    try:
        if isinstance(data, bytes):
            data = data.decode("utf-8")
        return json.loads(data, object_pairs_hook=_object, parse_constant=_constant)
    except (UnicodeError, ValueError, RecursionError) as error:
        raise ConfigError(f"{source}: {error}") from error


@lru_cache(maxsize=1)
def schema_identity() -> str:
    """Derive identity from this trusted checkout, independently of cwd."""
    try:
        schema = read_json(SCHEMA_PATH.read_bytes(), source=str(SCHEMA_PATH))
        identity = schema["properties"]["schema"]["const"]
        if not isinstance(identity, str) or not identity:
            raise ValueError("missing schema identity")
        return identity
    except (OSError, KeyError, TypeError, ValueError) as error:
        raise ConfigError(f"{SCHEMA_PATH}: {error}") from error


def repository_path(value: Any, *, pattern: bool = False) -> bool:
    """Whether a value is a safe root-relative Git path (or wildmatch)."""
    return (isinstance(value, str) and bool(value)
            and not value.startswith("/") and re.match(r"[A-Za-z]:", value) is None
            and "\\" not in value and "${{" not in value
            and not any(part in ("", ".", "..") for part in value.split("/"))
            and not any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in value)
            and (pattern or not any(char in value for char in "*?[]")))


def pointer_parts(pointer: str) -> list[str]:
    """Decode a validated RFC 6901 pointer; empty means the document root."""
    return [] if pointer == "" else [part.replace("~1", "/").replace("~0", "~")
                                      for part in pointer[1:].split("/")]


def validate_config(document: Any, *, source: str = "perimeter.json") -> dict[str, Any]:
    """Validate the closed wire contract without defaults or changes to values.

    Ownership conflicts depend on entry semantics and Git membership and are
    checked by the engine before comparison/application.
    """
    def fail(location: str, message: str) -> None:
        raise ConfigError(f"{source}: {location}: {message}")

    def closed(value: Any, keys: set[str], location: str) -> None:
        if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
            fail(location, "expected an object with string keys")
        if keys - value.keys():
            fail(location, f"missing required keys: {', '.join(sorted(keys - value.keys()))}")
        if value.keys() - keys:
            fail(location, f"unknown keys: {', '.join(sorted(value.keys() - keys))}")

    def string(value: Any, location: str, *, nonempty: bool = False) -> None:
        if not isinstance(value, str) or (nonempty and not value):
            fail(location, "expected a string" + (" that is not empty" if nonempty else ""))
        # JSON permits escaped surrogates; they cannot be encoded as UTF-8 Git content.
        if any(0xD800 <= ord(char) <= 0xDFFF for char in value):
            fail(location, "unpaired Unicode surrogate")

    if not isinstance(document, dict):
        fail("$", "expected an object")
    if "schema" in document and document["schema"] != schema_identity():
        raise ConfigError(f"{source}: unsupported schema {document['schema']}. CI/CD is owned by Hexalith.CiCd.")
    closed(document, {"schema", "entries"}, "$")
    if not isinstance(document["entries"], list):
        fail("$/entries", "expected an array")
    for index, entry in enumerate(document["entries"]):
        location = f"$/entries/{index}"
        if not isinstance(entry, dict):
            fail(location, "expected an object")
        if "kind" in entry and (not isinstance(entry["kind"], str) or entry["kind"] not in FIELDS):
            raise ConfigError(f"{source}: unsupported perimeter entry kind {entry['kind']}")
        if "kind" not in entry:
            fail(location, "missing required keys: kind")
        kind = entry["kind"]
        closed(entry, {"kind"} | FIELDS[kind], location)
        for key in FIELDS[kind] - {"values", "selectors"}:
            string(entry[key], f"{location}/{key}")
        for key in ("path", "submodulePath"):
            if key in entry and not repository_path(entry[key]):
                fail(f"{location}/{key}", "unsafe repository-relative path")
        if kind == "absent-glob" and not repository_path(entry["pattern"], pattern=True):
            fail(f"{location}/pattern", "unsafe repository-relative wildmatch")
        if kind == "gitlink" and STABLE_TAG.fullmatch(entry["buildsTag"]) is None:
            fail(f"{location}/buildsTag", "expected an exact stable vMAJOR.MINOR.PATCH release tag")
        if kind == "gitmodules-section":
            string(entry["url"], f"{location}/url", nonempty=True)
            if any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in entry["url"]):
                fail(f"{location}/url", "control character in URL")
        if kind == "json-superset":
            if entry["pointer"] and (not entry["pointer"].startswith("/")
                                      or re.search(r"~(?![01])", entry["pointer"])):
                fail(f"{location}/pointer", "expected an RFC 6901 pointer")
            values = entry["values"]
            if not isinstance(values, list):
                fail(f"{location}/values", "expected an array of unique strings")
            for offset, value in enumerate(values):
                string(value, f"{location}/values/{offset}")
                if value in values[:offset]:
                    fail(f"{location}/values/{offset}", "duplicate item")
        if kind == "marker-block" and (BEGIN in entry["text"] or END in entry["text"]):
            fail(f"{location}/text", "block body contains an ownership marker")
        if kind == "absent-json":
            selectors = entry["selectors"]
            if not isinstance(selectors, list) or not selectors:
                fail(f"{location}/selectors", "expected nonempty selector arrays")
            for offset, selector in enumerate(selectors):
                if not isinstance(selector, list) or not selector:
                    fail(f"{location}/selectors/{offset}", "expected a nonempty array of key components")
                for component in selector:
                    string(component, f"{location}/selectors/{offset}")
        if kind == "absent-line":
            try:
                re.compile(entry["pattern"])
            except re.error as error:
                fail(f"{location}/pattern", f"invalid Python regex: {error}")
    return document


def load_bytes(data: bytes, *, source: str = "perimeter.json") -> dict[str, Any]:
    """Load a Git blob without consulting a module's working tree."""
    return validate_config(read_json(data, source=source), source=source)


def load_config(path: str | Path) -> dict[str, Any]:
    """Load one strict UTF-8 perimeter file."""
    try:
        data = Path(path).read_bytes()
    except OSError as error:
        raise ConfigError(f"{path}: {error}") from error
    return load_bytes(data, source=str(path))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ConfigError("Python 3.12 or later is required")
        print(json.dumps(load_config(args.path), ensure_ascii=True, sort_keys=True))
        return 0
    except ConfigError as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
