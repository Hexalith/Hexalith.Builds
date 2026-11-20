"""Check the current xUnit summary and the live qualification decision."""
from pathlib import Path
import runpy
import unittest


RUNNER = runpy.run_path(str(Path(__file__).with_name("run-g4-stage3-live-qualification.py")))


class LiveQualificationTests(unittest.TestCase):
    def test_current_suite_requires_every_case_and_verified_cleanup(self):
        summary = RUNNER["SUITE_SUMMARY"]
        probes = {"containers": {"status": "verified", "count": 0}}
        snapshot = {"source": "fixture", "binaries": "fixture"}
        for total, errors, failed, skipped, not_run in (
                (13, 0, 0, 0, 0), (11, 0, 0, 0, 0), (13, 0, 0, 12, 0),
                (13, 0, 0, 0, 1), (13, 1, 0, 0, 0), (13, 0, 1, 0, 0)):
            line = (f"Total: {total}, Errors: {errors}, Failed: {failed}, "
                    f"Skipped: {skipped}, Not Run: {not_run}, Time: 0.247s")
            suite = tuple(map(int, summary.search(line).groups()))
            with self.subTest(suite=suite):
                self.assertEqual(suite == (13, 0, 0, 0, 0),
                                 RUNNER["qualifies"](0, suite, snapshot, snapshot, snapshot, probes))
        self.assertFalse(RUNNER["qualifies"](0, (13, 0, 0, 0, 0), snapshot, snapshot, snapshot,
                                            {"containers": {"status": "unverified", "count": None}}))


if __name__ == "__main__":
    unittest.main()
