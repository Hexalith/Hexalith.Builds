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
CONFIG = "application/vnd.oci.image.config.v1+json"
LAYERS = frozenset("application/vnd.oci.image.layer." + kind + suffix
                   for kind in ("v1.tar", "nondistributable.v1.tar")
                   for suffix in ("", "+gzip", "+zstd")) | frozenset({
    # SDK OCI manifests retain these compatible Docker base-layer descriptors.
    "application/vnd.docker.image.rootfs.diff.tar.gzip",
    "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip",
})
PLATFORM_FIELDS = ("os", "architecture", "variant", "os.version", "os.features")
MAX_EXTENDED_METADATA_SIZE = 2 ** 20
MAX_EXTENDED_METADATA_TOTAL = 8 * 2 ** 20


class OciError(ValueError):
    """An archive contains an unsafe member, inconsistent identity or broken graph."""


class BoundedTarInfo(tarfile.TarInfo):
    """Limit hidden metadata before tarfile allocates its extended payload."""

    def _proc_member(self, archive):
        if self.type in (tarfile.XHDTYPE, tarfile.XGLTYPE, tarfile.SOLARIS_XHDTYPE,
                         tarfile.GNUTYPE_LONGNAME, tarfile.GNUTYPE_LONGLINK):
            payload = self._block(self.size)
            total = getattr(archive, "_oci_metadata_size", 0) + tarfile.BLOCKSIZE + payload
            if (self.size < 0 or payload > MAX_EXTENDED_METADATA_SIZE
                    or total > MAX_EXTENDED_METADATA_TOTAL):
                raise OciError("oversized OCI extended metadata")
            archive._oci_metadata_size = total
        elif self.type == tarfile.GNUTYPE_SPARSE:
            raise OciError("sparse OCI archive member")
        return super()._proc_member(archive)

    def _proc_gnusparse_00(self, *args):
        raise OciError("sparse OCI archive member")

    _proc_gnusparse_01 = _proc_gnusparse_00
    _proc_gnusparse_10 = _proc_gnusparse_00


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
        with tarfile.open(mode="r:*", tarinfo=BoundedTarInfo, **options) as archive:
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
    except (tarfile.TarError, OSError, EOFError, RecursionError) as error:
        raise OciError("invalid OCI archive") from error


def validate_platform(platform):
    if (not isinstance(platform, dict)
            or any(not isinstance(platform.get(key), str) or not platform[key]
                   for key in ("os", "architecture"))):
        raise OciError("missing OCI platform")
    for key in ("variant", "os.version"):
        if key in platform and (not isinstance(platform[key], str) or not platform[key]):
            raise OciError("invalid OCI platform metadata")
    if "os.features" in platform and (not isinstance(platform["os.features"], list)
            or any(not isinstance(value, str) or not value for value in platform["os.features"])):
        raise OciError("invalid OCI platform metadata")


def image_platform(config, descriptor, *, single=False):
    """Keep descriptor metadata and check every field shared with image config."""
    platform = {key: config[key] for key in PLATFORM_FIELDS if key in config}
    validate_platform(platform)
    if "platform" in descriptor:
        supplied = descriptor["platform"]
        if single and supplied is None:
            return platform
        if single and isinstance(supplied, dict):
            # SDK single-image roots can serialize incomplete/null platform
            # fields. The image config supplies those absent values.
            supplied = {key: value for key, value in supplied.items()
                        if key not in PLATFORM_FIELDS or value is not None}
            validate_platform({**platform, **supplied})
        else:
            validate_platform(supplied)
        if any(supplied[key] != platform[key] for key in PLATFORM_FIELDS
               if key in supplied and key in platform):
            raise OciError("OCI configuration platform mismatch")
        platform.update(supplied)
    return platform


def platform_identity(platform):
    return platform["os"] + "/" + platform["architecture"] + (
        "/" + platform["variant"] if platform.get("variant") else "")


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
        if media and descriptor.get("mediaType") not in ({media} if isinstance(media, str) else media):
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
            config = document(blob(manifest["config"], CONFIG))
            if not single and "platform" not in child:
                raise OciError("missing OCI platform")
            platform = image_platform(config, child, single=single)
            observed.append(platform_identity(platform))
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
                blob(layer, LAYERS)
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
    for path, selected in archives:
        validate_platform(selected)
        platform_files = archive_files(path)
        # SDK single-platform descriptors may omit platform; config is authority.
        inspect_files(platform_files, revision=revision, version=version,
                      repository=repository, single=True)
        descriptor, = document(platform_files["index.json"])["manifests"]
        manifest = document(platform_files["blobs/sha256/" + descriptor["digest"][7:]])
        config = document(platform_files["blobs/sha256/" + manifest["config"]["digest"][7:]])
        platform = image_platform(config, descriptor, single=True)
        if any(platform[key] != value for key, value in selected.items() if key in platform):
            raise OciError("SDK archive differs from selected platform")
        platform = {**selected, **platform}
        children.append({**descriptor, "platform": platform})
        platforms.append(platform_identity(platform))
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
