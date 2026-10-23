"""Exercise the shared repair at the SDK image-creation boundary without publishing."""
from pathlib import Path
from html import escape
import subprocess  # nosec B404 -- tests execute fixed repository-owned MSBuild fixtures.
import tempfile
import unittest

TARGETS = Path(__file__).resolve().parents[3] / "Props/ContainerProvenance.targets"


class ContainerProvenanceTargetsTests(unittest.TestCase):
    def run_target(self, target=TARGETS, *, enabled=True, labels=True, overrides=None, valid=True):
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
            property_xml = "".join(f"<{key}>{escape(value)}</{key}>"
                                   for key, value in properties.items())
            label_xml = "".join(f'<ContainerLabel Include="{escape(key, quote=True)}" '
                                f'Value="{escape(value, quote=True)}" />'
                                for key, value in seed.items())
            project = f'''<Project>
  <PropertyGroup>{property_xml}</PropertyGroup>
  <Import Project="{escape(str(target), quote=True)}" />
  <ItemGroup>{label_xml}</ItemGroup>
  <Target Name="_PublishSingleContainer">
    <WriteLinesToFile File="{escape(str(output), quote=True)}"
      Lines="@(ContainerLabel-&gt;'%(Identity)=%(Value)')" Overwrite="true" />
    <WriteLinesToFile File="{escape(str(root / 'flags.txt'), quote=True)}"
      Lines="$(ContainerGenerateLabelsImageSource)" Overwrite="true" />
  </Target>
</Project>'''
            path = root / "fixture.proj"
            path.write_text(project)
            command = ["dotnet", "msbuild", str(path), "-nologo", "-v:q", "-t:_PublishSingleContainer"]
            result = subprocess.run(  # nosec B603 -- fixed executable/arguments and locally generated fixture; no shell.
                command, capture_output=True, text=True, timeout=30)
            if not valid:
                self.assertNotEqual(result.returncode, 0, "Impossible created instant reached image creation")
                self.assertIn("ContainerProvenanceCreated", result.stdout + result.stderr)
                self.assertFalse(output.exists())
                self.assertFalse((root / "flags.txt").exists(), "Image-write target body must not run")
                return None, properties
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
            "org.opencontainers.image.created": properties["ContainerProvenanceCreated"],
            "org.opencontainers.artifact.created": properties["ContainerProvenanceCreated"],
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
        for missing in ["ContainerProvenanceSourceSha", "ContainerProvenanceReleaseVersion"]:
            with self.subTest(missing=missing):
                labels, properties = self.run_target(overrides={missing: ""})
                self.assertEqual(labels, {"org.opencontainers.image.licenses": "MIT",
                                         "org.opencontainers.image.source": "https",
                                         "org.opencontainers.image.created": "2026-10-21T18"})
                self.assertEqual(properties["ResolvedGenerateSource"], "true")

    def test_missing_created_with_source_and_version_never_reaches_image_creation(self):
        self.run_target(overrides={"ContainerProvenanceCreated": ""}, valid=False)

    def test_accepts_real_calendar_instants_with_colons(self):
        for created in ["2024-02-29T23:59:59Z", "2000-02-29T00:00:00Z", "2026-04-30T12:34:56Z"]:
            with self.subTest(created=created):
                labels, properties = self.run_target(overrides={"ContainerProvenanceCreated": created})
                self.assert_provenance(labels, properties)

    def test_rejects_impossible_calendar_instants_before_image_creation(self):
        for created in ["2023-02-29T00:00:00Z", "1900-02-29T00:00:00Z", "2024-04-31T00:00:00Z",
                        "0000-01-01T00:00:00Z", "2026-01-01T24:00:00Z", "2026-01-01T00:00:00+00:00"]:
            with self.subTest(created=created):
                self.run_target(overrides={"ContainerProvenanceCreated": created}, valid=False)

    def test_removing_the_rebinding_target_is_detected(self):
        with tempfile.TemporaryDirectory(prefix="builds-provenance-mutation-") as temporary:
            mutant = Path(temporary) / "removed.targets"
            source = TARGETS.read_text()
            start_marker = '  <Target Name="RebindContainerProvenanceLabels"'
            self.assertEqual(source.count(start_marker), 1)
            start = source.index(start_marker)
            end = source.index("  </Target>", start) + len("  </Target>")
            mutant.write_text(source[:start] + source[end:])
            labels, properties = self.run_target(mutant)
            with self.assertRaises(AssertionError):
                self.assert_provenance(labels, properties)


if __name__ == "__main__":
    unittest.main()
