"""Public signed-package byte inventory. Pure stdlib; never executes package content."""
from __future__ import annotations

import hashlib
import io
import json
import re
import zipfile

ASSET_NAME = "hexalith-package-publication.v1.json"
SCHEMA = "hexalith.package-publication.v1"
MAX_DOCUMENT = 2 * 1024 * 1024
MAX_ARCHIVE = 2 * 1024 ** 3
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
PACKAGE_ID = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\Z")
VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\Z")


class PublicationError(ValueError):
    """Untrusted publication data failed the closed public contract."""


def safe_path(name):
    return (isinstance(name, str) and bool(name) and not name.startswith("/")
            and not any(char in name for char in ("\\", ":"))
            and all(part not in ("", ".", "..") for part in name.split("/"))
            and not any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in name))


def payload_map(data):
    """Hash every regular ZIP payload except the single NuGet signature entry."""
    if not isinstance(data, bytes) or len(data) > MAX_ARCHIVE:
        raise PublicationError("package archive exceeds size limit")
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            seen, result, total = set(), {}, 0
            for entry in archive.infolist():
                name = entry.filename.removesuffix("/") if entry.is_dir() else entry.filename
                if not safe_path(name) or name.casefold() in seen:
                    raise PublicationError("unsafe or duplicate package member")
                seen.add(name.casefold())
                mode = (entry.external_attr >> 16) & 0o170000
                if mode not in (0, 0o040000 if entry.is_dir() else 0o100000) or entry.flag_bits & 1:
                    raise PublicationError("nonregular or encrypted package member")
                total += entry.file_size
                if entry.file_size > MAX_ARCHIVE or total > MAX_ARCHIVE:
                    raise PublicationError("package payload exceeds size limit")
                if not entry.is_dir() and name != ".signature.p7s":
                    result[name] = hashlib.sha256(archive.read(entry)).hexdigest()
            return dict(sorted(result.items()))
    except (zipfile.BadZipFile, OSError, RuntimeError, NotImplementedError) as error:
        raise PublicationError("invalid package archive") from error


def validate(document):
    fields = {"schema", "repository", "sha", "version", "artifactRun", "artifactId", "artifactDigest", "packages"}
    if not isinstance(document, dict) or set(document) != fields or document["schema"] != SCHEMA:
        raise PublicationError("invalid package publication fields/schema")
    for key, pattern in (("repository", r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+"),
                         ("sha", r"[0-9a-f]{40}"), ("artifactDigest", r"sha256:[0-9a-f]{64}")):
        if not isinstance(document[key], str) or re.fullmatch(pattern, document[key]) is None:
            raise PublicationError("invalid package publication " + key)
    if not isinstance(document["version"], str) or VERSION.fullmatch(document["version"]) is None:
        raise PublicationError("invalid package publication version")
    if any(type(document[key]) is not int or document[key] < 1 for key in ("artifactRun", "artifactId")):
        raise PublicationError("invalid original package artifact identity")
    if not isinstance(document["packages"], list):
        raise PublicationError("publication packages must be an array")
    identities = []
    for package in document["packages"]:
        if (not isinstance(package, dict) or set(package) != {"id", "archiveSha256", "files"}
                or not isinstance(package["id"], str) or PACKAGE_ID.fullmatch(package["id"]) is None
                or not isinstance(package["archiveSha256"], str) or SHA256.fullmatch(package["archiveSha256"]) is None
                or not isinstance(package["files"], dict) or not package["files"]):
            raise PublicationError("invalid package publication record")
        identities.append(package["id"])
        paths = set()
        for name, digest in package["files"].items():
            if (not safe_path(name) or name == ".signature.p7s" or name.casefold() in paths
                    or not isinstance(digest, str) or SHA256.fullmatch(digest) is None):
                raise PublicationError("invalid package publication payload map")
            paths.add(name.casefold())
    if identities != sorted(identities) or len({name.casefold() for name in identities}) != len(identities):
        raise PublicationError("publication package IDs must be sorted and unique")
    return document


def read(data):
    if not isinstance(data, bytes) or len(data) > MAX_DOCUMENT:
        raise PublicationError("package publication exceeds size limit")
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise PublicationError("duplicate package publication key")
            result[key] = value
        return result
    try:
        document = json.loads(data, object_pairs_hook=unique,
            parse_constant=lambda _value: (_ for _ in ()).throw(PublicationError("invalid JSON constant")))
        return validate(document)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise PublicationError("invalid package publication document: " + str(error)) from error


def serialize(document):
    try:
        data = (json.dumps(validate(document), sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode()
    except (ValueError, UnicodeError, RecursionError) as error:
        raise PublicationError("invalid package publication document") from error
    if len(data) > MAX_DOCUMENT:
        raise PublicationError("package publication exceeds size limit")
    return data


def record(package_id, data, original_payload):
    files = payload_map(data)
    if files != original_payload:
        raise PublicationError("published package payload differs from original artifact: " + package_id)
    return {"id": package_id, "archiveSha256": hashlib.sha256(data).hexdigest(), "files": files}
