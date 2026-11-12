"""One valid/invalid corpus for the public schema and independent stdlib reader."""

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

from jsonschema import Draft202012Validator, FormatChecker


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "Github/lib/perimeter_config.py"
SPEC = importlib.util.spec_from_file_location("perimeter_config", SCRIPT)
CONFIG = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONFIG)
SCHEMA = json.loads((ROOT / "schemas/hexalith.perimeter.v1.json").read_text())
CORPUS = json.loads((Path(__file__).parent / "fixtures/perimeter-contract.json").read_text())


class PerimeterContractTests(unittest.TestCase):
    def test_schema_and_loader_agree_for_the_shared_corpus(self):
        Draft202012Validator.check_schema(SCHEMA)
        validator = Draft202012Validator(SCHEMA, format_checker=FormatChecker())
        self.assertEqual(len(CORPUS), len({case["name"] for case in CORPUS}))
        for case in CORPUS:
            with self.subTest(case=case["name"]):
                document = copy.deepcopy(case["document"])
                before = copy.deepcopy(document)
                errors = list(validator.iter_errors(document))
                try:
                    self.assertIs(document, CONFIG.validate_config(document, source=case["name"]))
                    valid = True
                except CONFIG.ConfigError as error:
                    self.assertIn(case["name"], str(error))
                    valid = False
                self.assertEqual(case["valid"], not errors, errors)
                self.assertEqual(case["valid"], valid)
                self.assertEqual(before, document)
        covered = {entry["kind"] for case in CORPUS if case["valid"] for entry in case["document"]["entries"]}
        schema_kinds = {entry["properties"]["kind"]["const"]
                        for entry in SCHEMA["properties"]["entries"]["items"]["oneOf"]}
        self.assertEqual(schema_kinds, covered)
        self.assertEqual(9, len(covered))

    def test_path_regex_is_ecmascript_compatible(self):
        paths = [entry["path"] for case in CORPUS if isinstance(case["document"], dict)
                 for entry in case["document"].get("entries", []) if isinstance(entry, dict) and "path" in entry]
        pattern = SCHEMA["$defs"]["repositoryPattern"]["pattern"]
        self.assertTrue(any(any(ord(char) > 0xFFFF for char in path) for path in paths))
        result = subprocess.run(["node", "--input-type=module", "-e",
                                 "import fs from 'node:fs'; const x=JSON.parse(fs.readFileSync(0,'utf8'));"
                                 "process.stdout.write(JSON.stringify(x.paths.map(p=>new RegExp(x.pattern,'u').test(p))));"],
                                input=json.dumps({"pattern": pattern, "paths": paths}),
                                text=True, capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        validator = Draft202012Validator(SCHEMA["$defs"]["repositoryPattern"])
        self.assertEqual([validator.is_valid(path) for path in paths], json.loads(result.stdout))

    def test_bytes_and_files_use_identical_strict_serialized_input(self):
        good = b'{"schema":"hexalith.perimeter.v1","entries":[]}'
        invalid = [b'\xff', b'\xef\xbb\xbf' + good, good + b' junk', b'{',
                   b'{"schema":"hexalith.perimeter.v1","entries":[],"entries":[]}',
                   b'{"schema":"hexalith.perimeter.v1","entries":[{"kind":"whole","path":"a","path":"b"}]}',
                   *(good.replace(b'[]', value) for value in (b'NaN', b'Infinity', b'-Infinity')),
                   b'[' * 10000 + b'0' + b']' * 10000]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "perimeter.json"
            path.write_bytes(good)
            self.assertEqual(CONFIG.load_bytes(good), CONFIG.load_config(path))
            for data in invalid:
                with self.subTest(data=data[:80]):
                    path.write_bytes(data)
                    for call in (lambda: CONFIG.load_bytes(data, source=str(path)), lambda: CONFIG.load_config(path)):
                        with self.assertRaises(CONFIG.ConfigError) as caught:
                            call()
                        self.assertIn(str(path), str(caught.exception))
            path.unlink()
            with self.assertRaises(CONFIG.ConfigError):
                CONFIG.load_config(path)

    def test_fixed_identity_kind_diagnostics_and_escaping(self):
        with self.assertRaises(CONFIG.ConfigError) as caught:
            CONFIG.validate_config({"schema": "future", "entries": []}, source="owned")
        self.assertEqual("owned: unsupported schema future. CI/CD is owned by Hexalith.CiCd.", str(caught.exception))
        with self.assertRaises(CONFIG.ConfigError) as caught:
            CONFIG.validate_config({"schema": CONFIG.schema_identity(), "entries": [{"kind": "future"}]}, source="owned")
        self.assertEqual("owned: unsupported perimeter entry kind future", str(caught.exception))
        with self.assertRaises(CONFIG.ConfigError) as caught:
            CONFIG.validate_config({"schema": "\n::error::bad\u2028", "entries": []}, source="x\r")
        self.assertEqual(1, len(str(caught.exception).splitlines()))
        self.assertIn("\\u000a", str(caught.exception))

    def test_identity_comes_from_the_schema_beside_trusted_code(self):
        with tempfile.TemporaryDirectory() as directory:
            schema = Path(directory) / "schema.json"
            schema.write_text('{"properties":{"schema":{"const":"future"}}}')
            CONFIG.schema_identity.cache_clear()
            try:
                with mock.patch.object(CONFIG, "SCHEMA_PATH", schema):
                    self.assertEqual("future", CONFIG.schema_identity())
            finally:
                CONFIG.schema_identity.cache_clear()
        self.assertEqual("hexalith.perimeter.v1", CONFIG.schema_identity())

    def test_cli_is_isolated_and_stdlib_only_and_returns_controlled_errors(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "json.py").write_text("raise RuntimeError('untrusted cwd')")
            (root / "perimeter_config.py").write_text("raise RuntimeError('untrusted cwd')")
            path = root / "document.json"
            environment = os.environ | {"PYTHONPATH": directory}
            for document, code in (({"schema": "hexalith.perimeter.v1", "entries": []}, 0), ({}, 1)):
                path.write_text(json.dumps(document))
                result = subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), str(path)], cwd=root,
                                        env=environment, capture_output=True, text=True, check=False)
                self.assertEqual(code, result.returncode, result.stderr)
                self.assertNotIn("Traceback", result.stderr)
                if code == 0:
                    self.assertEqual(document, json.loads(result.stdout))
                else:
                    self.assertEqual("", result.stdout)

    def test_regex_compile_limits_have_controlled_isolated_cli_diagnostics(self):
        patterns = ("a{4294967296}", "(" * 10000 + "a" + ")" * 10000)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "document.json"
            for pattern in patterns:
                with self.subTest(pattern_prefix=pattern[:30]):
                    document = {"schema": "hexalith.perimeter.v1", "entries": [
                        {"kind": "absent-line", "path": "hook", "pattern": pattern}]}
                    path.write_text(json.dumps(document))
                    with self.assertRaises(CONFIG.ConfigError) as caught:
                        CONFIG.load_config(path)
                    self.assertIn("$/entries/0/pattern: invalid Python regex:", str(caught.exception))
                    result = subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), str(path)],
                                            cwd=root, capture_output=True, text=True, check=False)
                    self.assertEqual(1, result.returncode)
                    self.assertEqual("", result.stdout)
                    self.assertEqual(str(caught.exception) + "\n", result.stderr)
                    self.assertEqual(1, len(result.stderr.splitlines()))
                    self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
