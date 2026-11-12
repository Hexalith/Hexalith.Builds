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

sys.path.insert(0, str(Path(__file__).resolve().parent))
from actions_expressions import Evaluator, parse_outputs, run_composite


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


def handoff(version="2.0.0"):
    needs = prerequisites()
    digest = "sha256:" + "c" * 64
    needs.update(
        version={"result": "success", "outputs": {"version": version, "last-tag": "v1.9.0", "no-version": "false"}},
        build={"result": "success", "outputs": {"artifact-id": "123", "artifact-digest": digest}},
        draft={"result": "success", "outputs": {"publish": "true", "sha": SELECTED_SHA, "version": version,
               "last-tag": "v1.9.0", "draft-id": "456", "artifact-id": "123", "artifact-digest": digest,
               "artifact-run": "789"}})
    return needs


def stale_draft(version="2.0.0"):
    needs = handoff(version)
    needs["version"]["result"] = needs["build"]["result"] = "skipped"
    needs["draft"]["outputs"].update(publish="false", **{"noop-reason": "stale-draft-deleted"})
    return needs


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

    def test_reducer_shares_the_ci_configuration_language_vocabulary(self):
        import ci_config
        self.assertIs(ci_config.LANGUAGES, OUTCOME.LANGUAGES)
        self.assertEqual("disabled", self.decide(codeql=sorted(ci_config.LANGUAGES))["document"]["outcome"])

    def test_enabled_gates_without_publication_stages_fail_explicitly(self):
        self.assert_failed(self.decide(enabled="true"), "release publication did not produce a handoff or validated no-op")

    def test_only_explicit_successful_downstream_noops_are_nothing(self):
        self.assert_failed(self.decide(enabled="true", noop_reason="no-version"), "requires successful")
        needs = prerequisites()
        needs.update(version={"result": "success"}, build={"result": "skipped"}, draft={"result": "skipped"})
        decision = self.decide(enabled="true", noop_reason="no-version", needs=needs)
        self.assertEqual("nothing", decision["document"]["outcome"])
        self.assertEqual("", decision["error"])
        self.assert_failed(self.decide(noop_reason="no-version", needs=needs), "disabled release")
        self.assert_failed(self.decide(enabled="true", mode="complete", noop_reason="no-version", needs=needs),
                           "requires create mode")

    def test_stale_deletion_requires_the_matching_successful_complete_draft(self):
        choices = dict(enabled="true", version="2.0.0", mode="complete",
                       noop_reason="stale-draft-deleted", needs=stale_draft())
        decision = self.decide(**choices)
        self.assertEqual({"schema": "hexalith.release-outcome.v1", "sha": SELECTED_SHA,
                          "outcome": "nothing", "version": "2.0.0"}, decision["document"])
        self.assertEqual(("false", "true", ""), (decision["publish"], decision["upload"], decision["error"]))
        for overrides in ({"mode": "create"}, {"version": ""}, {"enabled": "false"}, {"publish": "true"}):
            with self.subTest(overrides=overrides):
                self.assert_failed(self.decide(**(choices | overrides)))
        for name, value in (("noop-reason", ""), ("noop-reason", "no-version"), ("publish", "true"),
                            ("sha", DISPATCH_SHA), ("version", "2.0.1")):
            changed = stale_draft()
            changed["draft"]["outputs"][name] = value
            with self.subTest(name=name, value=value):
                self.assert_failed(self.decide(**(choices | {"needs": changed})), "matching complete-mode")
        for result in ("skipped", "failure", "cancelled"):
            changed = stale_draft()
            changed["draft"]["result"] = result
            self.assert_failed(self.decide(**(choices | {"needs": changed})))
        for outputs in ({}, None):
            changed = stale_draft()
            changed["draft"]["outputs"] = outputs
            self.assert_failed(self.decide(**(choices | {"needs": changed})))
        for job in ("resolve", "codeql", "nightly-hook", "version", "build"):
            for result in ("failure", "cancelled"):
                changed = stale_draft()
                changed[job]["result"] = result
                self.assert_failed(self.decide(**(choices | {"needs": changed})), result)

    def test_only_validated_successful_drafts_can_transfer_outcome_ownership(self):
        needs = handoff()
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

    def test_incomplete_or_mismatched_artifact_handoffs_retain_reporter_ownership(self):
        for name, value in (("artifact-id", ""), ("artifact-run", "01"), ("draft-id", "-1"),
                            ("artifact-digest", "c" * 64), ("last-tag", "v1.9.0\n")):
            with self.subTest(name=name):
                needs = handoff()
                needs["draft"]["outputs"][name] = value
                self.assert_failed(self.decide(enabled="true", publish="true", version="2.0.0", needs=needs))
        for owner, name, value in (("build", "artifact-id", "999"),
                                   ("build", "artifact-digest", "sha256:" + "d" * 64),
                                   ("version", "version", "2.0.1"), ("version", "last-tag", "v1.8.0"),
                                   ("version", "no-version", "true")):
            with self.subTest(owner=owner, name=name):
                needs = handoff()
                needs[owner]["outputs"][name] = value
                self.assert_failed(self.decide(enabled="true", publish="true", version="2.0.0", needs=needs),
                                   "differs from the version or build")

    def test_no_version_skips_build_and_draft_but_failure_or_cancellation_wins(self):
        needs = prerequisites(codeql="skipped", hook="skipped")
        needs.update(version={"result": "success", "outputs": {"no-version": "true"}},
                     build={"result": "skipped"}, draft={"result": "skipped"})
        choices = dict(enabled="true", codeql=[], nightly_hook="false", noop_reason="no-version", needs=needs)
        self.assertEqual("nothing", self.decide(**choices)["document"]["outcome"])
        for name in needs:
            for result in ("failure", "cancelled"):
                with self.subTest(name=name, result=result):
                    changed = copy.deepcopy(needs)
                    changed[name]["result"] = result
                    self.assert_failed(self.decide(**(choices | {"needs": changed})), result)

    def test_unexpected_complete_draft_skip_fails_closed(self):
        needs = prerequisites(codeql="skipped", hook="skipped")
        needs.update(version={"result": "skipped"}, build={"result": "skipped"}, draft={"result": "skipped"})
        self.assert_failed(self.decide(enabled="true", codeql=[], nightly_hook="false", mode="complete", needs=needs),
                           "'draft' was unexpectedly skipped")


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

    def test_absent_resolve_outputs_still_report_the_failed_prerequisite(self):
        failed_resolve = json.dumps(prerequisites(resolve="failure", codeql="skipped", hook="skipped"))
        for needs, diagnostic in [(failed_resolve, "release prerequisite 'resolve' failure"),
                                  ("", "release prerequisite results must be an object")]:
            with self.subTest(needs=needs):
                self.output.unlink(missing_ok=True)
                # Workflow expressions render absent job outputs as empty strings.
                result = self.adapter(RELEASE_SHA="", RELEASE_MODE="", RELEASE_ENABLED="", RELEASE_CODEQL="",
                                      RELEASE_NIGHTLY_HOOK="", RELEASE_NEEDS=needs)
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                self.assertIn(diagnostic, result.stderr)
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
        needs = handoff("1.2.3")
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


class ReleaseWorkflowGraphTests(unittest.TestCase):
    """Evaluate the real reusable graph's gates and handoffs, then run the real reporter.

    Job results come from controlled stage doubles; the `if:`, `with:`, job
    output, reporter `env:` and upload expressions are the workflow's own.
    """

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="release-graph-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "runner-temp").mkdir()
        self.workflow = yaml.safe_load((ROOT / ".github/workflows/release.yml").read_text())

    def run_graph(self, *, resolve=None, stages=None, inputs=None, codeql="skipped", hook="skipped",
                  cancelled=False, workflow_ref=None):
        stages = stages or {}
        trigger = self.workflow.get("on", self.workflow.get(True))["workflow_call"]
        values = {name: str(entry.get("default", "")) for name, entry in trigger["inputs"].items()}
        values.update(inputs or {})
        needs = {"resolve": {"result": "success", "outputs": {
                     "sha": SELECTED_SHA, "mode": "create", "enabled": "true", "codeql": "[]",
                     "nightly-hook": "false", "config-valid": "true", **(resolve or {})}},
                 "codeql": {"result": codeql, "outputs": {}},
                 "nightly-hook": {"result": hook, "outputs": {}}}
        workflow_ref = workflow_ref or "Hexalith/Hexalith.Builds/.github/workflows/release-probe.yml@refs/heads/main"
        base = {"inputs": values, "github": {"sha": DISPATCH_SHA, "token": "token-marker",
                                            "workflow_ref": workflow_ref},
                "secrets": {"registry-pull-username": "pull-reader", "registry-pull-password": "pull-secret"},
                "runner": {"temp": str(self.root / "runner-temp")}}
        executed, stage_environment = {}, {}
        for name in ("version", "build", "draft"):
            job = self.workflow["jobs"][name]
            evaluator = Evaluator(dict(base, needs={key: needs[key] for key in job["needs"]}),
                                  status={"success": all(needs[key]["result"] == "success" for key in job["needs"]),
                                          "cancelled": cancelled})
            if not evaluator.condition(job["if"]):
                needs[name] = {"result": "skipped", "outputs": {}}
                continue
            step, = job["steps"]
            executed[name] = {key: evaluator.template(value) for key, value in step.get("with", {}).items()}
            stage_environment[name] = {key: evaluator.template(value) for key, value in step.get("env", {}).items()}
            if name not in stages:
                self.fail(f"{name} must not run in this scenario")
            result, step_outputs = stages[name](executed[name])
            produced = Evaluator({"steps": {step["id"]: {"outputs": step_outputs}}})
            needs[name] = {"result": result, "outputs": {key: produced.template(value)
                                                         for key, value in job.get("outputs", {}).items()}}
        job = self.workflow["jobs"]["outcome"]
        evaluator = Evaluator(dict(base, needs={key: needs[key] for key in job["needs"]}))
        self.assertTrue(evaluator.condition(job["if"]))
        decision, upload = job["steps"]
        environment = {key: evaluator.template(value) for key, value in decision["env"].items()}
        output = self.root / "github-output"
        output.write_text("")
        process_environment = dict(os.environ, **environment, GITHUB_OUTPUT=str(output))
        process_environment["PATH"] = str(Path(sys.executable).parent) + os.pathsep + os.environ.get("PATH", "")
        recorded, processes = run_composite(ROOT / decision["uses"].removeprefix("$/"), {}, process_environment,
                                            cwd=self.root, runner_temp=self.root / "runner-temp")
        recorded = recorded or parse_outputs(output.read_text())
        steps = {decision["id"]: {"outputs": recorded}}
        uploaded = Evaluator({"steps": steps}).condition(upload["if"])
        jobs = {name: {"outputs": needs[name]["outputs"]} for name in ("resolve", "draft")}
        jobs["outcome"] = {"outputs": {key: Evaluator({"steps": steps}).template(value)
                                       for key, value in job["outputs"].items()}}
        published = {key: Evaluator({"jobs": jobs}).template(entry["value"])
                     for key, entry in trigger["outputs"].items()}
        return {"executed": executed, "stage_environment": stage_environment, "needs": needs,
                "environment": environment, "process": processes[-1],
                "recorded": recorded, "uploaded": uploaded, "outputs": published}

    def run_builds_caller(self, *, result="success", **scenario):
        caller = yaml.safe_load((ROOT / ".github/workflows/builds-release.yml").read_text())
        release = caller["jobs"]["release"]
        self.workflow = yaml.safe_load((ROOT / release["uses"]).read_text())
        context = {"vars": {"HEXALITH_NUGET_USER": "nuget-policy-owner"}, "secrets": {
            "HEXALITH_REGISTRY_PULL_USERNAME": "pull-reader", "HEXALITH_REGISTRY_PULL_PASSWORD": "pull-secret",
            "HEXALITH_REGISTRY_USERNAME": "builds-writer", "HEXALITH_REGISTRY_PASSWORD": "builds-push-secret"}}
        evaluator = Evaluator(context)
        graph = self.run_graph(inputs={key: evaluator.template(value) for key, value in release["with"].items()},
            workflow_ref="Hexalith/Hexalith.Builds/.github/workflows/builds-release.yml@refs/heads/main", **scenario)
        context["needs"] = {"release": {"result": result, "outputs": graph["outputs"]}}
        evaluator = Evaluator(context, status={"success": result == "success", "cancelled": result == "cancelled"})
        job = caller["jobs"]["publish"]
        graph["caller_publish"] = []
        if evaluator.condition(job["if"]):
            for step in job["steps"]:
                graph["caller_publish"].append((ROOT / step["uses"].removeprefix("$/"),
                    {key: evaluator.template(value) for key, value in step.get("with", {}).items()}))
        graph["caller_pull"] = {key: evaluator.template(value) for key, value in release["secrets"].items()}
        return graph

    def test_builds_caller_forwards_original_complete_identity_to_the_publisher(self):
        expected = handoff()["draft"]["outputs"]
        graph = self.run_builds_caller(resolve={"mode": "complete", "version": "2.0.0", "draft-id": "456",
                                               "last-tag": "v1.9.0"},
                                      stages={"draft": lambda _values: ("success", expected)})
        self.assertEqual([(ROOT / "Github/publish", expected | {"nuget-user": "nuget-policy-owner",
            "registry-username": "builds-writer", "registry-password": "builds-push-secret"})], graph["caller_publish"])
        self.assertEqual({"registry-pull-username": "pull-reader", "registry-pull-password": "pull-secret"},
                         graph["caller_pull"])
        self.assertEqual("Hexalith/Hexalith.Builds/.github/workflows/builds-release.yml@refs/heads/main",
                         graph["stage_environment"]["draft"]["GITHUB_WORKFLOW_REF"])
        self.assertFalse(graph["uploaded"])

    def test_builds_caller_uses_builds_rules_and_does_not_publish_without_a_successful_handoff(self):
        def version(values):
            self.assertEqual("builds", values["release-rules"])
            return "success", {"no-version": "true"}
        graph = self.run_builds_caller(stages={"version": version})
        self.assertEqual([], graph["caller_publish"])
        self.assertEqual("nothing", self.document()["outcome"])
        self.assertTrue(graph["uploaded"])
        for resolve in ({"enabled": "false"}, {"enabled": "invalid"}):
            with self.subTest(resolve=resolve):
                graph = self.run_builds_caller(resolve=resolve)
                self.assertEqual([], graph["caller_publish"])
                self.assertTrue(graph["uploaded"])
        for result in ("failure", "cancelled", "skipped"):
            with self.subTest(result=result):
                graph = self.run_builds_caller(result=result, resolve={"mode": "complete", "version": "2.0.0",
                    "draft-id": "456", "last-tag": "v1.9.0"},
                    stages={"draft": lambda _values: ("success", handoff()["draft"]["outputs"])})
                self.assertEqual([], graph["caller_publish"])

    def document(self):
        document = json.loads((self.root / "runner-temp/hexalith-release-outcome/outcome.json").read_text())
        Draft202012Validator(SCHEMA).validate(document)
        return document

    def test_no_version_skips_build_and_draft_and_reports_nothing(self):
        graph = self.run_graph(inputs={"release-rules": "builds"}, stages={"version": lambda values: (
            "success", {"version": "", "notes": "", "last-tag": "v1.9.0", "no-version": "true"})})
        self.assertEqual({"version": {"sha": SELECTED_SHA, "release-rules": "builds", "module-root": ""}},
                         graph["executed"])
        self.assertEqual("no-version", graph["environment"]["RELEASE_NOOP_REASON"])
        self.assertEqual("false", graph["environment"]["RELEASE_PUBLISH"])
        self.assertEqual(0, graph["process"].returncode, graph["process"].stdout + graph["process"].stderr)
        self.assertEqual("nothing", self.document()["outcome"])
        self.assertTrue(graph["uploaded"])
        self.assertEqual("false", graph["outputs"]["publish"])

    def test_default_rules_and_every_handoff_reach_the_publisher_only_after_the_draft(self):
        notes = "## 2.0.0 (2026-11-11)\n\n### Bug Fixes\n\n* **release:** multiline notes"
        digest = "sha256:" + "c" * 64
        draft_outputs = handoff("2.0.0")["draft"]["outputs"]
        def build(values):
            self.assertEqual({"sha": SELECTED_SHA, "version": "2.0.0", "module-root": ""}, values)
            return "success", {"artifact-id": "123", "artifact-digest": digest}
        def draft(values):
            self.assertEqual({"mode": "create", "draft-id": "", "last-tag": "", "sha": SELECTED_SHA,
                              "version": "2.0.0", "notes": notes, "artifact-id": "123",
                              "artifact-digest": digest}, values)
            return "success", draft_outputs
        graph = self.run_graph(stages={"version": lambda values: (
            "success", {"version": "2.0.0", "notes": notes, "last-tag": "v1.9.0", "no-version": "false"}),
            "build": build, "draft": draft})
        self.assertEqual("module", graph["executed"]["version"]["release-rules"])
        self.assertEqual({"RELEASE_PUBLISH": "true", "RELEASE_VERSION": "2.0.0", "RELEASE_NOOP_REASON": ""},
                         {key: graph["environment"][key] for key in ("RELEASE_PUBLISH", "RELEASE_VERSION",
                                                                     "RELEASE_NOOP_REASON")})
        self.assertEqual(0, graph["process"].returncode, graph["process"].stdout + graph["process"].stderr)
        self.assertEqual("true", graph["recorded"]["publish"])
        self.assertFalse(graph["uploaded"])
        expected = {key: draft_outputs[key] for key in ("version", "last-tag", "draft-id", "artifact-id",
                                                        "artifact-digest", "artifact-run")}
        self.assertEqual(expected | {"sha": SELECTED_SHA, "mode": "create", "publish": "true"}, graph["outputs"])

    def test_build_failure_retains_the_version_and_never_authorizes_the_publisher(self):
        graph = self.run_graph(stages={"version": lambda values: (
            "success", {"version": "2.0.0", "notes": "notes", "last-tag": "v1.9.0", "no-version": "false"}),
            "build": lambda values: ("failure", {})})
        self.assertEqual({"version", "build"}, set(graph["executed"]))
        self.assertEqual("2.0.0", graph["environment"]["RELEASE_VERSION"])
        self.assertEqual("false", graph["environment"]["RELEASE_PUBLISH"])
        self.assertEqual(1, graph["process"].returncode)
        self.assertEqual(("failed", "2.0.0"), (self.document()["outcome"], self.document()["version"]))
        self.assertEqual("false", graph["outputs"]["publish"])
        self.assertTrue(graph["uploaded"])

    def test_complete_handoff_uses_saved_identity_and_skips_version_and_build(self):
        expected = handoff()["draft"]["outputs"]
        def draft(values):
            self.assertEqual({"mode": "complete", "draft-id": "456", "last-tag": "v1.9.0", "sha": SELECTED_SHA,
                              "version": "2.0.0", "notes": "", "artifact-id": "", "artifact-digest": ""}, values)
            return "success", expected
        graph = self.run_graph(resolve={"mode": "complete", "version": "2.0.0", "draft-id": "456",
                                        "last-tag": "v1.9.0", "codeql": '["csharp"]', "nightly-hook": "true"},
                               codeql="success", hook="success", stages={"draft": draft})
        self.assertEqual({"draft"}, set(graph["executed"]))
        self.assertEqual(("skipped", "skipped"), tuple(graph["needs"][name]["result"] for name in ("version", "build")))
        self.assertEqual({"GITHUB_TOKEN": "token-marker", "GITHUB_WORKFLOW_REF":
                          "Hexalith/Hexalith.Builds/.github/workflows/release-probe.yml@refs/heads/main",
                          "REGISTRY_PULL_USERNAME": "pull-reader", "REGISTRY_PULL_PASSWORD": "pull-secret"},
                         graph["stage_environment"]["draft"])
        self.assertEqual(0, graph["process"].returncode, graph["process"].stderr)
        self.assertEqual("true", graph["outputs"]["publish"])
        for name in expected:
            self.assertEqual(expected[name], graph["outputs"][name])
        self.assertEqual("complete", graph["outputs"]["mode"])
        self.assertFalse(graph["uploaded"])
        self.assertFalse((self.root / "runner-temp/hexalith-release-outcome").exists())

    def test_complete_stale_deletion_reports_one_nothing_outcome(self):
        graph = self.run_graph(resolve={"mode": "complete", "version": "2.0.0", "draft-id": "456",
                                        "last-tag": "v1.9.0"},
                               stages={"draft": lambda _values: ("success", stale_draft()["draft"]["outputs"])})
        self.assertEqual({"draft"}, set(graph["executed"]))
        self.assertEqual("stale-draft-deleted", graph["environment"]["RELEASE_NOOP_REASON"])
        self.assertEqual(0, graph["process"].returncode, graph["process"].stderr)
        self.assertEqual({"schema": "hexalith.release-outcome.v1", "sha": SELECTED_SHA,
                          "outcome": "nothing", "version": "2.0.0"}, self.document())
        self.assertEqual("false", graph["outputs"]["publish"])
        self.assertTrue(graph["uploaded"])
        self.assertEqual([self.root / "runner-temp/hexalith-release-outcome/outcome.json"],
                         list((self.root / "runner-temp/hexalith-release-outcome").iterdir()))

    def test_enabled_skipped_security_gate_blocks_draft_before_any_mutation(self):
        for gate, codeql, hook in (("codeql", "skipped", "success"), ("nightly-hook", "success", "skipped")):
            mutations = []
            def stale(_values):
                mutations.append("DELETE")
                return "success", stale_draft()["draft"]["outputs"]
            with self.subTest(gate=gate):
                graph = self.run_graph(resolve={"mode": "complete", "version": "2.0.0", "draft-id": "456",
                    "last-tag": "v1.9.0", "codeql": '["csharp"]', "nightly-hook": "true"},
                    codeql=codeql, hook=hook, stages={"draft": stale})
                self.assertEqual([], mutations)
                self.assertEqual({}, graph["executed"])
                self.assertEqual("skipped", graph["needs"]["draft"]["result"])
                self.assertEqual(1, graph["process"].returncode)
                self.assertIn(f"'{gate}' was unexpectedly skipped", graph["process"].stderr)
                self.assertEqual("failed", self.document()["outcome"])
                self.assertEqual("false", graph["outputs"]["publish"])
                self.assertTrue(graph["uploaded"])

    def test_complete_draft_failures_and_incomplete_handoffs_report_failed(self):
        cases = (("failure", {}, "'draft' failure"), ("cancelled", {}, "'draft' cancelled"),
                 ("skipped", {}, "'draft' was unexpectedly skipped"),
                 ("success", {"publish": "true"}, "lacks a validated successful draft"),
                 ("success", {"publish": "false", "noop-reason": "stale-draft-deleted"}, "matching complete-mode"))
        for result, outputs, diagnostic in cases:
            with self.subTest(result=result, outputs=outputs):
                graph = self.run_graph(resolve={"mode": "complete", "version": "2.0.0"},
                                       stages={"draft": lambda _values: (result, outputs)})
                self.assertEqual({"draft"}, set(graph["executed"]))
                self.assertEqual(1, graph["process"].returncode)
                self.assertIn(diagnostic, graph["process"].stderr)
                self.assertEqual(("failed", "2.0.0"), (self.document()["outcome"], self.document()["version"]))
                self.assertEqual("false", graph["outputs"]["publish"])
                self.assertTrue(graph["uploaded"])

    def test_disabled_complete_and_gate_failures_skip_all_create_and_recovery_stages(self):
        cases = (({"enabled": "false", "mode": "complete"}, "skipped", "skipped", 0, "disabled", ""),
                 ({"codeql": '["csharp"]', "mode": "complete"}, "failure", "skipped", 1, "failed", "codeql"),
                 ({"codeql": '["csharp"]', "mode": "complete"}, "cancelled", "skipped", 1, "failed", "cancelled"),
                 ({"nightly-hook": "true", "mode": "complete"}, "skipped", "failure", 1, "failed", "nightly-hook"),
                 ({"enabled": "invalid", "mode": "complete"}, "skipped", "skipped", 1, "failed", "exactly"))
        for resolve, codeql, hook, code, outcome, diagnostic in cases:
            with self.subTest(resolve=resolve, codeql=codeql, hook=hook):
                graph = self.run_graph(resolve=resolve, codeql=codeql, hook=hook)
                self.assertEqual({}, graph["executed"])
                self.assertEqual(code, graph["process"].returncode, graph["process"].stderr)
                self.assertIn(diagnostic, graph["process"].stderr)
                self.assertEqual(outcome, self.document()["outcome"])
                self.assertEqual("false", graph["outputs"]["publish"])



if __name__ == "__main__":
    unittest.main()
