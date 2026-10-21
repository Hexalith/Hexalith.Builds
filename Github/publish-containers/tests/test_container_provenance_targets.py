"""Exercise the shared repair at the SDK image-creation boundary without publishing."""
from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

TARGETS = Path(__file__).resolve().parents[3] / "Props/ContainerProvenance.targets"


class ContainerProvenanceTargetsTests(unittest.TestCase):
    def run_target(self, target=TARGETS, *, enabled=True, labels=True, overrides=None):
        with tempfile.TemporaryDirectory(prefix="builds-provenance-") as temporary:
            root = Path(temporary)
            output = root / "labels.txt"
            properties = {
                "EnableContainer": "true" if enabled else "false",
                "RepositoryUrl": "https://example.test/platform/repository",
                "ContainerGenerateLabelsImageSource": "true",
                "ContainerProvenanceSourceSha": "a" * 40,
                "ContainerProvenanceReleaseVersion": "1.2.3",
                "ContainerProvenanceCreated": "2026-10-21T18:30:45Z",
            }
            properties.update(overrides or {})
            seed = {"org.opencontainers.image.licenses": "MIT"}
            if labels:
                seed.update({"org.opencontainers.image.source": "https",
                             "org.opencontainers.image.created": "2026-10-21T18"})
            project = ET.Element("Project")
            group = ET.SubElement(project, "PropertyGroup")
            for key, value in properties.items():
                ET.SubElement(group, key).text = value
            ET.SubElement(project, "Import", Project=str(target))
            group = ET.SubElement(project, "ItemGroup")
            for key, value in seed.items():
                ET.SubElement(group, "ContainerLabel", Include=key, Value=value)
            publish = ET.SubElement(project, "Target", Name="_PublishSingleContainer")
            ET.SubElement(publish, "WriteLinesToFile", File=str(output),
                          Lines="@(ContainerLabel->'%(Identity)=%(Value)')", Overwrite="true")
            ET.SubElement(publish, "WriteLinesToFile", File=str(root / "flags.txt"),
                          Lines="$(ContainerGenerateLabelsImageSource)", Overwrite="true")
            path = root / "fixture.proj"
            ET.ElementTree(project).write(path, encoding="unicode")
            command = ["dotnet", "msbuild", str(path), "-nologo", "-v:q", "-t:_PublishSingleContainer"]
            result = subprocess.run(command, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            rows = [line.split("=", 1) for line in output.read_text().splitlines()]
            self.assertEqual(len(rows), len(dict(rows)), "Duplicate labels reached image creation")
            properties["ResolvedGenerateSource"] = (root / "flags.txt").read_text().strip()
            return dict(rows), properties

    def assert_provenance(self, labels, properties):
        self.assertEqual(properties["ResolvedGenerateSource"], "false")
        repository = properties.get("ContainerProvenanceRepositoryUrl", properties["RepositoryUrl"])
        expected = {
            "org.opencontainers.image.source": repository,
            "org.opencontainers.image.url": properties.get("ContainerProvenanceReleaseUrl", repository + "/releases/tag/v1.2.3"),
            "org.opencontainers.image.documentation": properties.get("ContainerProvenanceDocumentationUrl", repository + "/blob/" + "a" * 40 + "/README.md"),
            "org.opencontainers.image.revision": "a" * 40,
            "org.opencontainers.image.version": "1.2.3",
            "org.opencontainers.image.created": "2026-10-21T18:30:45Z",
            "org.opencontainers.artifact.created": "2026-10-21T18:30:45Z",
            "org.opencontainers.image.licenses": "MIT",
        }
        self.assertEqual(labels, expected)

    def test_rebinds_truncated_sdk_values_and_preserves_unrelated_labels(self):
        labels, properties = self.run_target()
        self.assert_provenance(labels, properties)

    def test_supplies_complete_single_rid_provenance_without_previous_labels(self):
        labels, properties = self.run_target(labels=False)
        self.assert_provenance(labels, properties)

    def test_preserves_explicit_publisher_urls(self):
        labels, properties = self.run_target(overrides={
            "ContainerProvenanceRepositoryUrl": "https://example.test:8443/owner/repository",
            "ContainerProvenanceReleaseUrl": "https://example.test:8443/review/release",
            "ContainerProvenanceDocumentationUrl": "https://example.test:8443/docs/README.md",
        })
        self.assert_provenance(labels, properties)

    def test_leaves_noncontainer_projects_unchanged(self):
        labels, _ = self.run_target(enabled=False)
        self.assertEqual(labels, {"org.opencontainers.image.licenses": "MIT",
                                 "org.opencontainers.image.source": "https",
                                 "org.opencontainers.image.created": "2026-10-21T18"})

    def test_container_without_publisher_metadata_preserves_sdk_behavior(self):
        labels, properties = self.run_target(overrides={
            "ContainerProvenanceSourceSha": "",
            "ContainerProvenanceReleaseVersion": "",
            "ContainerProvenanceCreated": "",
        })
        self.assertEqual(labels, {"org.opencontainers.image.licenses": "MIT",
                                 "org.opencontainers.image.source": "https",
                                 "org.opencontainers.image.created": "2026-10-21T18"})
        self.assertEqual(properties["ResolvedGenerateSource"], "true")

    def test_partial_publisher_metadata_preserves_sdk_behavior(self):
        for missing in ["ContainerProvenanceSourceSha", "ContainerProvenanceReleaseVersion", "ContainerProvenanceCreated"]:
            with self.subTest(missing=missing):
                labels, properties = self.run_target(overrides={missing: ""})
                self.assertEqual(labels, {"org.opencontainers.image.licenses": "MIT",
                                         "org.opencontainers.image.source": "https",
                                         "org.opencontainers.image.created": "2026-10-21T18"})
                self.assertEqual(properties["ResolvedGenerateSource"], "true")

    def test_removing_the_rebinding_target_is_detected(self):
        with tempfile.TemporaryDirectory(prefix="builds-provenance-mutation-") as temporary:
            mutant = Path(temporary) / "removed.targets"
            tree = ET.parse(TARGETS)
            for target in list(tree.getroot().findall("Target")):
                tree.getroot().remove(target)
            tree.write(mutant, encoding="unicode")
            labels, properties = self.run_target(mutant)
            with self.assertRaises(AssertionError):
                self.assert_provenance(labels, properties)


if __name__ == "__main__":
    unittest.main()
