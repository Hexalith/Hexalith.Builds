"""Exercise the same README policy used by CI, the local guard and the standalone CLI."""

from contextlib import redirect_stdout
from io import StringIO
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

LIB = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(LIB))
import ci_pipeline
import docs_check

REPOSITORY = "Example/Package"
BADGE = "![CI](https://github.com/Example/Package/actions/workflows/ci.yml/badge.svg)"


class DocumentationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.readme = self.root / "README.md"
        self.readme.write_text(BADGE + "\n")
        self.workflows = self.root / ".github/workflows"
        self.workflows.mkdir(parents=True)
        (self.workflows / "ci.yml").touch()

    def test_current_workflow_passes_and_other_repositories_and_fences_are_ignored(self):
        self.readme.write_text("\n".join([BADGE.replace("badge.svg", "badge.svg?branch=main"),
                                          BADGE.replace("Example/Package", "Example/Other").replace("ci.yml", "old.yml"),
                                          "```markdown", BADGE.replace("ci.yml", "old.yml"), "```"]))
        self.assertEqual([], list(docs_check.inspect(self.root, REPOSITORY)))

    def test_reports_root_and_package_at_exact_lines_without_reading_dependency_sources(self):
        package = self.root / "src/libraries/Product"
        package.mkdir(parents=True)
        (package / "Product.csproj").touch()
        (package / "README.md").write_text("line\n" * 150 + BADGE.replace("ci.yml", "missing.yml") + "\n")
        (self.root / "README.md").unlink()
        (self.root / "references").mkdir()
        (self.root / "references/README.md").write_text(BADGE.replace("ci.yml", "old.yml"))
        found = list(docs_check.inspect(self.root, REPOSITORY))
        self.assertEqual([("README.md", 1, "D-README"), ("src/libraries/Product/README.md", 151, "D-LENGTH"),
                          ("src/libraries/Product/README.md", 151, "D-BADGE")], [f[:3] for f in found])
        self.assertTrue(all(f[-1] for f in found))

    def test_legacy_off_warn_and_fail_have_different_results_for_same_missing_badge(self):
        self.readme.write_text(BADGE.replace("ci.yml", "old.yml"))
        document = {}
        prepared = ci_pipeline.PreparedConfig(self.root, self.root, document, "none", "", ())
        output = StringIO()
        summary = self.root / "summary.md"
        with redirect_stdout(output):
            ci_pipeline.check_documentation(prepared, REPOSITORY)
            document["docs"] = "off"
            ci_pipeline.check_documentation(prepared, REPOSITORY)
            self.assertEqual("", output.getvalue())
            document["docs"] = "warn"
            ci_pipeline.check_documentation(prepared, REPOSITORY, summary=summary)
            finding = json.loads(output.getvalue().splitlines()[0])
            self.assertEqual(("README.md", 1, "D-BADGE"), (finding["file"], finding["line"], finding["rule"]))
            self.assertIn("D-BADGE", summary.read_text())
            document["docs"] = "fail"
            with self.assertRaisesRegex(ci_pipeline.PipelineError, "README checks failed"):
                ci_pipeline.check_documentation(prepared, REPOSITORY)
            self.readme.write_text(BADGE)
            ci_pipeline.check_documentation(prepared, REPOSITORY)

    def test_reusable_prepare_checks_readme_only_modules(self):
        fixtures = json.loads((LIB / "tests/fixtures/contract.json").read_bytes())
        document = next(case["document"] for case in fixtures if case["name"] == "no-solution")
        document["buildsTag"] = "v1.0.0"
        config = self.root / ".github/hexalith-ci.json"
        self.readme.write_text(BADGE.replace("ci.yml", "old.yml"))
        for mode, expected in (("warn", 0), ("fail", 1)):
            with self.subTest(mode=mode):
                document["docs"] = mode
                config.write_text(json.dumps(document))
                result = subprocess.run([sys.executable, "-I", str(LIB / "ci_pipeline.py"), "--phase", "prepare",
                                         "--workspace", str(self.root), "--repository", REPOSITORY],
                                        text=True, capture_output=True)
                self.assertEqual(expected, result.returncode, result.stderr)
                self.assertIn('"rule": "D-BADGE"', result.stdout)

    def test_cli_infers_repository_from_origin_and_refuses_a_stale_local_workflow(self):
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        subprocess.run(["git", "-C", str(self.root), "remote", "add", "origin",
                        "https://github.com/Example/Package.git"], check=True)
        (self.workflows / "ci.yml").unlink()
        result = subprocess.run([sys.executable, "-I", str(LIB / "docs_check.py"), "--root", str(self.root),
                                 "--mode", "fail"], text=True, capture_output=True)
        self.assertEqual(1, result.returncode)
        self.assertEqual("D-BADGE", json.loads(result.stdout.splitlines()[0])["rule"])
