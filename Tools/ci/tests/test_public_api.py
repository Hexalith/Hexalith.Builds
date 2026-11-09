"""Exercise API compatibility against real Git objects and synthetic public documents."""

from __future__ import annotations

import importlib.util
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "check_public_api.py"
SPEC = importlib.util.spec_from_file_location("quality_public_api", SCRIPT)
API = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(API)


WORKFLOW = """name: Synthetic reusable API
on:
  workflow_call:
    inputs:
      build-mode: {type: string, required: false}
    secrets:
      TOKEN: {required: false}
    outputs:
      result: {value: '${{ jobs.fixture.outputs.result }}'}
jobs: {}
"""
ACTION = """name: Synthetic action
inputs:
  solution: {required: true}
runs: {using: composite, steps: []}
"""
SCHEMA = {"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
          "properties": {"name": {"type": "string"}, "mode": {"enum": ["fast", "full"]}},
          "$defs": {"coordinate": {"properties": {"x": {"type": "number"}}}}}


class GitFixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="builds-api-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "repository"
        self.root.mkdir()
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.name", "API Fixture")
        self.git("config", "user.email", "api-fixture@example.invalid")
        self.write(".github/workflows/fixture.yml", WORKFLOW)
        self.write("Github/fixture/action.yml", ACTION)
        self.write("schemas/fixture.json", json.dumps(SCHEMA))
        self.write("Hexalith.Build.props", '<Project><Import Project="Props/Public.props" /></Project>')
        self.write("Props/Public.props", '<Project><PropertyGroup><PublicMode>fast</PublicMode></PropertyGroup></Project>')
        self.write("Hexalith.Package.props", '<Project><PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup></Project>')
        self.write("Props/Directory.Packages.props", '<Project><ItemGroup><PackageVersion Include="Example.Package" Version="1.0.0" /></ItemGroup></Project>')
        self.baseline = self.commit("feat: introduce public API")

    def git(self, *args, cwd=None):
        env = os.environ.copy()
        # Global hooks and commit signing must not turn hermetic fixtures into
        # an operation against a developer's identity or external toolchain.
        result = subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", *args],
                                cwd=cwd or self.root, env=env, text=True,
                                capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return result.stdout.strip()

    def write(self, path, data):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(data, encoding="utf-8")

    def commit(self, message):
        self.git("add", "--all")
        self.git("commit", "-q", "--allow-empty", "-m", message)
        return self.git("rev-parse", "HEAD")

    def check(self, *, root=None, ref="HEAD"):
        return subprocess.run([sys.executable, str(SCRIPT), "--repository", str(root or self.root), "--ref", ref],
                              text=True, capture_output=True, check=False)

    def stable(self, tag="v1.0.0"):
        self.git("tag", tag)

    def remove_input(self, message="fix: adjust public API"):
        self.write("Github/fixture/action.yml", "name: Synthetic action\nruns: {using: composite, steps: []}\n")
        return self.commit(message)

    def test_bootstrap_validates_documents_and_names_full_revision(self):
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("bootstrap", result.stdout)
        self.assertIn(self.baseline, result.stdout)
        self.assertIn("no reachable stable", result.stdout)

    def test_unchanged_api_and_additive_surfaces_pass(self):
        self.stable()
        self.write("Github/new/action.yml", ACTION)
        self.write(".github/workflows/new.yaml", "name: New API\non: {workflow_call: {inputs: {new: {type: string}}}}\njobs: {}\n")
        self.write("schemas/new.json", '{"properties":{"new":{"type":"string"}}}')
        self.commit("feat: add public surfaces")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("v1.0.0", result.stdout)
        self.assertIn(self.baseline, result.stdout)
        self.assertIn("no removals", result.stdout)

    def test_removed_action_input_fails_and_names_baseline_and_entry(self):
        self.stable()
        head = self.remove_input()
        result = self.check()
        self.assertEqual(1, result.returncode)
        self.assertIn("Github/fixture/action.yml:inputs:solution", result.stdout)
        self.assertIn("v1.0.0", result.stdout)
        self.assertIn(head, result.stdout)
        self.assertIn("Refusing unmarked API removal", result.stderr)

    def test_breaking_header_and_both_footer_forms_authorize_removal(self):
        for message in ("feat!: remove obsolete input", "feat(api)!: remove obsolete input",
                        "feat: remove obsolete input\n\nBREAKING CHANGE: solution is removed",
                        "feat: remove obsolete input\n\nBREAKING-CHANGE: solution is removed"):
            with self.subTest(message=message):
                self.git("reset", "--hard", self.baseline)
                if not self.git("tag", "--list", "v1.0.0"):
                    self.stable()
                marked = self.remove_input(message)
                result = self.check()
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("Intentional breaking API change authorized", result.stdout)
                self.assertIn(marked, result.stdout)
                self.assertIn("inputs:solution", result.stdout)

    def test_markers_before_baseline_and_in_subject_prose_do_not_authorize(self):
        for message in ("fix: mention ! without a breaking header",
                        "fix: mention BREAKING CHANGE: without a footer",
                        "fix: mention a marker\n\nA sentence with BREAKING CHANGE: inside"):
            with self.subTest(message=message):
                self.git("reset", "--hard", self.baseline)
                if not self.git("tag", "--list", "v1.0.0"):
                    self.stable()
                self.remove_input(message)
                result = self.check()
                self.assertEqual(1, result.returncode)
        self.git("reset", "--hard", self.baseline)
        self.commit("feat!: declare an earlier breaking change")
        self.stable("v1.1.0")
        self.remove_input()
        self.assertEqual(1, self.check().returncode)

    def test_highest_semver_reachable_stable_wins_over_date_prerelease_and_unreachable(self):
        self.stable("v2.9.0")
        self.stable("v2.10.0")
        self.stable("v99.0.0-rc.1")
        self.stable("v02.20.0")
        self.git("checkout", "-q", "-b", "unreachable")
        self.commit("feat: unrelated branch release")
        self.stable("v100.0.0")
        self.git("checkout", "-q", "main")
        self.commit("fix: extend release history")
        self.stable("v2.8.0")
        self.assertEqual("v2.10.0", API.baseline_tag(self.root, self.git("rev-parse", "HEAD")))
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Public API baseline: v2.10.0", result.stdout)

    def test_api_uses_checked_git_revision_instead_of_working_tree(self):
        self.stable()
        self.write("Github/fixture/action.yml", "invalid: [")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.remove_input()
        previous = self.check(ref=self.baseline)
        self.assertEqual(0, previous.returncode, previous.stderr)
        self.assertEqual(1, self.check().returncode)

    def test_shallow_history_fails_clearly(self):
        self.stable()
        self.commit("fix: add history")
        clone = self.root.parent / "shallow"
        self.git("clone", "--quiet", "--depth=1", self.root.as_uri(), str(clone))
        result = self.check(root=clone)
        self.assertEqual(1, result.returncode)
        self.assertIn("full history and tags", result.stderr)

    def test_invalid_revision_fails_without_bootstrap(self):
        result = self.check(ref="not-a-revision")
        self.assertEqual(1, result.returncode)
        self.assertIn("Public API check failed", result.stderr)
        self.assertNotIn("bootstrap", result.stdout)

    def test_reusable_workflow_inputs_secrets_outputs_and_file_removals_are_protected(self):
        self.stable()
        self.write(".github/workflows/fixture.yml", "name: Synthetic API\non: {workflow_call: {}}\njobs: {}")
        self.commit("fix: adjust workflow API")
        result = self.check()
        self.assertEqual(1, result.returncode)
        for entry in ("inputs:build-mode", "secrets:TOKEN", "outputs:result"):
            self.assertIn(entry, result.stdout)
        self.git("reset", "--hard", self.baseline)
        (self.root / ".github/workflows/fixture.yml").rename(self.root / ".github/workflows/renamed.yml")
        self.commit("fix: rename workflow")
        result = self.check()
        self.assertEqual(1, result.returncode)
        self.assertIn(".github/workflows/fixture.yml:workflow_call", result.stdout)

    def test_test_only_workflow_inputs_may_be_removed_without_breaking_marker(self):
        test_inputs = ("      module-root: {type: string}\n"
                       "      expected-source: {type: string}\n")
        self.write(".github/workflows/fixture.yml", WORKFLOW.replace("    inputs:\n", "    inputs:\n" + test_inputs))
        self.commit("test: introduce test-only workflow inputs")
        self.stable()
        self.write(".github/workflows/fixture.yml", WORKFLOW)
        head = self.commit("fix: remove test-only workflow inputs")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("no removals", result.stdout)
        entries = API.surface(self.root, head)
        self.assertIn(".github/workflows/fixture.yml:inputs:build-mode", entries)

    def test_same_names_remain_public_composite_inputs_secrets_and_outputs(self):
        self.write("Github/fixture/action.yml", ACTION.replace("inputs:\n", "inputs:\n  module-root: {}\n  expected-source: {}\n"))
        self.write(".github/workflows/fixture.yml", WORKFLOW.replace(
            "    secrets:\n", "    secrets:\n      module-root: {}\n").replace(
            "    outputs:\n", "    outputs:\n      expected-source: {value: result}\n"))
        head = self.commit("feat: add public names matching test-only workflow inputs")
        entries = API.surface(self.root, head)
        for entry in ("Github/fixture/action.yml:inputs:module-root", "Github/fixture/action.yml:inputs:expected-source",
                      ".github/workflows/fixture.yml:secrets:module-root", ".github/workflows/fixture.yml:outputs:expected-source"):
            self.assertIn(entry, entries)

    def test_public_schema_property_definition_enum_and_file_removals_are_protected(self):
        self.stable()
        self.write("schemas/fixture.json", '{"properties":{"mode":{"enum":["fast"]}}}')
        self.commit("fix: narrow schema")
        result = self.check()
        self.assertEqual(1, result.returncode)
        for entry in ("schemas/fixture.json/properties/name", "schemas/fixture.json/$defs/coordinate",
                      'schemas/fixture.json/properties/mode/enum/"full"'):
            self.assertIn(entry, result.stdout)
        (self.root / "schemas/fixture.json").unlink()
        self.commit("fix: remove schema")
        self.assertIn("schemas/fixture.json:schema", self.check().stdout)

    def test_additive_schema_annotations_and_instance_values_are_opaque(self):
        self.stable()
        document = copy.deepcopy(SCHEMA)
        document.update(examples=[{"enum": "ordinary-data"}],
                        default={"properties": {"ordinary": "instance-data"}},
                        const={"enum": "also-instance-data"})
        document["properties"]["name"]["examples"] = [{"enum": "nested-instance-data"}]
        self.write("schemas/fixture.json", json.dumps(document))
        self.commit("feat: document schema with instance examples")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("no removals", result.stdout)

    def test_removing_schema_annotations_does_not_remove_public_entries(self):
        document = copy.deepcopy(SCHEMA)
        instance = {"properties": {"ordinary": {}}, "enum": ["instance-data"]}
        document.update(examples=[instance], default=instance, const=instance)
        self.write("schemas/fixture.json", json.dumps(document))
        self.commit("docs: add schema instance annotations")
        self.stable()
        self.write("schemas/fixture.json", json.dumps(SCHEMA))
        self.commit("docs: remove schema instance annotations")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("no removals", result.stdout)

    def test_unordered_schema_alternatives_can_be_reordered_and_extended(self):
        alternatives = [{"properties": {name: {}}} for name in ("a", "b")]
        document = {keyword: copy.deepcopy(alternatives) for keyword in ("allOf", "anyOf", "oneOf")}
        self.write("schemas/fixture.json", json.dumps(document))
        release = self.commit("feat: introduce schema alternatives")
        self.stable()
        for keyword in ("allOf", "anyOf", "oneOf"):
            for extended in (False, True):
                with self.subTest(keyword=keyword, extended=extended):
                    self.git("reset", "--hard", release)
                    changed = copy.deepcopy(document)
                    changed[keyword].reverse()
                    if extended:
                        changed[keyword].insert(0, {"properties": {"c": {}}})
                    self.write("schemas/fixture.json", json.dumps(changed))
                    self.commit("feat: reorder or extend schema alternatives")
                    result = self.check()
                    self.assertEqual(0, result.returncode, result.stderr + result.stdout)
                    self.assertIn("no removals", result.stdout)

    def test_tuple_schema_positions_remain_part_of_the_public_surface(self):
        alternatives = [{"properties": {name: {}}} for name in ("a", "b")]
        document = {keyword: copy.deepcopy(alternatives) for keyword in ("prefixItems", "items")}
        self.write("schemas/fixture.json", json.dumps(document))
        release = self.commit("feat: introduce positional schema tuples")
        self.stable()
        for keyword in ("prefixItems", "items"):
            with self.subTest(keyword=keyword):
                self.git("reset", "--hard", release)
                changed = copy.deepcopy(document)
                changed[keyword].reverse()
                self.write("schemas/fixture.json", json.dumps(changed))
                self.commit("fix: change schema tuple positions")
                result = self.check()
                self.assertEqual(1, result.returncode)
                self.assertIn(f"{keyword}/0/properties/a", result.stdout)
                self.assertIn(f"{keyword}/1/properties/b", result.stdout)

    def test_slash_and_tilde_schema_names_have_distinct_pointer_components(self):
        document = {
            "properties": {"foo/properties/bar": {}, "foo": {"properties": {"bar": {}}},
                           "til~de": {}, "til~0de": {}},
            "$defs": {"def/name": {}, "def~1name": {}, "def~name": {}, "def~0name": {}},
        }
        self.write("schemas/fixture.json", json.dumps(document))
        release = self.commit("feat: introduce schema names with pointer characters")
        self.stable()
        for group, name, escaped in (("properties", "foo/properties/bar", "foo~1properties~1bar"),
                                     ("properties", "til~de", "til~0de"),
                                     ("$defs", "def/name", "def~1name"),
                                     ("$defs", "def~name", "def~0name")):
            with self.subTest(group=group, name=name):
                self.git("reset", "--hard", release)
                changed = copy.deepcopy(document)
                del changed[group][name]
                self.write("schemas/fixture.json", json.dumps(changed))
                self.commit("fix: remove one public schema member")
                result = self.check()
                self.assertEqual(1, result.returncode)
                self.assertIn(f"schemas/fixture.json/{group}/{escaped}", result.stdout)
                self.assertIn("Refusing unmarked API removal", result.stderr)

    def test_imported_props_properties_and_package_names_are_protected(self):
        self.stable()
        self.write("Props/Public.props", '<Project><PropertyGroup><RenamedMode>fast</RenamedMode></PropertyGroup></Project>')
        self.write("Props/Directory.Packages.props", '<Project><ItemGroup><PackageVersion Update="Other.Package" Version="2.0.0" /></ItemGroup></Project>')
        self.commit("fix: adjust public properties")
        result = self.check()
        self.assertEqual(1, result.returncode)
        self.assertIn("Props/Public.props:property:PublicMode", result.stdout)
        self.assertIn("Props/Directory.Packages.props:PackageVersion:Example.Package", result.stdout)

    def test_new_property_and_package_versions_preserve_existing_api(self):
        self.stable()
        self.write("Props/Directory.Packages.props", '<Project><ItemGroup><PackageVersion Include="Example.Package" Version="2.0.0" /><PackageVersion Include="New.Package" Version="1.0.0" /></ItemGroup></Project>')
        self.write("Props/Public.props", '<Project><PropertyGroup><PublicMode>full</PublicMode><NewMode>true</NewMode></PropertyGroup></Project>')
        self.commit("feat: extend catalog")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)

    def test_yaml_on_key_and_yaml_aliases_are_parsed_as_public_names(self):
        self.write(".github/workflows/fixture.yml", """name: Alias fixture
x-inputs: &inputs
  alias-input: {type: string}
on:
  workflow_call:
    inputs: *inputs
jobs: {}
""")
        head = self.commit("feat: use YAML aliases")
        entries = API.surface(self.root, head)
        self.assertIn(".github/workflows/fixture.yml:inputs:alias-input", entries)

    def test_yaml_merge_keys_preserve_inherited_entries_and_explicit_overrides(self):
        self.write(".github/workflows/fixture.yml", """name: Merge fixture
x-call: &call
  inputs:
    inherited-input: {type: string}
  secrets:
    INHERITED_TOKEN: {required: false}
on:
  workflow_call:
    <<: *call
    inputs:
      replacement-input: {type: string}
jobs: {}
""")
        head = self.commit("feat: override inherited API declaration")
        entries = API.surface(self.root, head)
        self.assertIn(".github/workflows/fixture.yml:inputs:replacement-input", entries)
        self.assertIn(".github/workflows/fixture.yml:secrets:INHERITED_TOKEN", entries)
        self.assertNotIn(".github/workflows/fixture.yml:inputs:inherited-input", entries)
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)

    def test_yaml_merge_does_not_hide_duplicate_explicit_keys(self):
        self.write(".github/workflows/fixture.yml", """name: Duplicate merge fixture
x-inputs: &inputs
  inherited-input: {type: string}
on:
  workflow_call:
    inputs:
      <<: *inputs
      repeated-input: {type: string}
      repeated-input: {type: boolean}
jobs: {}
""")
        self.commit("test: introduce duplicate YAML keys")
        result = self.check()
        self.assertEqual(1, result.returncode)
        self.assertIn("Duplicate YAML key: repeated-input", result.stderr)
        self.assertNotIn("bootstrap", result.stdout)

    def test_relative_imports_msbuild_directory_and_cycles_are_read_once(self):
        self.write("Props/Public.props", '<Project><Import Project="$(MSBuildThisFileDirectory)Nested.props" /><PropertyGroup><PublicMode>fast</PublicMode></PropertyGroup></Project>')
        self.write("Props/Nested.props", '<Project><Import Project="Public.props" /><PropertyGroup><NestedMode>full</NestedMode></PropertyGroup></Project>')
        head = self.commit("feat: add transitive public props")
        entries = API.surface(self.root, head)
        self.assertIn("Props/Nested.props:props", entries)
        self.assertIn("Props/Nested.props:property:NestedMode", entries)
        self.assertIn("Props/Public.props:property:PublicMode", entries)

    def test_removed_public_props_file_fails_even_when_no_import_remains(self):
        self.stable()
        (self.root / "Hexalith.Package.props").unlink()
        self.commit("fix: remove package props")
        result = self.check()
        self.assertEqual(1, result.returncode)
        self.assertIn("Hexalith.Package.props:props", result.stdout)
        self.assertIn("Hexalith.Package.props:property:PackageReadmeFile", result.stdout)

    def test_missing_and_escaping_imports_fail_without_bootstrap(self):
        for target, diagnostic in (("Props/Missing.props", "imported public props file missing"),
                                   ("../outside.props", "escapes the repository"),
                                   ("$(UnknownRoot)/Other.props", "cannot statically resolve")):
            with self.subTest(target=target):
                self.write("Hexalith.Build.props", f'<Project><Import Project="{target}" /></Project>')
                self.commit("fix: change public import")
                result = self.check()
                self.assertEqual(1, result.returncode)
                self.assertIn(diagnostic, result.stderr)
                self.assertNotIn("bootstrap", result.stdout)

    def test_invalid_yaml_json_xml_and_duplicate_keys_fail_explicitly(self):
        cases = ((".github/workflows/fixture.yml", "on: [", "Public API check failed"),
                 (".github/workflows/fixture.yml", "on: {}\non: {}", "Duplicate YAML key"),
                 (".github/workflows/fixture.yml", "- invalid", "expected a mapping"),
                 ("schemas/fixture.json", "{", "Public API check failed"),
                 ("schemas/fixture.json", "[]", "JSON Schema must be an object or boolean"),
                 ("Hexalith.Build.props", "<Project>", "Public API check failed"))
        for path, content, diagnostic in cases:
            with self.subTest(path=path, content=content):
                self.git("reset", "--hard", self.baseline)
                self.write(path, content)
                self.commit("fix: malformed public document")
                result = self.check()
                self.assertEqual(1, result.returncode)
                self.assertIn(diagnostic, result.stderr)
                self.assertNotIn("bootstrap", result.stdout)

    def test_test_only_schema_and_nested_action_fixtures_are_not_public(self):
        self.write("schemas/module-root/fixture.json", "malformed JSON test fixture")
        self.write("schemas/expected-source/fixture.json", "malformed JSON test fixture")
        self.write("test/module-root/schemas/fixture.json", "malformed JSON test fixture")
        self.write("Github/fixture/tests/module-root/action.yml", "malformed: [")
        self.commit("test: add intentionally invalid fixtures")
        result = self.check()
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
