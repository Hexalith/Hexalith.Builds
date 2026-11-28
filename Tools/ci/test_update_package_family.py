import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import release_declarations
import update_package_family
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

    def test_apply_registers_new_owner_release_with_local_metadata(self):
        packages = {
            "folders": "Hexalith.Folders.Contracts",
            "projects": "Hexalith.Projects.EventStoreExtensions",
            "mcpcli": "Hexalith.McpCli.Abstractions",
            "conversations": "Hexalith.Conversations.Contracts",
        }
        version = "2.3.4"
        commit = "a" * 40
        for owner, package in packages.items():
            with self.subTest(owner=owner), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                catalog, audit, index = (root / name for name in ("catalog.props", "audit.json", "releases.json"))
                family = "hexalith-" + owner
                audit.write_text(json.dumps({"packages": [{"id": package, "family": family, "selectedVersion": "1.0.0"}]}))
                catalog.write_text(f'<Project><ItemGroup><PackageVersion Include="{package}" Version="1.0.0" /></ItemGroup></Project>')
                index.write_text("{}")
                repository = package.rsplit(".", 1)[0]
                manifest = json.dumps({"schema": "hexalith.module-manifest.v2", "modules": [{"identity": {"moduleId": owner}}]}).encode()
                metadata = (
                    f"<package><metadata><id>{package}</id><version>{version}</version>"
                    f"<repository type='git' url='https://github.com/Hexalith/{repository}' commit='{commit}'/>"
                    "</metadata></package>"
                ).encode()
                package_path = package.lower()
                sources = {
                    f"https://api.nuget.org/v3-flatcontainer/{package_path}/{version}/{package_path}.nuspec": metadata,
                    f"https://raw.githubusercontent.com/Hexalith/{repository}/{commit}/module/hexalith.module-manifest.v2.json": manifest,
                }

                def verify(selected_owner, selected_version):
                    return resolve(selected_owner, selected_version, sources.__getitem__,
                                   lambda repo, value: {(repository, version): commit}[repo, value])

                with patch.object(update_package_family, "CATALOG", catalog), patch.object(update_package_family, "AUDIT", audit), \
                     patch.object(release_declarations, "INDEX", index), patch.object(release_declarations, "resolve", verify), \
                     patch.object(sys, "argv", ["update_package_family.py", "--apply", family, version]):
                    update_package_family.main()

                self.assertIn(f'Version="{version}"', catalog.read_text())
                recorded = json.loads(index.read_text())
                self.assertEqual([f"{owner}/{version}"], list(recorded))
                self.assertEqual(hashlib.sha256(manifest).hexdigest(), recorded[f"{owner}/{version}"]["sha256"])

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
