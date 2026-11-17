"""Real ZIPs and the public contract, including signature-only feed changes."""
import copy
import hashlib
import io
import json
from pathlib import Path
import sys
import unittest
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import package_publication as publication


def archive(files):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as package:
        for name, data in files:
            package.writestr(name, data)
    return stream.getvalue()


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.original = archive([("Fixture.nuspec", b"metadata"), ("tools/net11.0/any/Fixture.dll", b"compiled")])
        self.signed = archive([("Fixture.nuspec", b"metadata"), ("tools/net11.0/any/Fixture.dll", b"compiled"),
                               (".signature.p7s", b"repository-signature")])
        self.document = {"schema": publication.SCHEMA, "repository": "Example/Fixture", "sha": "a" * 40,
                         "version": "6.0.7", "artifactRun": 12, "artifactId": 34,
                         "artifactDigest": "sha256:" + "b" * 64,
                         "packages": [publication.record("Fixture", self.signed, publication.payload_map(self.original))]}

    def test_signature_only_changes_preserve_payload_but_signed_archive_hash_covers_every_byte(self):
        self.assertEqual(publication.payload_map(self.original), publication.payload_map(self.signed))
        record = self.document["packages"][0]
        self.assertEqual(record["archiveSha256"], hashlib.sha256(self.signed).hexdigest())
        self.assertNotEqual(record["archiveSha256"], hashlib.sha256(self.original).hexdigest())
        changed = archive([("Fixture.nuspec", b"metadata"), ("tools/net11.0/any/Fixture.dll", b"different"),
                           (".signature.p7s", b"repository-signature")])
        with self.assertRaisesRegex(publication.PublicationError, "payload differs"):
            publication.record("Fixture", changed, publication.payload_map(self.original))

    def test_deterministic_roundtrip_and_empty_inventory(self):
        data = publication.serialize(self.document)
        self.assertEqual(publication.read(data), self.document)
        self.assertEqual(data, publication.serialize(dict(reversed(list(self.document.items())))))
        self.document["packages"] = []
        self.assertEqual(publication.read(publication.serialize(self.document))["packages"], [])

    def test_closed_document_and_schema_agree_on_structural_invalid_data(self):
        from jsonschema import Draft202012Validator
        schema = json.loads((Path(__file__).resolve().parents[3] / "schemas/hexalith.package-publication.v1.json").read_bytes())
        validator = Draft202012Validator(schema)
        validator.validate(self.document)
        invalid = []
        for key, value in (("sha", "a" * 40 + "\n"), ("schema", "other"), ("artifactRun", True),
                           ("artifactId", 0), ("version", "06.0.7"), ("artifactDigest", "b" * 64), ("extra", 1)):
            candidate = copy.deepcopy(self.document)
            candidate[key] = value
            invalid.append(candidate)
        for path in ("../outside", "/root", "a//b", "a/./b", "a/../b", "C:/root", "a\\b", ".signature.p7s"):
            candidate = copy.deepcopy(self.document)
            candidate["packages"][0]["files"] = {path: "c" * 64}
            invalid.append(candidate)
        for candidate in invalid:
            with self.subTest(candidate=candidate), self.assertRaises(publication.PublicationError):
                publication.validate(candidate)
            self.assertTrue(list(validator.iter_errors(candidate)), candidate)

    def test_duplicates_malformed_and_oversize_documents_refuse(self):
        for data in (b'{"schema":1,"schema":2}', b"NaN", b"\xff", b"[]", b" " * (publication.MAX_DOCUMENT + 1)):
            with self.subTest(data=data[:40]), self.assertRaises(publication.PublicationError):
                publication.read(data)
        duplicate = copy.deepcopy(self.document)
        duplicate["packages"].append(copy.deepcopy(duplicate["packages"][0]))
        with self.assertRaises(publication.PublicationError):
            publication.serialize(duplicate)
        duplicate["packages"][1]["id"] = "Another"
        with self.assertRaises(publication.PublicationError):
            publication.serialize(duplicate)

    def test_unsafe_duplicate_and_nonregular_zip_entries_refuse(self):
        for path in ("../outside", "a/../../b", "/root", "C:/root", "a//b", "a\\b", "a/./b"):
            with self.subTest(path=path), self.assertRaises(publication.PublicationError):
                publication.payload_map(archive([(path, b"payload")]))
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            duplicate = archive([("a", b"one"), ("a", b"two")])
        for data in (duplicate, archive([("A", b"one"), ("a", b"two")]), b"invalid"):
            with self.assertRaises(publication.PublicationError):
                publication.payload_map(data)
        link = zipfile.ZipInfo("link")
        link.external_attr = 0o120777 << 16
        with self.assertRaises(publication.PublicationError):
            publication.payload_map(archive([(link, b"target")]))


if __name__ == "__main__":
    unittest.main()
