"""Executed tar/graph controls for original-byte release OCI assembly."""
from __future__ import annotations

import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import oci_archive as oci

SHA = "a" * 40
VERSION = "2.4.0"
REPOSITORY = "owner/product"


def sdk_archive(path, architecture="amd64", *, version=VERSION):
    files = {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}'}

    def store(value, media):
        data = value if isinstance(value, bytes) else json.dumps(value).encode()
        digest = oci.sha256(data)
        files["blobs/sha256/" + digest[7:]] = data
        return {"mediaType": media, "digest": digest, "size": len(data)}

    layer = store(b"unchanged compressed layer bytes", "application/vnd.oci.image.layer.v1.tar+gzip")
    config = store({"os": "linux", "architecture": architecture, "config": {"Labels": {
        "org.opencontainers.image.source": "https://github.com/" + REPOSITORY,
        "org.opencontainers.image.revision": SHA, "org.opencontainers.image.version": version,
        "arbitrary": "https://example.org:443/a:b"}}}, "application/vnd.oci.image.config.v1+json")
    child = store({"schemaVersion": 2, "mediaType": oci.MANIFEST, "config": config, "layers": [layer]}, oci.MANIFEST)
    files["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [child]}).encode()
    write_tar(path, files)
    return files


def write_tar(path, files):
    with tarfile.open(path, "w") as archive:
        for name, data in files.items():
            entry = tarfile.TarInfo(name)
            entry.size = len(data)
            archive.addfile(entry, io.BytesIO(data))


class OciTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.inputs = []
        self.original = {}
        for architecture in ("amd64", "arm64"):
            path = self.root / (architecture + ".tar.gz")
            files = sdk_archive(path, architecture)
            self.original.update({name: data for name, data in files.items() if name.startswith("blobs/")})
            self.inputs.append((path, {"os": "linux", "architecture": architecture}))
        self.output = self.root / "combined.tar"

    def combine(self):
        return oci.combine_archives(self.inputs, self.output, revision=SHA, version=VERSION,
                                    repository=REPOSITORY, name="product/image")

    def test_complete_graph_preserves_all_image_bytes_and_colon_labels(self):
        graph = self.combine()
        self.assertEqual(["linux/amd64", "linux/arm64"], graph["platforms"])
        self.assertNotEqual(b"\x1f\x8b", self.output.read_bytes()[:2])
        files = oci.archive_files(self.output)
        for name, data in self.original.items():
            self.assertEqual(data, files[name])
        self.assertEqual(graph, oci.inspect_archive(io.BytesIO(self.output.read_bytes()), revision=SHA,
            version=VERSION, repository=REPOSITORY, name="product/image", platforms=graph["platforms"]))

    def test_graph_rejects_missing_tampered_unreferenced_and_wrong_identity(self):
        self.combine()
        original = oci.archive_files(self.output)
        for kind in ("missing", "tampered", "unreferenced"):
            with self.subTest(kind=kind):
                files = original.copy()
                path = next(name for name in self.original)
                if kind == "missing":
                    del files[path]
                elif kind == "tampered":
                    files[path] += b"x"
                else:
                    files["blobs/sha256/" + "b" * 64] = b"unused"
                write_tar(self.output, files)
                with self.assertRaises(oci.OciError):
                    oci.inspect_archive(self.output, revision=SHA, version=VERSION)
        write_tar(self.output, original)
        for changes in ({"revision": "b" * 40}, {"version": "2.3.4"}, {"name": "other"},
                        {"platforms": ["linux/amd64"]}, {"repository": "owner/other"}):
            with self.subTest(changes=changes), self.assertRaises(oci.OciError):
                oci.inspect_archive(self.output, **({"revision": SHA, "version": VERSION,
                    "repository": REPOSITORY, "name": "product/image"} | changes))

    def test_duplicate_platform_wrong_platform_and_foreign_version_rejected(self):
        for changes in ("duplicate", "wrong", "version"):
            with self.subTest(changes=changes):
                if changes == "duplicate":
                    self.inputs[1] = self.inputs[0]
                elif changes == "wrong":
                    self.inputs[0] = (self.inputs[0][0], {"os": "windows", "architecture": "amd64"})
                else:
                    sdk_archive(self.inputs[0][0], version="9.0.0")
                with self.assertRaises(oci.OciError):
                    self.combine()
                self.setUp()

    def test_unsafe_link_and_duplicate_json_rejected(self):
        for name in ("../escape", "/escape", "a\\b"):
            write_tar(self.output, {name: b"x"})
            with self.subTest(name=name), self.assertRaises(oci.OciError):
                oci.archive_files(self.output)
        with tarfile.open(self.output, "w") as archive:
            entry = tarfile.TarInfo("link")
            entry.type, entry.linkname = tarfile.SYMTYPE, "/etc/passwd"
            archive.addfile(entry)
        with self.assertRaises(oci.OciError):
            oci.archive_files(self.output)
        with self.assertRaises(oci.OciError):
            oci.document(b'{"a":1,"a":2}')


if __name__ == "__main__":
    unittest.main()
