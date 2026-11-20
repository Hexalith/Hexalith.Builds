"""Fresh evidence, MTP/VSTest reporting, and scoped coverage behavior."""

from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ci_reports as reports


def cobertura(lines, filename="src/Product/Target.cs"):
    values = "".join(f'<line number="{number}" hits="{hits}" {branch}/>' for number, hits, branch in lines)
    return f'<coverage><packages><package><classes><class filename="{filename}"><lines>{values}</lines></class></classes></package></packages></coverage>'


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ci-reports-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.project = "test/Tests.csproj"
        (self.root / "src/Product").mkdir(parents=True)
        (self.root / "src/Product/Target.cs").write_text("// product source fixture")
        self.policy = {"trx": True, "coverage": {"projects": [self.project],
                       "lineScope": ["src/Product/"], "minimumLine": 80, "lineComparison": "greater-than",
                       "branchTargets": ["src/Product/Target.cs"], "minimumBranch": 100}}

    def file(self, text, name="coverage.cobertura.xml"):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        return path

    def passing_session(self, stage="Unit"):
        session = reports.ReportSession(self.root, stage, (self.project,), self.policy)
        directory = session.directory_for(self.project, "net10.0")
        (directory / "results.trx").write_text('<TestRun xmlns="urn:test"><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>')
        (directory / "coverage.cobertura.xml").write_text(cobertura([(1, 1, 'branch="True" condition-coverage="100% (2/2)"')]))
        session.record(self.project, "net10.0", directory, success=True)
        session.finish()
        return session, directory

    def test_engine_specific_switches_do_not_use_vstest_collection_in_mtp(self):
        session = reports.ReportSession(self.root, "Unit", (self.project,), self.policy)
        directory = session.directory_for(self.project, "net10.0")
        mtp = session.options(self.project, directory, runner="Microsoft.Testing.Platform", xunit=True)
        self.assertIn("--report-xunit-trx", mtp)
        self.assertIn("--coverage-output-format", mtp)
        self.assertNotIn("--logger", mtp)
        self.assertNotIn("--collect", mtp)
        other = session.options(self.project, directory, runner="Microsoft.Testing.Platform", xunit=False)
        self.assertIn("--report-trx", other)
        vstest = session.options(self.project, directory, runner="VSTest", xunit=True)
        self.assertIn("trx;LogFileName=results.trx", vstest)
        self.assertIn("XPlat Code Coverage", vstest)
        self.assertNotIn("--coverage", vstest)

    def test_scoped_union_never_double_counts_lines_or_branch_denominators(self):
        first = self.file(cobertura([(1, 1, 'branch="True" condition-coverage="50% (1/2)"'), (2, 0, "")]), "first.xml")
        second = self.file(cobertura([(1, 0, 'branch="true" condition-coverage="100% (2/2)"'), (2, 1, "")],
                                     "/_/src/Product/Target.cs"), "second.xml")
        result = reports.coverage_summary(self.root, [first, second], self.policy["coverage"], enforce=True)
        self.assertEqual((result["linesCovered"], result["linesValid"]), (2, 2))
        self.assertEqual((result["branchesCovered"], result["branchesValid"]), (2, 2))

    def test_exact_eighty_percent_fails_the_tenants_strict_line_gate(self):
        path = self.file(cobertura([(number, int(number != 5), 'branch="True" condition-coverage="100% (2/2)"') for number in range(1, 6)]))
        with self.assertRaisesRegex(reports.ReportError, "greater-than 80"):
            reports.coverage_summary(self.root, [path], self.policy["coverage"], enforce=True)
        policy = dict(self.policy["coverage"], lineComparison="at-least")
        reports.coverage_summary(self.root, [path], policy, enforce=True)

    def test_each_named_target_needs_an_instrumented_branch(self):
        other = "src/Product/Other.cs"
        self.file("// second source", other)
        policy = self.policy["coverage"]
        policy["branchTargets"].append(other)
        first = self.file(cobertura([(1, 1, 'branch="true" condition-coverage="100% (2/2)"')]))
        second = self.file(cobertura([(7, 1, "")], other), "other.xml")
        with self.assertRaisesRegex(reports.ReportError, "branch targets missing: src/Product/Other.cs"):
            reports.coverage_summary(self.root, [first, second], policy, enforce=True)
        self.assertEqual(2, reports.coverage_summary(self.root, [first, second], policy, enforce=False)["branchesValid"])

    def test_missing_named_branch_target_and_missing_scope_fail(self):
        path = self.file(cobertura([(1, 1, "")], "src/Product/Other.cs"))
        with self.assertRaisesRegex(reports.ReportError, "branch targets missing"):
            reports.coverage_summary(self.root, [path], self.policy["coverage"], enforce=True)
        policy = dict(self.policy["coverage"], lineScope=["src/Product/", "src/Other/"])
        (self.root / "src/Other").mkdir()
        with self.assertRaisesRegex(reports.ReportError, "line scopes have no data"):
            reports.coverage_summary(self.root, [path], policy, enforce=True)

    def test_reporting_only_has_no_new_threshold(self):
        path = self.file(cobertura([(1, 0, 'branch="True" condition-coverage="50% (1/2)"')]))
        policy = {"projects": [self.project]}
        result = reports.coverage_summary(self.root, [path], policy, enforce=True)
        self.assertEqual(result["linePercent"], 0)
        self.assertEqual(result["branchPercent"], 50)

    def test_inconsistent_branch_instrumentation_fails(self):
        first = self.file(cobertura([(1, 1, 'branch="True" condition-coverage="100% (2/2)"')]), "first.xml")
        second = self.file(cobertura([(1, 1, 'branch="True" condition-coverage="100% (3/3)"')]), "second.xml")
        with self.assertRaisesRegex(reports.ReportError, "inconsistent.*denominator"):
            reports.coverage_summary(self.root, [first, second], self.policy["coverage"], enforce=True)

    def test_complementary_counts_preserve_best_per_line_instead_of_fabricating_union(self):
        first = self.file(cobertura([(1, 1, 'branch="True" condition-coverage="50% (1/2)"')]), "first.xml")
        second = self.file(cobertura([(1, 1, 'branch="True" condition-coverage="50% (1/2)"')]), "second.xml")
        result = reports.coverage_summary(self.root, [first, second], self.policy["coverage"], enforce=False)
        self.assertEqual((1, 2), (result["branchesCovered"], result["branchesValid"]))
        with self.assertRaisesRegex(reports.ReportError, "below 100"):
            reports.coverage_summary(self.root, [first, second], self.policy["coverage"], enforce=True)

    def test_project_relative_uncovered_pdb_lines_remain_in_the_line_gate(self):
        first = self.file(cobertura([(1, 1, "")]), "covered.xml")
        second = self.file(cobertura([(1, 0, ""), (2, 0, "")], "Product/Target.cs"), "uncovered.xml")
        result = reports.coverage_summary(self.root, [first, second], self.policy["coverage"], enforce=False)
        self.assertEqual((1, 2), (result["linesCovered"], result["linesValid"]))
        with self.assertRaisesRegex(reports.ReportError, "line coverage 50.00"):
            reports.coverage_summary(self.root, [first, second], self.policy["coverage"], enforce=True)

    def test_coverage_scopes_and_branch_targets_must_exist_in_current_source(self):
        for changed in (dict(self.policy["coverage"], lineScope=["src/Missing/"]),
                        dict(self.policy["coverage"], branchTargets=["src/Product/Missing.cs"])):
            with self.subTest(policy=changed), self.assertRaisesRegex(reports.ReportError, "does not exist"):
                reports.ReportSession(self.root, "Unit", (self.project,), {"coverage": changed})

    def test_empty_malformed_zero_test_and_false_success_trx_fail(self):
        examples = ["", "<broken", "<TestRun />",
                    '<TestRun><ResultSummary><Counters total="0" executed="0" passed="0" failed="0" /></ResultSummary></TestRun>',
                    '<TestRun><Results><UnitTestResult outcome="Failed" /></Results><ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="0" failed="1" /></ResultSummary></TestRun>']
        for text in examples:
            with self.subTest(text=text):
                with self.assertRaises(reports.ReportError):
                    reports.validate_trx(self.file(text, "results.trx"), success=True)

    def test_empty_and_malformed_coverage_fail_even_without_thresholds(self):
        for text in ("", "<broken", "<coverage />", cobertura([(0, 1, "")])):
            with self.subTest(text=text):
                with self.assertRaises(reports.ReportError):
                    reports.validate_cobertura(self.file(text))

    def test_trx_counters_must_describe_every_result_including_skips(self):
        valid = '<TestRun><Results><UnitTestResult outcome="Passed" /><UnitTestResult outcome="NotExecuted" /></Results><ResultSummary outcome="Completed"><Counters total="2" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>'
        reports.validate_trx(self.file(valid, "results.trx"), success=True)
        for changed in (valid.replace('total="2"', 'total="100"'),
                        valid.replace('executed="1"', 'executed="2"'),
                        valid.replace('<UnitTestResult outcome="Passed" />', '')):
            with self.subTest(changed=changed):
                with self.assertRaisesRegex(reports.ReportError, "disagree"):
                    reports.validate_trx(self.file(changed, "results.trx"), success=True)

    def test_source_filename_traversal_cannot_enter_a_coverage_scope(self):
        path = self.file(cobertura([(1, 1, "")], "/_/src/Product/../Other/Source.cs"))
        with self.assertRaisesRegex(reports.ReportError, "unsafe source filename"):
            reports.coverage_summary(self.root, [path], self.policy["coverage"], enforce=True)

    def test_dtd_is_forbidden_in_utf8_and_utf16_reports(self):
        document = '<!DOCTYPE coverage [<!ENTITY marker "x">]><coverage />'
        for encoding in ("utf-8", "utf-16"):
            path = self.root / "coverage.cobertura.xml"
            path.write_bytes(document.encode(encoding))
            with self.assertRaisesRegex(reports.ReportError, "DTD/entity"):
                reports.validate_cobertura(path)

    def test_manifest_binds_the_actual_report_bytes(self):
        _, directory = self.passing_session()
        reports.aggregate_reports(self.root, self.policy, ("Unit",), enforce=True)
        (directory / "coverage.cobertura.xml").write_text(cobertura([(1, 0, "")]))
        with self.assertRaisesRegex(reports.ReportError, "changed after collection"):
            reports.aggregate_reports(self.root, self.policy, ("Unit",), enforce=True)

    def test_trx_only_manifests_revalidate_digest_and_existence_at_each_boundary(self):
        self.passing_session()
        policy = {"trx": True}
        path = reports.index_path(self.root, "Unit")
        original = json.loads(path.read_text())
        original["policy"] = policy
        original["results"][0]["paths"] = [value for value in original["results"][0]["paths"] if value.endswith(".trx")]
        original["results"][0]["sha256"] = {value: original["results"][0]["sha256"][value] for value in original["results"][0]["paths"]}
        path.write_text(json.dumps(original))
        reports.aggregate_reports(self.root, policy, ("Unit",), enforce=False)
        trx = self.root / original["results"][0]["paths"][0]
        content = trx.read_bytes()
        trx.write_bytes(content + b"\n")
        with self.assertRaisesRegex(reports.ReportError, "changed after collection"):
            reports.stage_manifest(self.root, "Unit", policy)
        trx.unlink()
        with self.assertRaisesRegex(reports.ReportError, "missing"):
            reports.aggregate_reports(self.root, policy, ("Unit",), enforce=False)

    def test_new_session_invalidates_old_success_and_never_uses_its_reports(self):
        _, old_directory = self.passing_session()
        session = reports.ReportSession(self.root, "Unit", (self.project,), self.policy)
        directory = session.directory_for(self.project, "net10.0")
        self.assertNotEqual(directory, old_directory)
        with self.assertRaisesRegex(reports.ReportError, "missing required"):
            session.record(self.project, "net10.0", directory, success=True)
        with self.assertRaisesRegex(reports.ReportError, "incomplete"):
            reports.aggregate_reports(self.root, self.policy, ("Unit",), enforce=True)

    def test_missing_coverage_project_cannot_satisfy_an_aggregate_gate(self):
        self.passing_session()
        policy = dict(self.policy)
        policy["coverage"] = dict(self.policy["coverage"], projects=[self.project, "test/Missing.csproj"])
        path = reports.index_path(self.root, "Unit")
        document = json.loads(path.read_text())
        document["policy"] = policy
        path.write_text(json.dumps(document))
        with self.assertRaisesRegex(reports.ReportError, "projects have no completed reports"):
            reports.aggregate_reports(self.root, policy, ("Unit",), enforce=True)

    def test_symlinked_report_or_output_directory_is_refused(self):
        session = reports.ReportSession(self.root, "Unit", (self.project,), self.policy)
        directory = session.directory_for(self.project, "net10.0")
        (directory / "results.trx").symlink_to(self.file("<TestRun />", "outside.trx"))
        with self.assertRaisesRegex(reports.ReportError, "escapes"):
            session.record(self.project, "net10.0", directory, success=True)
        other = self.root / "other"
        other.mkdir()
        (other / "TestResults").symlink_to(self.root / "TestResults", target_is_directory=True)
        with self.assertRaisesRegex(reports.ReportError, "symlink"):
            reports.ReportSession(other, "Unit", (), {})


if __name__ == "__main__":
    unittest.main()
