"""AD-5 canonical JSON and the closed scalar conventions shared by split publication."""

from __future__ import annotations

import hashlib
import json
import re
from typing import Any


SAFE_INTEGER_MAX = 9007199254740991
VERSION_COMPONENT_MAX = 2147483647
ASSET_NAME_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$")
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
IDENTITY_PATTERN = re.compile(r"^github\.com/[a-z0-9._-]+/[a-z0-9._-]+$")
TIMESTAMP_PATTERN = re.compile(r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")
LOGIN_PATTERN = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")
PRERELEASE_IDENTIFIER = re.compile(r"^[0-9a-z-]+$")
NUMERIC_IDENTIFIER = re.compile(r"^[0-9]+$")


class ContractError(ValueError):
    """A fail-closed split-publication contract violation."""


def canonical_bytes(value: Any) -> bytes:
    """Return the AD-5 canonical UTF-8 bytes of a JSON value."""

    return json.dumps(
        value,
        ensure_ascii=True,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def canonical_digest(value: Any) -> str:
    """Return the SHA-256 hex digest of an AD-5 canonical value."""

    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def sha256_bytes(payload: bytes) -> str:
    """Return the SHA-256 hex digest of raw bytes."""

    return hashlib.sha256(payload).hexdigest()


def _reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    seen: set[str] = set()
    document: dict[str, Any] = {}
    for key, value in pairs:
        if key in seen:
            raise ContractError(f"duplicate JSON member {key!r}")
        seen.add(key)
        document[key] = value
    return document


def parse_json(payload: bytes, context: str, *, max_bytes: int) -> Any:
    """Parse one closed JSON document, rejecting duplicates and non-standard numbers."""

    if len(payload) > max_bytes:
        raise ContractError(f"{context} is {len(payload)} bytes, exceeding the {max_bytes}-byte ceiling")
    if payload.startswith(b"\xef\xbb\xbf"):
        raise ContractError(f"{context} must not start with a UTF-8 BOM")
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ContractError(f"{context} is not UTF-8") from error
    try:
        return json.loads(
            text,
            object_pairs_hook=_reject_duplicates,
            parse_constant=lambda value: (_ for _ in ()).throw(
                ContractError(f"{context} contains non-standard JSON number {value!r}")
            ),
        )
    except ContractError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
        raise ContractError(f"{context} is not duplicate-free JSON: {error}") from error


def expect_members(value: Any, members: set[str], context: str) -> dict[str, Any]:
    """Require a JSON object with exactly the named members."""

    if not isinstance(value, dict):
        raise ContractError(f"{context} must be a JSON object")
    if set(value) != members:
        raise ContractError(
            f"{context} members must be exactly {sorted(members)}, found {sorted(value)}"
        )
    return value


def expect_string(value: Any, context: str) -> str:
    """Require a JSON string."""

    if not isinstance(value, str):
        raise ContractError(f"{context} must be a string")
    return value


def expect_commit(value: Any, context: str) -> str:
    """Require a lowercase 40-hex Git SHA-1 identity."""

    text = expect_string(value, context)
    if COMMIT_PATTERN.fullmatch(text) is None or text == "0" * 40:
        raise ContractError(f"{context} must be a lowercase 40-hex commit")
    return text


def expect_sha256(value: Any, context: str) -> str:
    """Require a lowercase 64-hex SHA-256 digest."""

    text = expect_string(value, context)
    if SHA256_PATTERN.fullmatch(text) is None:
        raise ContractError(f"{context} must be a lowercase 64-hex SHA-256")
    return text


def expect_identity(value: Any, context: str) -> str:
    """Require a normalized github.com/owner/repository identity."""

    text = expect_string(value, context)
    if IDENTITY_PATTERN.fullmatch(text) is None:
        raise ContractError(f"{context} must be a normalized github.com/owner/repository identity")
    return text


def expect_timestamp(value: Any, context: str) -> str:
    """Require a canonical UTC timestamp with second precision."""

    text = expect_string(value, context)
    if TIMESTAMP_PATTERN.fullmatch(text) is None:
        raise ContractError(f"{context} must be a canonical UTC timestamp")
    month = int(text[5:7])
    day = int(text[8:10])
    hour = int(text[11:13])
    minute = int(text[14:16])
    second = int(text[17:19])
    if month < 1 or month > 12 or day < 1 or day > 31 or hour > 23 or minute > 59 or second > 59:
        raise ContractError(f"{context} is not a valid UTC timestamp")
    return text


def expect_bool(value: Any, context: str) -> bool:
    """Require a JSON boolean."""

    if not isinstance(value, bool):
        raise ContractError(f"{context} must be a JSON boolean")
    return value


def expect_safe_integer(value: Any, context: str, *, minimum: int = 0) -> int:
    """Require an interoperable JSON integer inside the declared inclusive range."""

    if isinstance(value, bool) or not isinstance(value, int):
        raise ContractError(f"{context} must be an integer")
    if value < minimum or value > SAFE_INTEGER_MAX:
        raise ContractError(f"{context} is outside {minimum}..{SAFE_INTEGER_MAX}")
    return value


def expect_path(value: Any, context: str, *, max_bytes: int = 240, max_depth: int = 8) -> str:
    """Require an ASCII relative POSIX path inside the archive ceilings."""

    text = expect_string(value, context)
    if not text.isascii() or "\\" in text or text.startswith("/") or "//" in text:
        raise ContractError(f"{context} must be an ASCII relative POSIX path")
    if len(text.encode("utf-8")) > max_bytes:
        raise ContractError(f"{context} exceeds {max_bytes} UTF-8 bytes")
    segments = text.split("/")
    if len(segments) > max_depth:
        raise ContractError(f"{context} exceeds path depth {max_depth}")
    for segment in segments:
        if segment in ("", ".", "..") or any(ord(character) < 32 or ord(character) == 127 for character in segment):
            raise ContractError(f"{context} contains an unsafe path segment")
    return text


def expect_asset_name(value: Any, context: str) -> str:
    """Require a GitHub Release asset name."""

    text = expect_string(value, context)
    if ASSET_NAME_PATTERN.fullmatch(text) is None:
        raise ContractError(f"{context} is not a destination asset name")
    return text


def expect_version(value: Any, context: str) -> str:
    """Require the canonical NuGet/SemVer subset used as a release version."""

    text = expect_string(value, context)
    if not text.isascii() or len(text.encode("ascii")) > 128 or "+" in text or text != text.strip():
        raise ContractError(f"{context} is not a canonical release version")
    version, separator, prerelease = text.partition("-")
    components = version.split(".")
    if separator == "" and prerelease:
        raise ContractError(f"{context} is not a canonical release version")
    if len(components) != 3:
        raise ContractError(f"{context} must contain exactly three numeric components")
    for component in components:
        if NUMERIC_IDENTIFIER.fullmatch(component) is None or (len(component) > 1 and component.startswith("0")):
            raise ContractError(f"{context} contains a non-canonical numeric component")
        if int(component) > VERSION_COMPONENT_MAX:
            raise ContractError(f"{context} contains a numeric component above {VERSION_COMPONENT_MAX}")
    if separator:
        identifiers = prerelease.split(".")
        if not identifiers or any(PRERELEASE_IDENTIFIER.fullmatch(identifier) is None for identifier in identifiers):
            raise ContractError(f"{context} contains an empty or non-lowercase prerelease identifier")
        for identifier in identifiers:
            if NUMERIC_IDENTIFIER.fullmatch(identifier) and len(identifier) > 1 and identifier.startswith("0"):
                raise ContractError(f"{context} contains a numeric prerelease identifier with a leading zero")
    return text


def expect_login(value: Any, context: str) -> str:
    """Require one normalized GitHub login spelling."""

    text = expect_string(value, context)
    if LOGIN_PATTERN.fullmatch(text) is None:
        raise ContractError(f"{context} is not a GitHub login")
    return text
