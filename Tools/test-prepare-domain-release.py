#!/usr/bin/env python3
"""Execute the composite's Bash guards against hermetic source/CI fixtures."""

from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
import tempfile
import unittest


ACTION = pathlib.Path(__file__).resolve().parents[1] / "Github/prepare-domain-release/action.yml"
SHA = "0123456789abcdef0123456789abcdef01234567"
OTHER_SHA = "1" * 40


def run_body(name: str) -> str:
    text = ACTION.read_text(encoding="utf-8")
    block = re.search(r"(?ms)^    - name: " + re.escape(name) + r"\n(.*?)(?=^    - name: |\Z)", text)
    if block is None:
        raise AssertionError(f"Missing composite step {name}")
    body = re.search(r"(?ms)^      run: \|\n(.*)", block[1])
    if body is None:
        raise AssertionError(f"Missing Bash body for {name}")
    return "\n".join(line[8:] if line.startswith("        ") else line for line in body[1].splitlines())


class PreparationTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        (self.root / "fixture.slnx").write_text("<Solution />", encoding="utf-8")
        self.packages = [{"id": f"Fixture.Package{i}", "project": f"src/Package{i}.csproj"} for i in range(5)]
        (self.root / "src").mkdir()
        for package in self.packages:
            (self.root / package["project"]).write_text("<Project />", encoding="utf-8")
        self.write_manifest()
        self.environment = os.environ.copy()
        self.environment.update({
            "BUILD_EXECUTION_SHA": SHA, "RESOLVED_ACTION_REPOSITORY": "Hexalith/Hexalith.Builds",
            "RESOLVED_ACTION_REF": SHA, "SOLUTION": "fixture.slnx", "EXPECTED_PACKAGE_COUNT": "5",
            "PACKAGE_MANIFEST": "manifest.json", "SOURCE_BRANCH": "main", "SOURCE_CI_WORKFLOW": "ci.yml",
            "DISPATCH_REF": "refs/heads/main", "DISPATCH_SHA": SHA, "MOCK_CHECKOUT_SHA": SHA,
            "HEXALITH_RELEASE_PUBLISH_ENABLED": "true", "NUGET_USER": "fixture-creator", "MOCK_MAIN_SHA": SHA,
            "REPOSITORY": "Fixture/Module", "GH_TOKEN": "fixture-token",
            "GITHUB_OUTPUT": str(self.root / "output"), "MOCK_CALL_LOG": str(self.root / "calls"),
            "MOCK_RUNS": json.dumps({"workflow_runs": [self.successful_run()]}),
        })
        self.write_executable("git", '#!/bin/sh\n[ "$*" = "rev-parse HEAD" ] || exit 91\nprintf "%s\\n" "$MOCK_CHECKOUT_SHA"\n')
        self.write_executable("gh", '''#!/usr/bin/env python3
import os, pathlib, sys
arguments = sys.argv[1:]
with pathlib.Path(os.environ["MOCK_CALL_LOG"]).open("a") as calls:
    calls.write(" ".join(arguments) + "\\n")
if any("/git/ref/heads/main" in arg for arg in arguments):
    print(os.environ["MOCK_MAIN_SHA"])
elif "repos/Fixture/Module/actions/workflows/ci.yml/runs" in arguments:
    required = ["branch=main", "event=push", "head_sha=" + os.environ["DISPATCH_SHA"], "status=success"]
    if not all(arg in arguments for arg in required):
        raise SystemExit(92)
    print(os.environ["MOCK_RUNS"])
else:
    raise SystemExit(93)
''')
        self.environment["PATH"] = str(self.root) + os.pathsep + self.environment["PATH"]

    def write_executable(self, name: str, body: str) -> None:
        path = self.root / name
        path.write_text(body, encoding="utf-8")
        path.chmod(0o755)

    def write_manifest(self) -> None:
        (self.root / "manifest.json").write_text(json.dumps({"packages": self.packages}), encoding="utf-8")

    @staticmethod
    def successful_run() -> dict[str, str]:
        return {"head_sha": SHA, "head_branch": "main", "event": "push", "status": "completed", "conclusion": "success"}

    def execute(self, name: str, **overrides: str) -> subprocess.CompletedProcess[str]:
        for path in (self.root / "output", self.root / "calls"):
            path.unlink(missing_ok=True)
        return subprocess.run(["bash", "-c", run_body(name)], cwd=self.root,
                              env=self.environment | overrides, text=True, capture_output=True, check=False)

    def assert_rejected(self, name: str, **overrides: str) -> None:
        result = self.execute(name, **overrides)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse((self.root / "output").exists(), "No publication verdict may escape a failed gate")

    def test_action_identity_is_exact(self) -> None:
        name = "Validate approved shared action identity"
        self.assertEqual(0, self.execute(name).returncode)
        for overrides in ({"BUILD_EXECUTION_SHA": "main"}, {"BUILD_EXECUTION_SHA": SHA.upper()},
                          {"RESOLVED_ACTION_REF": "v1"}, {"RESOLVED_ACTION_REF": OTHER_SHA},
                          {"RESOLVED_ACTION_REPOSITORY": "Fixture/Other"}):
            with self.subTest(overrides=overrides):
                self.assert_rejected(name, **overrides)

    def test_preparation_contract_rejects_invalid_inputs_and_source(self) -> None:
        name = "Validate preparation contract"
        result = self.execute(name)
        self.assertEqual(0, result.returncode, result.stderr)
        for key, value in (("EXPECTED_PACKAGE_COUNT", "0"), ("EXPECTED_PACKAGE_COUNT", "5.0"),
                           ("SOLUTION", "fixture.sln"), ("SOLUTION", "../fixture.slnx"),
                           ("PACKAGE_MANIFEST", "missing.json"), ("SOURCE_BRANCH", "feature"),
                           ("SOURCE_CI_WORKFLOW", "../ci.yml"), ("DISPATCH_REF", "refs/heads/feature"),
                           ("DISPATCH_SHA", "bad-sha"), ("MOCK_CHECKOUT_SHA", OTHER_SHA)):
            with self.subTest(key=key, value=value):
                self.assert_rejected(name, **{key: value})

    def test_manifest_count_identity_and_project_paths_fail_closed(self) -> None:
        original = json.loads(json.dumps(self.packages))
        for mutation in ("count", "duplicate-id", "duplicate-project", "bad-id", "unsafe-project", "missing-project", "malformed"):
            with self.subTest(mutation=mutation):
                self.packages = json.loads(json.dumps(original))
                if mutation == "count": self.packages.pop()
                elif mutation == "duplicate-id": self.packages[1]["id"] = self.packages[0]["id"].upper()
                elif mutation == "duplicate-project": self.packages[1]["project"] = self.packages[0]["project"]
                elif mutation == "bad-id": self.packages[0]["id"] = "invalid/id"
                elif mutation == "unsafe-project": self.packages[0]["project"] = "../outside.csproj"
                elif mutation == "missing-project": self.packages[0]["project"] = "src/missing.csproj"
                self.write_manifest()
                if mutation == "malformed": (self.root / "manifest.json").write_text("{", encoding="utf-8")
                self.assert_rejected("Validate preparation contract")

    def test_exact_freeze_skips_creator_and_live_proof(self) -> None:
        name = "Resolve publication freeze and revalidate exact green source"
        for value in ("", "TRUE", "True", " true", "true ", "false", "true\n"):
            with self.subTest(value=value):
                result = self.execute(name, HEXALITH_RELEASE_PUBLISH_ENABLED=value, NUGET_USER="", MOCK_MAIN_SHA=OTHER_SHA)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual("publish-enabled=false\n", (self.root / "output").read_text())
                self.assertIn("publication frozen", result.stdout)
                self.assertFalse((self.root / "calls").exists())

    def test_missing_creator_fails_before_token_exchange_or_live_calls(self) -> None:
        for user in ("", " \t\n"):
            self.assert_rejected("Resolve publication freeze and revalidate exact green source", NUGET_USER=user)
            self.assertFalse((self.root / "calls").exists())

    def test_late_source_proof_rejects_invalid_dispatch_checkout_and_main(self) -> None:
        name = "Resolve publication freeze and revalidate exact green source"
        for key, value in (("SOURCE_BRANCH", "feature"), ("DISPATCH_REF", "refs/heads/feature"),
                           ("DISPATCH_SHA", SHA.upper()), ("MOCK_CHECKOUT_SHA", OTHER_SHA),
                           ("MOCK_CHECKOUT_SHA", "bad"), ("MOCK_MAIN_SHA", OTHER_SHA), ("MOCK_MAIN_SHA", "bad")):
            with self.subTest(key=key, value=value):
                self.assert_rejected(name, **{key: value})

    def test_exact_completed_successful_push_ci_is_required(self) -> None:
        name = "Resolve publication freeze and revalidate exact green source"
        result = self.execute(name)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("publish-enabled=true\n", (self.root / "output").read_text())
        for key, value in (("head_sha", OTHER_SHA), ("head_branch", "feature"), ("event", "pull_request"),
                           ("status", "in_progress"), ("conclusion", "failure")):
            with self.subTest(key=key):
                run = self.successful_run() | {key: value}
                self.assert_rejected(name, MOCK_RUNS=json.dumps({"workflow_runs": [run]}))
        self.assert_rejected(name, MOCK_RUNS='{"workflow_runs": []}')
        self.assert_rejected(name, MOCK_RUNS="malformed")


if __name__ == "__main__":
    unittest.main()
