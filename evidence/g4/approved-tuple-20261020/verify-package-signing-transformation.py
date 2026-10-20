"""Inspect ZIP bytes without running tools, repacking, or rebuilding a product."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def unsigned_bytes(path):
    raw = path.read_bytes()
    with zipfile.ZipFile(path) as archive:
        items = archive.infolist()
        assert len({item.filename for item in items}) == len(items), "duplicate archive entry"
        signature = archive.getinfo(".signature.p7s")
        offsets = sorted(item.header_offset for item in items)
        end = next((offset for offset in offsets if offset > signature.header_offset), archive.start_dir)
        removed = end - signature.header_offset
        local = raw[:signature.header_offset] + raw[end:archive.start_dir]
        central, cursor = [], archive.start_dir
        for _ in items:
            assert raw[cursor:cursor + 4] == b"PK\x01\x02", "central-directory shape changed"
            name_length, extra_length, comment_length = struct.unpack_from("<HHH", raw, cursor + 28)
            length = 46 + name_length + extra_length + comment_length
            block = bytearray(raw[cursor:cursor + length])
            name = raw[cursor + 46:cursor + 46 + name_length].decode("utf-8")
            if name != ".signature.p7s":
                offset = struct.unpack_from("<I", block, 42)[0]
                assert offset != 0xffffffff, "ZIP64 requires a separate inspected procedure"
                if offset > signature.header_offset:
                    struct.pack_into("<I", block, 42, offset - removed)
                central.append(bytes(block))
            cursor += length
        assert raw[cursor:cursor + 4] == b"PK\x05\x06", "end record changed"
        assert cursor + 22 + struct.unpack_from("<H", raw, cursor + 20)[0] == len(raw), "unaccounted ZIP trailer"
        end_record = bytearray(raw[cursor:])
        count, directory = len(items) - 1, b"".join(central)
        struct.pack_into("<HHII", end_record, 8, count, count, len(directory), len(local))
        return raw, local + directory + bytes(end_record), count


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--inventory", type=Path, required=True)
parser.add_argument("--archives", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
options = parser.parse_args()
assert not options.output.exists(), "preserve earlier observations"
inventory_raw = options.inventory.read_bytes()
inventory = json.loads(inventory_raw)
rows = []
for package in inventory["packages"]:
    identifier, version = package["id"], package["version"]
    path = options.archives / (identifier.lower() + "." + version + ".nupkg")
    signed, unsigned, count = unsigned_bytes(path)
    expected, actual = package["nupkg"]["sha256"].lower(), digest(unsigned)
    rows.append({"id": identifier, "version": version, "archive": str(path.resolve()),
                 "signedArchiveSha256": digest(signed), "qualifiedLocalSha256": expected,
                 "reconstructedUnsignedSha256": actual, "exactQualifiedArchiveReconstructed": actual == expected,
                 "payloadEntryCount": count, "derivedBytes": len(unsigned)})
result = {"artifactKind": "independent static NuGet signing transformation proof",
          "procedureSha256": digest(Path(__file__).read_bytes()),
          "inventory": str(options.inventory.resolve()), "inventorySha256": digest(inventory_raw),
          "method": "remove signature local/central records and adjust ZIP offsets/counts; preserve all other compressed bytes and metadata",
          "packages": rows, "allQualifiedArchiveHashesMatch": all(row["exactQualifiedArchiveReconstructed"] for row in rows),
          "qualificationOrAcceptanceGranted": False}
options.output.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps({key: result[key] for key in ["allQualifiedArchiveHashesMatch", "qualificationOrAcceptanceGranted"]}))
raise SystemExit(0 if result["allQualifiedArchiveHashesMatch"] else 2)
