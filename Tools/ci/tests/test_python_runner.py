"""Prove explicit groups execute under the selected Python and preserve failures."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "run_python_tests.py"
SPEC = importlib.util.spec_from_file_location("quality_python_runner", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class PythonRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="builds-suite-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for directory in ("Tools", "Github/fixture/tests", "Tools/ci/tests"):
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        self.log = self.root / "executions.jsonl"

    def script(self, path, *, unit=False, code=0):
        action = (f"with open({str(self.log)!r}, 'a') as log: "
                  f"log.write(json.dumps([{path!r}, sys.executable, os.getcwd()]) + '\\n')")
        content = "import json, os, sys\n"
        if unit:
            content += ("import unittest\nclass ExecutedTest(unittest.TestCase):\n"
                        "    def test_behavior(self):\n"
                        f"        {action}\n"
                        f"        self.assertEqual(0, {code})\n")
        else:
            content += action + f"\nraise SystemExit({code})\n"
        (self.root / path).write_text(content)

    def test_hyphen_underscore_assertion_and_discovery_groups_run_with_same_interpreter(self):
        paths = ("Tools/test-hyphen.py", "Tools/test_assertions.py", "Github/fixture/tests/test_nested.py",
                 "Tools/ci/tests/test_quality.py")
        for path in paths:
            self.script(path, unit="/tests/" in path)
        with contextlib.redirect_stdout(io.StringIO()):
            result = RUNNER.run(self.root)
        self.assertEqual(0, result)
        records = [json.loads(line) for line in self.log.read_text().splitlines()]
        self.assertEqual(set(paths), {row[0] for row in records})
        self.assertEqual({sys.executable}, {row[1] for row in records})
        for path, _, cwd in records:
            expected = self.root / Path(path).parent if "/tests/" in path else self.root
            self.assertEqual(str(expected), cwd)

    def test_failure_in_assertion_script_fails_suite_and_other_groups_still_run(self):
        self.script("Tools/test_failure.py", code=17)
        self.script("Tools/test_next.py")
        self.script("Github/fixture/tests/test_success.py", unit=True)
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as errors:
            result = RUNNER.run(self.root)
        self.assertEqual(1, result)
        self.assertIn("FAILED (17)", errors.getvalue())
        records = [json.loads(line) for line in self.log.read_text().splitlines()]
        self.assertEqual(3, len(records))

    def test_failure_in_discovered_group_fails_suite(self):
        self.script("Github/fixture/tests/test_failure.py", unit=True, code=1)
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, RUNNER.run(self.root))

    def test_missing_required_group_refuses_partial_suite(self):
        (self.root / "Tools/ci/tests").rmdir()
        with self.assertRaisesRegex(ValueError, "Missing Python test group"):
            RUNNER.commands(self.root)


if __name__ == "__main__":
    unittest.main()
