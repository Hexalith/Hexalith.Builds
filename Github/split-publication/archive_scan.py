"""AD-7 publication-candidate ZIP scanner.

The scanner measures the raw archive and central directory before it inflates any
member. Candidate bytes are then materialized as regular files and never executed.
"""

from __future__ import annotations

import os
import stat
import struct
import zlib
from pathlib import Path

from canonical import ContractError, expect_path


RAW_LIMIT = 512 * 1024 * 1024
MAX_MEMBERS = 256
MAX_MEMBER_UNCOMPRESSED = 128 * 1024 * 1024
MAX_TOTAL_UNCOMPRESSED = 1024 * 1024 * 1024
MAX_RATIO = 100
MAX_PATH_DEPTH = 8
MAX_PATH_BYTES = 240

_EOCD = b"PK\x05\x06"
_ZIP64_LOCATOR = b"PK\x06\x07"
_ZIP64_EOCD = b"PK\x06\x06"
_CENTRAL = b"PK\x01\x02"
_LOCAL = b"PK\x03\x04"
_DESCRIPTOR = b"PK\x07\x08"


class ArchiveMember:
    """One authenticated regular member of a publication-candidate archive."""

    def __init__(self, name: str, payload: bytes, crc32: int) -> None:
        self.name = name
        self.payload = payload
        self.crc32 = crc32


def _ratio_exceeds(uncompressed: int, compressed: int) -> bool:
    return uncompressed > MAX_RATIO * max(compressed, 1)


def _u16(payload: bytes, offset: int) -> int:
    return struct.unpack_from("<H", payload, offset)[0]


def _u32(payload: bytes, offset: int) -> int:
    return struct.unpack_from("<I", payload, offset)[0]


def _u64(payload: bytes, offset: int) -> int:
    return struct.unpack_from("<Q", payload, offset)[0]


def _find_eocd(payload: bytes) -> int:
    start = max(0, len(payload) - (65535 + 22))
    position = payload.rfind(_EOCD, start)
    if position < 0 or len(payload) - position < 22:
        raise ContractError("publication archive has no end-of-central-directory record")
    return position


def _zip64_extra(extra: bytes, compressed: int, uncompressed: int, offset: int) -> tuple[int, int, int]:
    cursor = 0
    found = False
    while cursor + 4 <= len(extra):
        header_id = _u16(extra, cursor)
        size = _u16(extra, cursor + 2)
        cursor += 4
        if cursor + size > len(extra):
            raise ContractError("publication archive ZIP64 extra field is truncated")
        if header_id == 0x0001:
            if found:
                raise ContractError("publication archive contains duplicate ZIP64 extra fields")
            found = True
            field = extra[cursor:cursor + size]
            field_cursor = 0
            if uncompressed == 0xFFFFFFFF:
                if field_cursor + 8 > len(field):
                    raise ContractError("publication archive ZIP64 uncompressed size is missing")
                uncompressed = _u64(field, field_cursor)
                field_cursor += 8
            if compressed == 0xFFFFFFFF:
                if field_cursor + 8 > len(field):
                    raise ContractError("publication archive ZIP64 compressed size is missing")
                compressed = _u64(field, field_cursor)
                field_cursor += 8
            if offset == 0xFFFFFFFF:
                if field_cursor + 8 > len(field):
                    raise ContractError("publication archive ZIP64 local header offset is missing")
                offset = _u64(field, field_cursor)
                field_cursor += 8
            if field_cursor != len(field):
                raise ContractError("publication archive ZIP64 extra field is inconsistent")
        cursor += size
    return compressed, uncompressed, offset


def _central_members(payload: bytes) -> list[dict[str, object]]:
    eocd = _find_eocd(payload)
    if _u16(payload, eocd + 4) != 0 or _u16(payload, eocd + 6) != 0:
        raise ContractError("publication archive spans multiple disks")
    member_count = _u16(payload, eocd + 8)
    declared_count = _u16(payload, eocd + 10)
    directory_size = _u32(payload, eocd + 12)
    directory_offset = _u32(payload, eocd + 16)
    if member_count == 0xFFFF or declared_count == 0xFFFF or directory_size == 0xFFFFFFFF or directory_offset == 0xFFFFFFFF:
        locator = eocd - 20
        if locator < 0 or payload[locator:locator + 4] != _ZIP64_LOCATOR:
            raise ContractError("publication archive ZIP64 end record is inconsistent")
        if _u32(payload, locator + 4) != 0:
            raise ContractError("publication archive spans multiple disks")
        zip64_offset = _u64(payload, locator + 8)
        if payload[zip64_offset:zip64_offset + 4] != _ZIP64_EOCD:
            raise ContractError("publication archive ZIP64 end record is missing")
        member_count = _u64(payload, zip64_offset + 32)
        declared_count = _u64(payload, zip64_offset + 40)
        directory_size = _u64(payload, zip64_offset + 40)
        directory_offset = _u64(payload, zip64_offset + 48)
        directory_size = _u64(payload, zip64_offset + 40)
        # The record layout is: size(8) at +4, then versions, counts, directory size, offset.
        member_count = _u64(payload, zip64_offset + 24)
        declared_count = _u64(payload, zip64_offset + 32)
        directory_size = _u64(payload, zip64_offset + 40)
        directory_offset = _u64(payload, zip64_offset + 48)
    if member_count != declared_count:
        raise ContractError("publication archive central-directory count is inconsistent")
    if member_count > MAX_MEMBERS:
        raise ContractError(f"publication archive contains {member_count} members, exceeding {MAX_MEMBERS}")
    if directory_offset + directory_size > len(payload):
        raise ContractError("publication archive central directory exceeds the raw archive")
    members: list[dict[str, object]] = []
    cursor = directory_offset
    end = directory_offset + directory_size
    for _ in range(member_count):
        if cursor + 46 > end or payload[cursor:cursor + 4] != _CENTRAL:
            raise ContractError("publication archive central directory is truncated or overlapped")
        flags = _u16(payload, cursor + 8)
        method = _u16(payload, cursor + 10)
        crc = _u32(payload, cursor + 16)
        compressed = _u32(payload, cursor + 20)
        uncompressed = _u32(payload, cursor + 24)
        name_length = _u16(payload, cursor + 28)
        extra_length = _u16(payload, cursor + 30)
        comment_length = _u16(payload, cursor + 32)
        if _u16(payload, cursor + 34) != 0:
            raise ContractError("publication archive spans multiple disks")
        external_attr = _u32(payload, cursor + 38)
        local_offset = _u32(payload, cursor + 42)
        cursor += 46
        if cursor + name_length + extra_length + comment_length > end:
            raise ContractError("publication archive central-directory entry is truncated")
        name_bytes = payload[cursor:cursor + name_length]
        extra = payload[cursor + name_length:cursor + name_length + extra_length]
        cursor += name_length + extra_length + comment_length
        compressed, uncompressed, local_offset = _zip64_extra(extra, compressed, uncompressed, local_offset)
        if flags & 0x1:
            raise ContractError("publication archive contains an encrypted member")
        if flags & 0x20:
            raise ContractError("publication archive contains a patched member")
        if method not in (0, 8):
            raise ContractError(f"publication archive compression method {method} is not a regular deflate or store")
        try:
            name = name_bytes.decode("utf-8")
        except UnicodeDecodeError as error:
            raise ContractError("publication archive member name is not UTF-8") from error
        mode = (external_attr >> 16) & 0xFFFF
        file_type = stat.S_IFMT(mode)
        if name.endswith("/") or file_type not in (0, stat.S_IFREG):
            raise ContractError(f"publication archive member {name!r} is not a regular file")
        expect_path(name, f"publication archive member {name!r}", max_bytes=MAX_PATH_BYTES, max_depth=MAX_PATH_DEPTH)
        if uncompressed > MAX_MEMBER_UNCOMPRESSED or _ratio_exceeds(uncompressed, compressed):
            raise ContractError(f"publication archive member {name!r} exceeds the size or compression ceiling")
        members.append(
            {
                "name": name,
                "flags": flags,
                "method": method,
                "crc": crc,
                "compressed": compressed,
                "uncompressed": uncompressed,
                "offset": local_offset,
            }
        )
    if cursor != end:
        raise ContractError("publication archive central directory contains undeclared bytes")
    return members


def _inflate(member: dict[str, object], compressed: bytes) -> bytes:
    expected = int(member["uncompressed"])
    if int(member["method"]) == 0:
        if len(compressed) != expected:
            raise ContractError(f"publication archive member {member['name']!r} stored size does not match")
        return compressed
    inflater = zlib.decompressobj(-15)
    try:
        payload = inflater.decompress(compressed, expected + 1)
        payload += inflater.flush()
    except zlib.error as error:
        raise ContractError(f"publication archive member {member['name']!r} could not be inflated") from error
    if len(payload) != expected or inflater.unused_data or not inflater.eof:
        raise ContractError(f"publication archive member {member['name']!r} inflated size does not match")
    return payload


def _read_member(payload: bytes, member: dict[str, object]) -> tuple[int, int, bytes]:
    offset = int(member["offset"])
    if offset + 30 > len(payload) or payload[offset:offset + 4] != _LOCAL:
        raise ContractError(f"publication archive member {member['name']!r} local header is missing")
    flags = _u16(payload, offset + 6)
    method = _u16(payload, offset + 8)
    crc = _u32(payload, offset + 14)
    compressed_size = _u32(payload, offset + 18)
    uncompressed_size = _u32(payload, offset + 22)
    name_length = _u16(payload, offset + 26)
    extra_length = _u16(payload, offset + 28)
    header_end = offset + 30 + name_length + extra_length
    if header_end > len(payload):
        raise ContractError(f"publication archive member {member['name']!r} local header is truncated")
    name = payload[offset + 30:offset + 30 + name_length].decode("utf-8")
    if name != member["name"] or method != member["method"]:
        raise ContractError(f"publication archive member {member['name']!r} local header disagrees")
    extra = payload[offset + 30 + name_length:header_end]
    if flags & 0x8:
        data_start = header_end
        data_end = data_start + int(member["compressed"])
        descriptor = data_end
        descriptor_crc = _u32(payload, descriptor + 4) if payload[descriptor:descriptor + 4] == _DESCRIPTOR else _u32(payload, descriptor)
        descriptor_start = descriptor + (4 if payload[descriptor:descriptor + 4] == _DESCRIPTOR else 0)
        if int(member["compressed"]) > 0xFFFFFFFF or int(member["uncompressed"]) > 0xFFFFFFFF:
            raise ContractError(f"publication archive member {member['name']!r} ZIP64 data descriptor is inconsistent")
        described_crc = _u32(payload, descriptor_start)
        described_compressed = _u32(payload, descriptor_start + 4)
        described_uncompressed = _u32(payload, descriptor_start + 8)
        if (
            described_crc != int(member["crc"])
            or described_compressed != int(member["compressed"])
            or described_uncompressed != int(member["uncompressed"])
        ):
            raise ContractError(f"publication archive member {member['name']!r} data descriptor disagrees")
        data_end_with_descriptor = descriptor_start + 12
    else:
        compressed_size, uncompressed_size, _ = _zip64_extra(extra, compressed_size, uncompressed_size, 0)
        if crc != int(member["crc"]) or compressed_size != int(member["compressed"]) or uncompressed_size != int(member["uncompressed"]):
            raise ContractError(f"publication archive member {member['name']!r} local sizes disagree")
        if flags != int(member["flags"]):
            raise ContractError(f"publication archive member {member['name']!r} local flags disagree")
        data_start = header_end
        data_end = data_start + int(member["compressed"])
        data_end_with_descriptor = data_end
    if data_end > len(payload):
        raise ContractError(f"publication archive member {member['name']!r} data exceeds the raw archive")
    return data_start, data_end_with_descriptor, payload[data_start:data_end]


def scan_archive(payload: bytes) -> list[ArchiveMember]:
    """Authenticate one raw ZIP and return its regular members without executing them."""

    if len(payload) > RAW_LIMIT:
        raise ContractError(f"publication archive is {len(payload)} bytes, exceeding the {RAW_LIMIT}-byte ceiling")
    members = _central_members(payload)
    names = [str(member["name"]) for member in members]
    if len(names) != len(set(names)):
        raise ContractError("publication archive contains duplicate member names")
    total_uncompressed = sum(int(member["uncompressed"]) for member in members)
    total_compressed = sum(int(member["compressed"]) for member in members)
    if total_uncompressed > MAX_TOTAL_UNCOMPRESSED or _ratio_exceeds(total_uncompressed, total_compressed):
        raise ContractError("publication archive exceeds the aggregate size or compression ceiling")
    ranges: list[tuple[int, int, str]] = []
    authenticated: list[ArchiveMember] = []
    for member in members:
        data_start, data_end, compressed = _read_member(payload, member)
        ranges.append((int(member["offset"]), data_end, str(member["name"])))
        inflated = _inflate(member, compressed)
        if zlib.crc32(inflated) & 0xFFFFFFFF != int(member["crc"]):
            raise ContractError(f"publication archive member {member['name']!r} CRC disagrees")
        authenticated.append(ArchiveMember(str(member["name"]), inflated, int(member["crc"])))
    ranges.sort()
    previous_end = -1
    for start, end, name in ranges:
        if start < previous_end:
            raise ContractError(f"publication archive member {name!r} overlaps another member")
        previous_end = end
    return authenticated


def materialize_archive(payload: bytes, destination: Path) -> list[ArchiveMember]:
    """Scan an archive and write only its authenticated regular members."""

    members = scan_archive(payload)
    destination.mkdir(mode=0o700, exist_ok=False)
    for member in members:
        target = destination.joinpath(*member.name.split("/"))
        if destination not in target.parents:
            raise ContractError(f"publication archive member {member.name!r} escapes the destination")
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY
        if hasattr(os, "O_NOFOLLOW"):
            flags |= os.O_NOFOLLOW
        descriptor = os.open(target, flags, 0o600)
        try:
            with os.fdopen(descriptor, "wb") as handle:
                handle.write(member.payload)
        except Exception:
            os.close(descriptor)
            raise
    return members
