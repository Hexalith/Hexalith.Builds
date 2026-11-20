import json
import unittest

from release_declarations import INDEX, resolve

from update_package_family import AUDIT, CATALOG, newest_complete_release, update_family


class PackageFamilies(unittest.TestCase):
    def test_partial_and_prerelease_publications_do_not_move_the_family(self):
        rows = [{"id": "a", "selectedVersion": "5.4.0"}, {"id": "b", "selectedVersion": "5.4.0"}]
        self.assertIsNone(newest_complete_release(rows, [{"5.4.0", "6.0.0", "7.0.0-rc.1"}, {"5.4.0", "7.0.0-rc.1"}]))
        self.assertEqual("6.0.0", newest_complete_release(rows, [{"5.4.0", "6.0.0"}] * 2, lambda *_: True))
        self.assertIsNone(newest_complete_release(rows, [{"5.4.0", "6.0.0"}] * 2, lambda package, _: package == "a"))
        with self.assertRaises(ValueError):
            newest_complete_release(rows, [{"5.4.0"}, {"6.0.0"}])

    def test_all_audited_family_owners_preserve_catalog_bytes(self):
        families = {}
        for row in json.loads(AUDIT.read_bytes())["packages"]:
            if row["id"].startswith("Hexalith."):
                families.setdefault(row["family"], []).append(row)
        original = CATALOG.read_bytes()
        for family, rows in families.items():
            with self.subTest(family=family):
                updated = update_family(original, rows, "999.123.456")
                self.assertNotEqual(updated, original)
                updated_rows = [dict(row, selectedVersion="999.123.456") for row in rows]
                if len({row["selectedVersion"] for row in rows}) == 1:
                    self.assertEqual(original, update_family(updated, updated_rows, rows[0]["selectedVersion"]))

    def test_published_declaration_must_match_the_package_and_release_tag(self):
        release = json.loads(INDEX.read_bytes())["eventstore/5.4.0"]
        commit = release["commit"]
        spec = f"<package><metadata><id>Hexalith.EventStore.Aspire</id><version>5.4.0</version><repository type='git' url='https://github.com/Hexalith/Hexalith.EventStore' commit='{commit}'/></metadata></package>".encode()
        manifest = b'{"schema":"hexalith.module-manifest.v2","modules":[{"identity":{"moduleId":"eventstore"}}]}'
        read = lambda url: spec if url.endswith(".nuspec") else manifest
        entry, body = resolve("eventstore", "5.4.0", read, lambda *_: commit)
        self.assertEqual(body, manifest)
        self.assertEqual(entry["commit"], commit)
        with self.assertRaisesRegex(ValueError, "disagree"):
            resolve("eventstore", "5.4.0", read, lambda *_: "0" * 40)
        with self.assertRaisesRegex(ValueError, "identity"):
            resolve("eventstore", "5.4.1", read, lambda *_: commit)
