"""Exercise release reduction, the executed adapter and independent v1 validation."""

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

from jsonschema import Draft202012Validator
import yaml


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "Github/lib/release_outcome.py"
SPEC = importlib.util.spec_from_file_location("release_outcome", SCRIPT)
OUTCOME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OUTCOME)
SCHEMA = json.loads((ROOT / "schemas/hexalith.release-outcome.v1.json").read_text())
CORPUS = json.loads((Path(__file__).parent / "fixtures/release-outcome.json").read_text())
DISPATCH_SHA = "a" * 40
SELECTED_SHA = "b" * 40


def prerequisites(*, codeql="success", hook="success", resolve="success"):
    return {"resolve": {"result": resolve}, "codeql": {"result": codeql},
            "nightly-hook": {"result": hook}}


class OutcomeContractTests(unittest.TestCase):
    def test_json_schema_and_stdlib_agree_on_the_shared_corpus(self):
        Draft202012Validator.check_schema(SCHEMA)
        validator = Draft202012Validator(SCHEMA)
        self.assertEqual("https://hexalith.com/schemas/hexalith.release-outcome.v1.json", SCHEMA["$id"])
        self.assertEqual(len(CORPUS), len({case["name"] for case in CORPUS}))
        for case in CORPUS:
            with self.subTest(case=case["name"]):
                document = copy.deepcopy(case["document"])
                original = copy.deepcopy(document)
                self.assertEqual(case["valid"], validator.is_valid(document))
                if case["valid"]:
                    self.assertIs(document, OUTCOME.validate_outcome(document))
                else:
                    with self.assertRaises(OUTCOME.OutcomeError):
                        OUTCOME.validate_outcome(document)
                self.assertEqual(original, document)

    def test_schema_patterns_also_reject_trailing_newlines_in_ecmascript(self):
        for key, value in (("sha", DISPATCH_SHA), ("version", "1.2.3"), ("lastTag", "v1.2.3")):
            result = subprocess.run(["node", "--input-type=module", "-e",
                                     "const [p,v]=JSON.parse(process.argv[1]);"
                                     "const r=new RegExp(p);"
                                     "process.stdout.write(JSON.stringify([r.test(v),r.test(v+'\\n')]));",
                                     json.dumps([SCHEMA["properties"][key]["pattern"], value])],
                                    text=True, capture_output=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual([True, False], json.loads(result.stdout))


class OutcomeReductionTests(unittest.TestCase):
    def decide(self, **changes):
        arguments = dict(dispatch_sha=DISPATCH_SHA, sha=SELECTED_SHA, version="", enabled="false",
                         codeql=["csharp"], nightly_hook="true", needs=prerequisites())
        arguments.update(changes)
        return OUTCOME.reduce_results(**arguments)

    def assert_failed(self, decision, diagnostic=""):
        self.assertEqual("failed", decision["document"]["outcome"])
        self.assertEqual("false", decision["publish"])
        self.assertEqual("true", decision["upload"])
        self.assertTrue(decision["error"])
        self.assertIn(diagnostic, decision["error"])
        Draft202012Validator(SCHEMA).validate(decision["document"])

    def test_successful_gates_under_the_false_switch_are_disabled(self):
        decision = self.decide(version="1.4.0")
        self.assertEqual({"schema": "hexalith.release-outcome.v1", "sha": SELECTED_SHA,
                          "outcome": "disabled", "version": "1.4.0"}, decision["document"])
        self.assertEqual("", decision["error"])

    def test_intentionally_disabled_gates_can_skip(self):
        decision = self.decide(codeql=[], nightly_hook="false", needs=prerequisites(codeql="skipped", hook="skipped"))
        self.assertEqual("disabled", decision["document"]["outcome"])
        # An intentionally disabled gate that nevertheless succeeded is safe.
        self.assertEqual("disabled", self.decide(codeql=[], nightly_hook="false")["document"]["outcome"])
        needs = prerequisites()
        needs.update({name: {"result": "skipped"} for name in ("version", "build", "draft")})
        self.assertEqual("disabled", self.decide(needs=needs)["document"]["outcome"])

    def test_failure_and_cancellation_win_over_all_other_decisions(self):
        for job in ("resolve", "codeql", "nightly-hook", "version", "build", "draft"):
            for result in ("failure", "cancelled"):
                for choices in ({"enabled": "false"}, {"enabled": "true", "noop_reason": "no-version"},
                                {"enabled": "true", "publish": "true", "version": "2.0.0"}):
                    with self.subTest(job=job, result=result, choices=choices):
                        needs = prerequisites()
                        needs.update({name: {"result": "success", "outputs": {"publish": "true"}}
                                      for name in ("version", "build", "draft")})
                        needs[job]["result"] = result
                        self.assert_failed(self.decide(needs=needs, **choices), result)

    def test_missing_unknown_and_unexpected_skips_cannot_be_success(self):
        for name in ("resolve", "codeql", "nightly-hook"):
            needs = prerequisites()
            del needs[name]
            self.assert_failed(self.decide(needs=needs), "missing")
        for result in ("", "pending", None, "neutral"):
            needs = prerequisites()
            needs["codeql"]["result"] = result
            self.assert_failed(self.decide(needs=needs), "unknown result")
        for job in ("resolve", "codeql", "nightly-hook", "unexpected"):
            needs = prerequisites()
            needs[job] = {"result": "skipped"}
            self.assert_failed(self.decide(needs=needs))

    def test_known_release_identity_is_preserved_after_failure(self):
        decision = self.decide(needs=prerequisites(resolve="failure"), version="2.3.4")
        self.assert_failed(decision)
        self.assertEqual(SELECTED_SHA, decision["document"]["sha"])
        self.assertEqual("2.3.4", decision["document"]["version"])
        early = self.decide(sha="", needs=prerequisites(resolve="failure"))
        self.assert_failed(early)
        self.assertEqual(DISPATCH_SHA, early["document"]["sha"])
        self.assert_failed(self.decide(sha=""), "did not select a SHA")

    def test_unknown_switch_or_configuration_fails_closed(self):
        for switch in ("", "False", "TRUE", "0", "false\n"):
            self.assert_failed(self.decide(enabled=switch), "must be exactly true or false")
        for changes in ({"codeql": None}, {"codeql": ["actions"]}, {"codeql": ["csharp", "csharp"]},
                        {"codeql": [["csharp"]]}, {"nightly_hook": ""}, {"nightly_hook": "yes"},
                        {"mode": "unknown"}, {"publish": "yes"}, {"noop_reason": "unknown"}):
            with self.subTest(changes=changes):
                self.assert_failed(self.decide(**changes))

    def test_enabled_gates_without_publication_stages_fail_explicitly(self):
        self.assert_failed(self.decide(enabled="true"), "release publication stages are not installed")

    def test_only_explicit_successful_downstream_noops_are_nothing(self):
        for reason, owner in (("no-version", "version"), ("stale-draft-deleted", "draft")):
            with self.subTest(reason=reason):
                self.assert_failed(self.decide(enabled="true", noop_reason=reason), "requires successful")
                needs = prerequisites()
                needs[owner] = {"result": "success"}
                decision = self.decide(enabled="true", noop_reason=reason, needs=needs)
                self.assertEqual("nothing", decision["document"]["outcome"])
                self.assertEqual("", decision["error"])
                self.assert_failed(self.decide(noop_reason=reason, needs=needs), "disabled release")
        needs = prerequisites()
        needs.update(version={"result": "success"}, build={"result": "skipped"}, draft={"result": "skipped"})
        self.assertEqual("nothing", self.decide(enabled="true", noop_reason="no-version", needs=needs)["document"]["outcome"])

    def test_only_validated_successful_drafts_can_transfer_outcome_ownership(self):
        needs = prerequisites()
        needs.update({name: {"result": "success"} for name in ("version", "build", "draft")})
        needs["draft"]["outputs"] = {"publish": "true", "sha": SELECTED_SHA, "version": "2.0.0"}
        choices = dict(enabled="true", publish="true", version="2.0.0", needs=needs)
        decision = self.decide(**choices)
        self.assertIsNone(decision["document"])
        self.assertEqual("false", decision["upload"])
        self.assertEqual("true", decision["publish"])
        for changes in ({"enabled": "false"}, {"version": ""}, {"noop_reason": "no-version"}):
            self.assert_failed(self.decide(**(choices | changes)))
        for missing in ("version", "build", "draft"):
            incomplete = copy.deepcopy(needs)
            del incomplete[missing]
            self.assert_failed(self.decide(**(choices | {"needs": incomplete})))
        for outputs in ({}, {"publish": "false"}, {"publish": True}, None,
                        {"publish": "true", "version": "2.0.0"},
                        {"publish": "true", "sha": SELECTED_SHA},
                        {"publish": "true", "sha": DISPATCH_SHA, "version": "2.0.0"},
                        {"publish": "true", "sha": SELECTED_SHA, "version": "2.0.1"}):
            invalid = copy.deepcopy(needs)
            invalid["draft"]["outputs"] = outputs
            for mode in ("create", "complete"):
                self.assert_failed(self.decide(**(choices | {"needs": invalid, "mode": mode})))
        complete = copy.deepcopy(needs)
        complete["version"]["result"] = complete["build"]["result"] = "skipped"
        self.assertEqual("true", self.decide(**(choices | {"mode": "complete", "needs": complete}))["publish"])


class OutcomeAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="release-outcome-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.directory = self.root / "artifact"
        self.output = self.root / "github-output"
        self.environment = dict(os.environ, RELEASE_DISPATCH_SHA=DISPATCH_SHA, RELEASE_SHA=SELECTED_SHA,
                                RELEASE_ENABLED="false", RELEASE_CODEQL='["csharp"]', RELEASE_NIGHTLY_HOOK="true",
                                RELEASE_NEEDS=json.dumps(prerequisites()), RELEASE_VERSION="", RELEASE_PUBLISH="false",
                                RELEASE_NOOP_REASON="", RELEASE_MODE="create", RELEASE_OUTPUT_DIRECTORY=str(self.directory),
                                GITHUB_OUTPUT=str(self.output), GITHUB_ACTION_PATH=str(ROOT / "Github/release-outcome"))
        self.environment["PATH"] = str(Path(sys.executable).parent) + os.pathsep + os.environ.get("PATH", "")

    def adapter(self, **changes):
        self.environment.update(changes)
        action = yaml.safe_load((ROOT / "Github/release-outcome/action.yml").read_text())
        results = []
        for step in action["runs"]["steps"]:
            result = subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", step["run"]],
                                    cwd=self.root, env=self.environment, text=True, capture_output=True, check=False)
            results.append(result)
            if result.returncode:
                break
        return results[-1]

    def recorded_outputs(self):
        return dict(line.split("=", 1) for line in self.output.read_text().splitlines())

    def document(self):
        path = self.directory / "outcome.json"
        document = json.loads(path.read_text())
        Draft202012Validator(SCHEMA).validate(document)
        self.assertEqual([path], list(self.directory.iterdir()))
        return document

    def test_actual_adapter_writes_one_valid_disabled_outcome(self):
        result = self.adapter(RELEASE_VERSION="3.0.0")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("disabled", self.document()["outcome"])
        self.assertEqual("3.0.0", self.document()["version"])
        self.assertEqual({"upload": "true", "publish": "false", "path": str(self.directory / "outcome.json"),
                          "outcome": "disabled"}, self.recorded_outputs())

    def test_actual_adapter_never_imports_module_owned_python(self):
        (self.root / "json.py").write_text("from pathlib import Path\nPath('module-executed').touch()\nraise RuntimeError('module code')\n")
        result = self.adapter()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("disabled", self.document()["outcome"])
        self.assertFalse((self.root / "module-executed").exists())

    def test_failed_decision_still_exposes_upload_path_and_retains_failure(self):
        result = self.adapter(RELEASE_NEEDS=json.dumps(prerequisites(hook="failure")))
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("nightly-hook", result.stderr)
        self.assertEqual("failed", self.document()["outcome"])
        self.assertEqual("true", self.recorded_outputs()["upload"])
        self.assertEqual("false", self.recorded_outputs()["publish"])

    def test_actual_adapter_falls_back_to_dispatch_on_early_or_malformed_metadata(self):
        for changes in ({"RELEASE_SHA": "", "RELEASE_NEEDS": json.dumps(prerequisites(resolve="failure"))},
                        {"RELEASE_SHA": "malformed", "RELEASE_VERSION": "v2.0.0"},
                        {"RELEASE_SHA": "", "RELEASE_NEEDS": "not JSON"}):
            with self.subTest(changes=changes):
                self.output.unlink(missing_ok=True)
                result = self.adapter(**changes)
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                self.assertEqual({"schema": "hexalith.release-outcome.v1", "sha": DISPATCH_SHA,
                                  "outcome": "failed"}, self.document())

    def test_actual_adapter_preserves_selected_identity_and_escapes_external_diagnostics(self):
        needs = prerequisites()
        needs["\n::error::injected"] = {"result": "failure"}
        result = self.adapter(RELEASE_VERSION="1.2.3", RELEASE_NEEDS=json.dumps(needs))
        self.assertEqual(1, result.returncode)
        self.assertEqual(SELECTED_SHA, self.document()["sha"])
        self.assertEqual("1.2.3", self.document()["version"])
        self.assertNotIn("\n::error::injected", result.stderr)

    def test_actual_adapter_does_no_file_or_upload_work_for_publisher_handoff(self):
        needs = prerequisites()
        needs.update({name: {"result": "success"} for name in ("version", "build", "draft")})
        needs["draft"]["outputs"] = {"publish": "true", "sha": SELECTED_SHA, "version": "1.2.3"}
        result = self.adapter(RELEASE_ENABLED="true", RELEASE_PUBLISH="true", RELEASE_VERSION="1.2.3",
                              RELEASE_NEEDS=json.dumps(needs))
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse(self.directory.exists())
        self.assertEqual({"upload": "false", "publish": "true", "path": "", "outcome": ""}, self.recorded_outputs())

    def test_actual_adapter_unknown_results_cannot_authorize_disabled_or_handoff(self):
        for needs in ({}, {"resolve": {"result": "success"}}, {"resolve": {"result": "pending"}}):
            result = self.adapter(RELEASE_NEEDS=json.dumps(needs))
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertEqual("failed", self.document()["outcome"])

    def test_reexecution_replaces_the_one_document_without_leaving_temporary_files(self):
        self.assertEqual(0, self.adapter().returncode)
        self.assertEqual("disabled", self.document()["outcome"])
        self.assertEqual(1, self.adapter(RELEASE_ENABLED="true").returncode)
        self.assertEqual("failed", self.document()["outcome"])


if __name__ == "__main__":
    unittest.main()
