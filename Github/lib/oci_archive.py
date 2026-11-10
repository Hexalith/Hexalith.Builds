#!/usr/bin/env python3
"""Validate SDK OCI graphs and assemble an index without rewriting image blobs."""
from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import re
import tarfile

DIGEST = re.compile(r"sha256:[0-9a-f]{64}\Z")
INDEX = "application/vnd.oci.image.index.v1+json"
MANIFEST = "application/vnd.oci.image.manifest.v1+json"


class OciError(ValueError):
    """An archive contains an unsafe member, inconsistent identity or broken graph."""


def sha256(data: bytes) -> str:
    return "sha256:" + hashlib.sha256(data).hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise OciError("duplicate OCI JSON key")
        result[key] = value
    return result


def document(data):
    try:
        value = json.loads(data, object_pairs_hook=unique_object)
        if not isinstance(value, dict):
            raise OciError("OCI document must be an object")
        return value
    except (UnicodeError, ValueError, RecursionError) as error:
        raise OciError("invalid OCI JSON") from error


def archive_files(source: Path | io.BytesIO) -> dict[str, bytes]:
    """Read regular members as data only, including SDK's gzip intermediates."""
    files = {}
    try:
        options = {"fileobj": source} if hasattr(source, "read") else {"name": source}
        with tarfile.open(mode="r:*", **options) as archive:
            total = 0
            for member in archive:
                name = member.name.removeprefix("./")
                if (not name or name.startswith("/") or "\\" in name
                        or any(part in ("", ".", "..") for part in name.split("/"))):
                    raise OciError("unsafe OCI archive path")
                if member.isdir():
                    continue
                total += member.size
                if (not member.isfile() or name in files or member.size > 2 ** 31
                        or total > 8 * 2 ** 30):
                    raise OciError("unsafe, duplicate or oversized OCI member")
                files[name] = archive.extractfile(member).read()
        return files
    except (tarfile.TarError, OSError, EOFError) as error:
        raise OciError("invalid OCI archive") from error


def inspect_files(files: dict[str, bytes], *, revision: str, version: str,
                  repository: str = "", name: str = "", platforms: list[str] | None = None,
                  single: bool = False) -> dict:
    """Verify every descriptor, platform, config label and referenced blob."""
    used = {"oci-layout", "index.json"}
    blobs = {}

    def blob(descriptor, media=None):
        if not isinstance(descriptor, dict) or not DIGEST.fullmatch(str(descriptor.get("digest", ""))):
            raise OciError("invalid OCI descriptor digest")
        digest = descriptor["digest"]
        if media and descriptor.get("mediaType") != media:
            raise OciError("unexpected OCI descriptor media type")
        path = "blobs/sha256/" + digest[7:]
        data = files[path]
        if type(descriptor.get("size")) is not int or descriptor["size"] != len(data) or sha256(data) != digest:
            raise OciError("OCI blob digest/size mismatch")
        used.add(path)
        blobs[digest] = len(data)
        return data

    try:
        if document(files["oci-layout"]) != {"imageLayoutVersion": "1.0.0"}:
            raise OciError("unsupported OCI layout")
        layout = document(files["index.json"])
        if layout.get("schemaVersion") != 2 or len(layout["manifests"]) != 1:
            raise OciError("expected exactly one OCI root")
        root, = layout["manifests"]
        if single:
            children = [root]
        else:
            if name and root.get("annotations", {}).get("org.opencontainers.image.ref.name") != name + ":" + version:
                raise OciError("container repository/version annotation mismatch")
            index = document(blob(root, INDEX))
            if index.get("schemaVersion") != 2 or index.get("mediaType") != INDEX:
                raise OciError("invalid OCI root index")
            children = index["manifests"]
        if not isinstance(children, list) or not children:
            raise OciError("empty OCI platform inventory")
        observed, manifests = [], []
        for child in children:
            manifest = document(blob(child, MANIFEST))
            if manifest.get("schemaVersion") != 2 or manifest.get("mediaType") != MANIFEST:
                raise OciError("invalid OCI child manifest")
            config = document(blob(manifest["config"]))
            platform = child.get("platform", {}) if not single else {
                key: config[key] for key in ("os", "architecture")}
            if any(not isinstance(platform.get(key), str) or not platform[key] for key in ("os", "architecture")):
                raise OciError("missing OCI platform")
            if config.get("os") != platform["os"] or config.get("architecture") != platform["architecture"]:
                raise OciError("OCI configuration platform mismatch")
            variant = platform.get("variant", "")
            if variant and config.get("variant", variant) != variant:
                raise OciError("OCI configuration variant mismatch")
            identity = platform["os"] + "/" + platform["architecture"] + ("/" + variant if variant else "")
            observed.append(identity)
            manifests.append(child["digest"])
            labels = config["config"]["Labels"]
            expected = {"org.opencontainers.image.revision": revision, "org.opencontainers.image.version": version}
            if repository:
                expected["org.opencontainers.image.source"] = "https://github.com/" + repository
            if not isinstance(labels, dict) or any(labels.get(key) != value for key, value in expected.items()):
                raise OciError("OCI source/revision/version label mismatch")
            if not isinstance(manifest["layers"], list):
                raise OciError("OCI layer list missing")
            for layer in manifest["layers"]:
                blob(layer)
        if len(observed) != len(set(observed)) or len(manifests) != len(set(manifests)):
            raise OciError("duplicate OCI platform or manifest")
        if platforms is not None and sorted(platforms) != sorted(observed):
            raise OciError("OCI platform inventory mismatch")
        if set(files) != used:
            raise OciError("unexpected or unreferenced OCI payload")
        return {"root": root["digest"], "children": manifests, "platforms": sorted(observed), "blobs": blobs}
    except (KeyError, TypeError, IndexError) as error:
        raise OciError("incomplete OCI graph") from error


def inspect_archive(source: Path | io.BytesIO, *, revision: str, version: str,
                    repository: str = "", name: str = "", platforms: list[str] | None = None) -> dict:
    return inspect_files(archive_files(source), revision=revision, version=version,
                         repository=repository, name=name, platforms=platforms)


def combine_archives(archives: list[tuple[Path, dict]], destination: Path, *,
                     revision: str, version: str, repository: str, name: str) -> dict:
    """Wrap unchanged single-platform manifests/configs/layers in one new index."""
    files, children, platforms = {}, [], []
    for path, platform in archives:
        platform_files = archive_files(path)
        expected = platform["os"] + "/" + platform["architecture"]
        if platform.get("variant"):
            expected += "/" + platform["variant"]
        # SDK single-platform descriptors may omit platform; config is authority.
        graph = inspect_files(platform_files, revision=revision, version=version,
                              repository=repository, single=True)
        if graph["platforms"][0].split("/")[:2] != expected.split("/")[:2]:
            raise OciError("SDK archive differs from selected platform")
        descriptor, = document(platform_files["index.json"])["manifests"]
        children.append({**descriptor, "platform": platform})
        platforms.append(expected)
        for member, data in platform_files.items():
            if member.startswith("blobs/"):
                if member in files and files[member] != data:
                    raise OciError("conflicting immutable OCI blob")
                files[member] = data
    index = json.dumps({"schemaVersion": 2, "mediaType": INDEX, "manifests": children},
                       separators=(",", ":")).encode()
    digest = sha256(index)
    files["blobs/sha256/" + digest[7:]] = index
    files["oci-layout"] = b'{"imageLayoutVersion":"1.0.0"}'
    files["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [{
        "mediaType": INDEX, "digest": digest, "size": len(index),
        "annotations": {"org.opencontainers.image.ref.name": name + ":" + version}}]},
        separators=(",", ":")).encode()
    graph = inspect_files(files, revision=revision, version=version, repository=repository,
                          name=name, platforms=platforms)
    with tarfile.open(destination, "w", format=tarfile.USTAR_FORMAT) as archive:
        for member, data in sorted(files.items()):
            entry = tarfile.TarInfo(member)
            entry.size = len(data)
            archive.addfile(entry, io.BytesIO(data))
    return graph
