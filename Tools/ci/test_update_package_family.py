import json
import unittest

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
