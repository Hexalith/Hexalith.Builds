"""Exercise the schema and independent stdlib loader on identical documents."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

from jsonschema import Draft202012Validator


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "Github/lib/ci_config.py"
SPEC = importlib.util.spec_from_file_location("ci_config", SCRIPT)
CONFIG = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONFIG)
SCHEMA = json.loads((ROOT / "schemas/hexalith.ci.v1.json").read_text(encoding="utf-8"))
CORPUS = json.loads((Path(__file__).parent / "fixtures/contract.json").read_text(encoding="utf-8"))
BUILDS = "Hexalith/Hexalith.Builds"
MODULE = "Example/Fixture"


class SharedContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        Draft202012Validator.check_schema(SCHEMA)
        cls.validator = Draft202012Validator(SCHEMA)

    def test_shared_positive_and_negative_fixtures_agree(self):
        self.assertEqual(len(CORPUS), len({case["name"] for case in CORPUS}))
        self.assertTrue(any(case["valid"] for case in CORPUS))
        self.assertTrue(any(not case["valid"] for case in CORPUS))
        for case in CORPUS:
            with self.subTest(case=case["name"]):
                document = copy.deepcopy(case["document"])
                original = copy.deepcopy(document)
                schema_errors = list(self.validator.iter_errors(document))
                try:
                    result = CONFIG.validate_config(document, repository=BUILDS, source=case["name"])
                    loader_valid = True
                    self.assertIs(result, document)
                except CONFIG.ConfigError as error:
                    loader_valid = False
                    self.assertIn(case["name"], str(error))
                self.assertEqual(case["valid"], not schema_errors, schema_errors)
                self.assertEqual(case["valid"], loader_valid)
                self.assertEqual(original, document, "validation must not insert defaults or change values")

    def test_builds_hand_written_configuration_validates_with_both_engines(self):
        path = ROOT / ".github/hexalith-ci.json"
        document = json.loads(path.read_text(encoding="utf-8"))
        self.assertNotIn("buildsTag", document)
        self.validator.validate(document)
        self.assertEqual(document, CONFIG.load_config(path, repository=BUILDS))
        self.assertEqual("Hexalith.Builds.slnx", document["solution"])
        self.assertEqual(set(SCHEMA["required"]), document.keys())

    def test_renderer_defaults_are_metadata_and_every_knob_remains_required(self):
        defaults = {"solution": "none", "dapr": False, "browsers": False, "registry": False,
                    "tests": {"integration": [], "exclude": {}},
                    "hooks": [], "jobs": {}, "submodules": []}
        for key, value in defaults.items():
            with self.subTest(key=key):
                self.assertEqual(value, SCHEMA["properties"][key]["default"])
                self.assertIn(key, SCHEMA["required"])
        self.assertIn("codeql", SCHEMA["required"])
        self.assertNotIn("default", SCHEMA["properties"]["codeql"])
        for solution, expected in (("none", []), ("Fixture.slnx", ["csharp"])):
            with self.subTest(solution=solution):
                defaults = []
                for rule in SCHEMA["allOf"]:
                    branch = "then" if Draft202012Validator(rule["if"]).is_valid({"solution": solution}) else "else"
                    codeql = rule.get(branch, {}).get("properties", {}).get("codeql", {})
                    if "default" in codeql:
                        defaults.append(codeql["default"])
                self.assertEqual([expected], defaults, "exactly one applicable CodeQL default is required")

    def test_positive_corpus_covers_every_schema_language_and_hook(self):
        for key in ("codeql", "hooks"):
            with self.subTest(key=key):
                covered = {value for case in CORPUS if case["valid"] for value in case["document"][key]}
                self.assertEqual(set(SCHEMA["properties"][key]["items"]["enum"]), covered)

    def test_public_path_regex_agrees_with_ecmascript_for_corpus_paths(self):
        paths = set()
        for case in CORPUS:
            document = case["document"]
            if not isinstance(document, dict):
                continue
            solution = document.get("solution")
            if isinstance(solution, str) and solution != "none":
                paths.add(solution)
            submodules = document.get("submodules")
            if isinstance(submodules, list):
                paths.update(value for value in submodules if isinstance(value, str))
            tests = document.get("tests")
            if isinstance(tests, dict):
                integration = tests.get("integration")
                if isinstance(integration, list):
                    paths.update(value for value in integration if isinstance(value, str))
                exclude = tests.get("exclude")
                if isinstance(exclude, dict):
                    paths.update(exclude)
        paths = sorted(paths)
        definition = SCHEMA["$defs"]["repositoryPath"]
        result = subprocess.run(["node", "--input-type=module", "-e",
                                 "import fs from 'node:fs';"
                                 "const {pattern, paths} = JSON.parse(fs.readFileSync(0, 'utf8'));"
                                 "const regex = new RegExp(pattern);"
                                 "process.stdout.write(JSON.stringify(paths.map(path => regex.test(path))));"],
                                input=json.dumps({"pattern": definition["pattern"], "paths": paths}),
                                text=True, capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        validator = Draft202012Validator(definition)
        self.assertEqual([validator.is_valid(path) for path in paths], json.loads(result.stdout))
        for separator in ("\u2028", "\u2029"):
            self.assertTrue(any(separator in path and validator.is_valid(path) for path in paths))


class ContextAndInputTests(unittest.TestCase):
    def setUp(self):
        self.document = copy.deepcopy(CORPUS[0]["document"])
        self.temporary = tempfile.TemporaryDirectory(prefix="ci-config-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "hexalith-ci.json"

    def write(self, document=None):
        self.path.write_text(json.dumps(self.document if document is None else document), encoding="utf-8")
        return self.path

    def cli(self, *arguments, environment=None, no_site=False):
        return subprocess.run([sys.executable, "-I", *(["-S"] if no_site else []), str(SCRIPT), str(self.path), *arguments],
                              cwd=self.root, env=environment, text=True, capture_output=True, check=False)

    def test_tag_is_required_for_other_repositories_and_builds_samples(self):
        del self.document["buildsTag"]
        # Repository identity cannot be represented by a document-only JSON Schema.
        Draft202012Validator(SCHEMA).validate(self.document)
        for repository, module_root in ((MODULE, ""), (BUILDS, "Samples/Fixture"),
                                        ("Hexalith/Hexalith.Builds-Fork", ""), (BUILDS.lower(), "")):
            with self.subTest(repository=repository, module_root=module_root):
                with self.assertRaisesRegex(CONFIG.ConfigError, "buildsTag.*required outside Builds"):
                    CONFIG.validate_config(self.document, repository=repository, module_root=module_root)
        self.assertIs(self.document, CONFIG.validate_config(self.document, repository=BUILDS))

    def test_explicit_tag_works_for_modules_and_samples(self):
        self.assertIs(self.document, CONFIG.validate_config(self.document, repository=MODULE))
        self.assertIs(self.document, CONFIG.validate_config(self.document, repository=BUILDS, module_root="Samples/Fixture"))

    def test_repository_context_cannot_be_supplied_in_document(self):
        self.document["repository"] = BUILDS
        with self.assertRaisesRegex(CONFIG.ConfigError, "unknown keys: repository"):
            CONFIG.validate_config(self.document, repository=MODULE)
        with self.assertRaises(TypeError):
            CONFIG.validate_config(self.document)
        with self.assertRaises(TypeError):
            CONFIG.load_config(self.write())

    def test_exclusion_requires_an_enabled_action(self):
        # Dynamic membership in arbitrary job keys is an AD-3 semantic check,
        # beyond standard JSON Schema. Test these separately from the corpus.
        self.document["tests"]["exclude"] = {"test/Fixture.csproj": "windows-tests"}
        Draft202012Validator(SCHEMA).validate(self.document)
        with self.assertRaisesRegex(CONFIG.ConfigError, "not an enabled hook or job"):
            CONFIG.validate_config(self.document, repository=MODULE)
        self.document["jobs"] = {"windows-tests": {"runner": "windows-latest"}}
        CONFIG.validate_config(self.document, repository=MODULE)
        for hook in sorted(CONFIG.HOOKS):
            self.document["tests"]["exclude"]["test/Fixture.csproj"] = hook
            with self.assertRaisesRegex(CONFIG.ConfigError, "not an enabled hook or job"):
                CONFIG.validate_config(self.document, repository=MODULE)
            self.document["hooks"] = [hook]
            CONFIG.validate_config(self.document, repository=MODULE)
            self.document["hooks"] = []

    def test_strict_json_rejects_duplicate_keys_and_non_json_constants(self):
        text = json.dumps(self.document)
        bad = [text.replace('"dapr": false', '"dapr": false, "dapr": true'),
               text.replace('"integration": []', '"integration": [], "integration": []'),
               text.replace('"registry": false', '"registry": NaN'),
               text.replace('"registry": false', '"registry": Infinity'),
               text.replace('"registry": false', '"registry": -Infinity'),
               text + " trailing", "\ufeff" + text, "{\n broken json"]
        for content in bad:
            with self.subTest(content=content):
                self.path.write_text(content, encoding="utf-8")
                with self.assertRaises(CONFIG.ConfigError) as caught:
                    CONFIG.load_config(self.path, repository=MODULE)
                self.assertIn(str(self.path), str(caught.exception))

    def test_unreadable_or_invalid_utf8_files_fail_with_source(self):
        with self.assertRaises(CONFIG.ConfigError) as caught:
            CONFIG.load_config(self.path, repository=MODULE)
        self.assertIn(str(self.path), str(caught.exception))
        self.path.write_bytes(b"\xff")
        with self.assertRaises(CONFIG.ConfigError):
            CONFIG.load_config(self.path, repository=MODULE)

    def test_deep_json_is_a_controlled_import_and_cli_failure(self):
        # Python 3.12 reaches its recursion limit; newer decoders can parse the
        # complete arrays, which still fail the required root-object contract.
        for content in ("[" * 10000 + "0" + "]" * 10000, "[" * 10000):
            with self.subTest(complete=content.endswith("]")):
                self.path.write_text(content, encoding="utf-8")
                with self.assertRaises(CONFIG.ConfigError) as caught:
                    CONFIG.load_config(self.path, repository=MODULE)
                self.assertIn(str(self.path), str(caught.exception))
                result = self.cli("--repository", MODULE)
                self.assertEqual(1, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertEqual(str(caught.exception) + "\n", result.stderr)
                self.assertNotIn("Traceback", result.stderr)
                self.assertEqual(1, len(result.stderr.splitlines()))

    def test_schema_identity_is_read_beside_loader_not_from_current_directory(self):
        self.assertEqual(SCHEMA["properties"]["schema"]["const"], CONFIG.schema_identity())
        alternate = self.root / "schema.json"
        alternate.write_text(json.dumps({"properties": {"schema": {"const": "hexalith.ci.fixture"}}}), encoding="utf-8")
        with mock.patch.object(CONFIG, "SCHEMA_PATH", alternate):
            CONFIG.schema_identity.cache_clear()
            try:
                with self.assertRaisesRegex(CONFIG.ConfigError, "unsupported schema hexalith.ci.v1"):
                    CONFIG.validate_config(self.document, repository=MODULE)
                self.document["schema"] = "hexalith.ci.fixture"
                CONFIG.validate_config(self.document, repository=MODULE)
            finally:
                CONFIG.schema_identity.cache_clear()

    def test_isolated_cli_prints_same_values_and_ignores_pythonpath(self):
        self.write()
        (self.root / "json.py").write_text("raise RuntimeError('untrusted module executed')", encoding="utf-8")
        environment = os.environ.copy()
        environment["PYTHONPATH"] = str(self.root)
        for no_site in (False, True):
            with self.subTest(no_site=no_site):
                result = self.cli("--repository", MODULE, environment=environment, no_site=no_site)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(self.document, json.loads(result.stdout))
                self.assertEqual("", result.stderr)

    def test_cli_requires_trusted_repository_and_checks_samples(self):
        del self.document["buildsTag"]
        self.write()
        self.assertEqual(2, self.cli().returncode)
        self.assertEqual(0, self.cli("--repository", BUILDS).returncode)
        self.assertEqual(1, self.cli("--repository", MODULE).returncode)
        self.assertEqual(1, self.cli("--repository", BUILDS, "--module-root", "Samples/Fixture").returncode)

    def test_unknown_schema_has_exact_fixed_diagnostic_in_import_and_cli(self):
        self.document["schema"] = "hexalith.ci.v99"
        expected = f"{self.path}: unsupported schema hexalith.ci.v99. CI/CD is owned by Hexalith.CiCd."
        with self.assertRaises(CONFIG.ConfigError) as caught:
            CONFIG.load_config(self.write(), repository=MODULE)
        self.assertEqual(expected, str(caught.exception))
        result = self.cli("--repository", MODULE)
        self.assertEqual(1, result.returncode)
        self.assertEqual("", result.stdout)
        self.assertEqual(expected + "\n", result.stderr)

    def test_cli_reports_nested_location_without_executing_paths(self):
        marker = self.root / "executed"
        self.document["tests"]["integration"] = [f"${{{{ touch {marker} }}}}/Fixture.csproj"]
        self.write()
        result = self.cli("--repository", MODULE)
        self.assertEqual(1, result.returncode)
        self.assertIn("$/tests/integration/0", result.stderr)
        self.assertFalse(marker.exists())

    def test_untrusted_diagnostic_values_keys_and_path_locations_stay_on_one_line(self):
        controls = "\n::error::injected\r\x1b\x85\u2028\u2029"
        original = copy.deepcopy(self.document)
        for surface in ("schema", "unknown-key", "path-key"):
            with self.subTest(surface=surface):
                self.document = copy.deepcopy(original)
                if surface == "schema":
                    self.document["schema"] = "hexalith.ci.v99" + controls
                elif surface == "unknown-key":
                    self.document["unknown" + controls] = False
                else:
                    self.document["tests"]["exclude"] = {"test/" + controls + "/Fixture.csproj": "nightly"}
                    self.document["hooks"] = ["nightly"]
                with self.assertRaises(CONFIG.ConfigError) as caught:
                    CONFIG.load_config(self.write(), repository=MODULE)
                self.assertEqual(1, len(str(caught.exception).splitlines()))
                for escaped in ("\\u000a", "\\u000d", "\\u001b", "\\u0085", "\\u2028", "\\u2029"):
                    self.assertIn(escaped, str(caught.exception))
                result = self.cli("--repository", MODULE)
                self.assertEqual(1, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertEqual(str(caught.exception) + "\n", result.stderr)
                self.assertEqual(1, len(result.stderr.splitlines()))


if __name__ == "__main__":
    unittest.main()
