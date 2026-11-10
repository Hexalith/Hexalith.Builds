"""Executed tar/graph controls for original-byte release OCI assembly."""
from __future__ import annotations

import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import oci_archive as oci

SHA = "a" * 40
VERSION = "2.4.0"
REPOSITORY = "owner/product"


def sdk_archive(path, architecture="amd64", *, version=VERSION, platform=None, config_platform=None):
    files = {"oci-layout": b'{"imageLayoutVersion":"1.0.0"}'}

    def store(value, media):
        data = value if isinstance(value, bytes) else json.dumps(value).encode()
        digest = oci.sha256(data)
        files["blobs/sha256/" + digest[7:]] = data
        return {"mediaType": media, "digest": digest, "size": len(data)}

    layer = store(b"unchanged compressed layer bytes", "application/vnd.oci.image.layer.v1.tar+gzip")
    config = store({"os": "linux", "architecture": architecture, **(config_platform or {}), "config": {"Labels": {
        "org.opencontainers.image.source": "https://github.com/" + REPOSITORY,
        "org.opencontainers.image.revision": SHA, "org.opencontainers.image.version": version,
        "arbitrary": "https://example.org:443/a:b"}}}, "application/vnd.oci.image.config.v1+json")
    child = store({"schemaVersion": 2, "mediaType": oci.MANIFEST, "config": config, "layers": [layer]}, oci.MANIFEST)
    if platform is not None:
        child["platform"] = platform
    files["index.json"] = json.dumps({"schemaVersion": 2, "manifests": [child]}).encode()
    write_tar(path, files)
    return files


def write_tar(path, files):
    with tarfile.open(path, "w") as archive:
        for name, data in files.items():
            entry = tarfile.TarInfo(name)
            entry.size = len(data)
            archive.addfile(entry, io.BytesIO(data))


def replace_blob(files, descriptor, value):
    """Rehash all changed graph edges so malformed controls pass digest checks."""
    data = json.dumps(value).encode()
    del files["blobs/sha256/" + descriptor["digest"][7:]]
    descriptor.update(digest=oci.sha256(data), size=len(data))
    files["blobs/sha256/" + descriptor["digest"][7:]] = data


def change_manifest(files, mutate):
    layout = oci.document(files["index.json"])
    descriptor, = layout["manifests"]
    manifest = oci.document(files["blobs/sha256/" + descriptor["digest"][7:]])
    mutate(manifest)
    replace_blob(files, descriptor, manifest)
    files["index.json"] = json.dumps(layout).encode()


def pax_record(key, value):
    suffix = f" {key}={value}\n".encode()
    size = len(suffix) + 1
    while len(str(size)) + len(suffix) != size:
        size = len(str(size)) + len(suffix)
    return str(size).encode() + suffix


def extended_archive(path, kind, count=1):
    with tarfile.open(path, "w", format=tarfile.USTAR_FORMAT) as archive:
        for index in range(count):
            name = f"member{index}"
            payload = pax_record("comment", "metadata") if kind in (
                tarfile.XHDTYPE, tarfile.XGLTYPE, tarfile.SOLARIS_XHDTYPE) else (name + "\0").encode()
            entry = tarfile.TarInfo("extended")
            entry.type, entry.size = kind, len(payload)
            archive.addfile(entry, io.BytesIO(payload))
            entry = tarfile.TarInfo(name)
            entry.size = 1
            archive.addfile(entry, io.BytesIO(b"x"))


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

    def test_assembly_preserves_descriptor_platform_metadata_and_all_child_bytes(self):
        platform = {"os": "linux", "architecture": "arm64", "variant": "v8",
                    "os.version": "6.12.0", "os.features": ["feature:a", "feature-b"]}
        path = self.root / "metadata.tar"
        original = sdk_archive(path, "arm64", platform=platform, config_platform=platform)
        self.inputs = [(path, {"os": "linux", "architecture": "arm64"})]
        graph = self.combine()
        self.assertEqual(["linux/arm64/v8"], graph["platforms"])
        files = oci.archive_files(self.output)
        root, = oci.document(files["index.json"])["manifests"]
        index = oci.document(files["blobs/sha256/" + root["digest"][7:]])
        self.assertEqual(platform, index["manifests"][0]["platform"])
        for name, data in original.items():
            if name.startswith("blobs/"):
                self.assertEqual(data, files[name])

    def test_sdk_single_descriptor_can_defer_null_or_partial_platform_to_config(self):
        for supplied in (None, {}, {"os": "linux"}, {"variant": "v8"},
                         {"os": None, "architecture": None, "variant": None, "os.features": None}):
            with self.subTest(supplied=supplied):
                path = self.root / "sdk-null-platform.tar"
                files = sdk_archive(path, "arm64", config_platform={"variant": "v8"})
                layout = oci.document(files["index.json"])
                layout["manifests"][0]["platform"] = supplied
                files["index.json"] = json.dumps(layout).encode()
                write_tar(path, files)
                oci.combine_archives([(path, {"os": "linux", "architecture": "arm64"})], self.output,
                    revision=SHA, version=VERSION, repository=REPOSITORY, name="product/image")
                combined = oci.archive_files(self.output)
                root, = oci.document(combined["index.json"])["manifests"]
                index = oci.document(combined["blobs/sha256/" + root["digest"][7:]])
                self.assertEqual({"os": "linux", "architecture": "arm64", "variant": "v8"},
                                 index["manifests"][0]["platform"])
                for name, data in files.items():
                    if name.startswith("blobs/"):
                        self.assertEqual(data, combined[name])

    def test_assembled_children_still_require_complete_nonnull_platforms(self):
        self.combine()
        original = oci.archive_files(self.output)
        for supplied in (None, {}, {"os": "linux"}, {"os": None, "architecture": "amd64"}):
            with self.subTest(supplied=supplied):
                files = original.copy()
                layout = oci.document(files["index.json"])
                root, = layout["manifests"]
                index = oci.document(files["blobs/sha256/" + root["digest"][7:]])
                index["manifests"][0]["platform"] = supplied
                replace_blob(files, root, index)
                files["index.json"] = json.dumps(layout).encode()
                with self.assertRaisesRegex(oci.OciError, "missing OCI platform"):
                    oci.inspect_files(files, revision=SHA, version=VERSION)

    def test_conflicting_or_malformed_platform_metadata_rejected(self):
        path = self.root / "metadata.tar"
        for metadata in ({"variant": 7}, {"os.version": []}, {"os.features": "feature"},
                         {"os.features": [3]}, {"os.features": [""]}):
            with self.subTest(metadata=metadata):
                supplied = {"os": "linux", "architecture": "arm64", **metadata}
                sdk_archive(path, "arm64", platform=supplied)
                with self.assertRaisesRegex(oci.OciError, "platform metadata"):
                    oci.combine_archives([(path, {"os": "linux", "architecture": "arm64"})], self.output,
                        revision=SHA, version=VERSION, repository=REPOSITORY, name="product/image")
        for key, actual, expected in (("variant", "v8", "v7"), ("os.version", "6.12", "6.11"),
                                      ("os.features", ["a"], ["b"])):
            with self.subTest(key=key):
                supplied = {"os": "linux", "architecture": "arm64", key: actual}
                sdk_archive(path, "arm64", platform=supplied, config_platform={key: expected})
                with self.assertRaisesRegex(oci.OciError, "configuration platform mismatch"):
                    oci.combine_archives([(path, {"os": "linux", "architecture": "arm64"})], self.output,
                        revision=SHA, version=VERSION, repository=REPOSITORY, name="product/image")
        sdk_archive(path, "arm64", platform={"os": "linux", "architecture": "arm64", "variant": "v8"})
        with self.assertRaisesRegex(oci.OciError, "selected platform"):
            oci.combine_archives([(path, {"os": "linux", "architecture": "arm64", "variant": "v7"})],
                self.output, revision=SHA, version=VERSION, repository=REPOSITORY, name="product/image")

    def test_correctly_rehashed_graph_requires_supported_config_and_layer_media_types(self):
        for target in ("config", "layer"):
            for media in (None, "application/octet-stream", oci.MANIFEST, "",
                          "application/vnd.docker.container.image.v1+json",
                          "application/vnd.docker.distribution.manifest.v2+json"):
                with self.subTest(target=target, media=media):
                    files = sdk_archive(self.output)
                    def change(manifest):
                        descriptor = manifest["config"] if target == "config" else manifest["layers"][0]
                        if media is None:
                            del descriptor["mediaType"]
                        else:
                            descriptor["mediaType"] = media
                    change_manifest(files, change)
                    write_tar(self.output, files)
                    with self.assertRaisesRegex(oci.OciError, "descriptor media type"):
                        oci.inspect_files(oci.archive_files(self.output), revision=SHA, version=VERSION, single=True)
        for media in sorted(oci.LAYERS):
            with self.subTest(supported_media=media):
                files = sdk_archive(self.output)
                change_manifest(files, lambda manifest: manifest["layers"][0].update(mediaType=media))
                self.assertEqual(["linux/amd64"], oci.inspect_files(files,
                    revision=SHA, version=VERSION, single=True)["platforms"])

    def test_sdk_docker_base_layer_descriptors_preserve_original_child_bytes(self):
        for media in ("application/vnd.docker.image.rootfs.diff.tar.gzip",
                      "application/vnd.docker.image.rootfs.foreign.diff.tar.gzip"):
            with self.subTest(media=media):
                source = self.root / "sdk-docker-base.tar"
                original = sdk_archive(source)
                change_manifest(original, lambda manifest: manifest["layers"][0].update(mediaType=media))
                write_tar(source, original)
                oci.combine_archives([(source, {"os": "linux", "architecture": "amd64"})], self.output,
                    revision=SHA, version=VERSION, repository=REPOSITORY, name="product/image")
                assembled = oci.archive_files(self.output)
                for name, data in original.items():
                    if name.startswith("blobs/"):
                        self.assertEqual(data, assembled[name])

    def test_extended_payload_is_bounded_before_tarfile_reads_it(self):
        kinds = (tarfile.XHDTYPE, tarfile.XGLTYPE, tarfile.SOLARIS_XHDTYPE,
                 tarfile.GNUTYPE_LONGNAME, tarfile.GNUTYPE_LONGLINK)
        for kind in kinds:
            with self.subTest(kind=kind):
                extended_archive(self.output, kind)
                method = "_proc_pax" if kind in kinds[:3] else "_proc_gnulong"
                with patch.object(oci, "MAX_EXTENDED_METADATA_SIZE", tarfile.BLOCKSIZE - 1), \
                     patch.object(tarfile.TarInfo, method, side_effect=AssertionError("metadata was read")):
                    with self.assertRaisesRegex(oci.OciError, "extended metadata"):
                        oci.archive_files(self.output)

    def test_extended_metadata_accumulates_across_members_with_small_controls(self):
        for kind, method in ((tarfile.XHDTYPE, "_proc_pax"), (tarfile.XGLTYPE, "_proc_pax"),
                             (tarfile.GNUTYPE_LONGNAME, "_proc_gnulong"),
                             (tarfile.GNUTYPE_LONGLINK, "_proc_gnulong")):
            with self.subTest(kind=kind):
                extended_archive(self.output, kind, count=2)
                original = getattr(tarfile.TarInfo, method)
                calls = []
                def process(member, archive):
                    calls.append(member.size)
                    return original(member, archive)
                with patch.object(oci, "MAX_EXTENDED_METADATA_SIZE", tarfile.BLOCKSIZE), \
                     patch.object(oci, "MAX_EXTENDED_METADATA_TOTAL", 3 * tarfile.BLOCKSIZE), \
                     patch.object(tarfile.TarInfo, method, process):
                    with self.assertRaisesRegex(oci.OciError, "extended metadata"):
                        oci.archive_files(self.output)
                self.assertEqual(1, len(calls))

    def test_normal_sdk_pax_and_gnu_metadata_archives_remain_readable(self):
        for kind in (tarfile.XHDTYPE, tarfile.XGLTYPE, tarfile.SOLARIS_XHDTYPE,
                     tarfile.GNUTYPE_LONGNAME, tarfile.GNUTYPE_LONGLINK):
            with self.subTest(kind=kind):
                extended_archive(self.output, kind, count=2)
                self.assertEqual({"member0": b"x", "member1": b"x"}, oci.archive_files(self.output))

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
