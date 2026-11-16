"""Qualify additive adoption configuration and independent semantic checks."""

import copy
import unittest

from test_ci_config import CONFIG, SCHEMA, CORPUS, BUILDS
from jsonschema import Draft202012Validator


class ExecutionConfigTests(unittest.TestCase):
    def test_common_runner_property_remains_in_the_public_schema_surface(self):
        job = SCHEMA["properties"]["jobs"]["additionalProperties"]
        self.assertEqual("object", job["type"])
        self.assertIn("runner", job["required"])
        self.assertEqual({"string", "object"}, set(job["properties"]["runner"]["type"]))
        for runner in ("windows-latest", "ubuntu-latest"):
            self.valid(self.document(jobs={"product": {"runner": runner}}))
        private = {"runner": {"group": "private", "labels": ["self-hosted", "linux", "x64"]},
                   "trigger": "manual", "environment": "approval"}
        self.valid(self.document(jobs={"product": private}))

    def document(self, **changes):
        document = copy.deepcopy(next(case["document"] for case in CORPUS if case["valid"]))
        document.update(solution="Fixture.slnx", **changes)
        return document

    def valid(self, document):
        Draft202012Validator(SCHEMA).validate(document)
        before = copy.deepcopy(document)
        self.assertIs(document, CONFIG.validate_config(document, repository=BUILDS))
        self.assertEqual(before, document)

    def test_retained_linux_cases_duration_resources_and_dependencies(self):
        self.valid(self.document(releaseTimeoutMinutes=180, nightlyTimeoutMinutes=360, jobs={
            "routes": {"runner": "ubuntu-latest", "timeoutMinutes": 90,
                       "matrix": {"include": [{"id": "route-one"}, {"id": "route-two", "engine": "firefox"}]},
                       "maxParallel": 2, "setup": {"dapr": True, "browsers": True}},
            "mutation": {"runner": "ubuntu-24.04", "trigger": "nightly", "timeoutMinutes": 360,
                         "resourceProfile": "mutation", "matrix": {"include": [{"id": "happy"}, {"id": "error"}]}},
            "aggregate": {"runner": "ubuntu-24.04", "trigger": "nightly", "needs": ["mutation"],
                          "runAfterFailure": True}}))

    def test_manual_private_credentials_and_exact_shared_prerequisites(self):
        self.valid(self.document(prerequisites={"nodeVersion": "24.10.0", "npmVersion": "11.6.1",
                                                "apt": True, "tlsTrust": True, "daprProfile": "catalog"}, jobs={
            "azure": {"runner": {"group": "azure-private", "labels": ["self-hosted", "linux", "x64"]},
                      "trigger": "manual", "environment": "azure-conformance",
                      "secrets": {"AUTHORITY_JSON": "PAYLOAD_AZURE_AUTHORITY_JSON"}}}))

    def test_private_mappings_preserve_fixtures_and_the_existing_180_minute_bound(self):
        job = {"runner": {"group": "private", "labels": ["self-hosted", "linux", "x64"]},
               "trigger": "manual", "environment": "approval", "preparation": "build-only",
               "secrets": {"AUTHORITY_JSON": "PAYLOAD_AZURE_AUTHORITY_JSON"}, "timeoutMinutes": 180}
        self.valid(self.document(jobs={"private": job}))
        for change in ({"timeoutMinutes": 181}, {"setup": {}}, {"preparation": "setup"},
                       {"secrets": {"ACTIONS_RUNTIME_TOKEN": "AUTHORITY_JSON"}}):
            document = self.document(jobs={"private": {**job, **change}})
            with self.subTest(change=change), self.assertRaises(CONFIG.ConfigError):
                CONFIG.validate_config(document, repository=BUILDS)
            self.assertFalse(Draft202012Validator(SCHEMA).is_valid(document))
        for reserved in ("HEXALITH_AUTHORITY_SHA256", "HEXALITH_SOURCE_SHA"):
            document = self.document(jobs={"private": {**job, "secrets": {reserved: "AUTHORITY_JSON"}}})
            with self.subTest(reserved=reserved), self.assertRaises(CONFIG.ConfigError):
                CONFIG.validate_config(document, repository=BUILDS)
            self.assertFalse(Draft202012Validator(SCHEMA).is_valid(document))
            legacy = self.document(jobs={"private": {**job, "secrets": [reserved]}})
            with self.subTest(legacy_reserved=reserved), self.assertRaises(CONFIG.ConfigError):
                CONFIG.validate_config(legacy, repository=BUILDS)
            self.assertFalse(Draft202012Validator(SCHEMA).is_valid(legacy))

    def test_additional_projects_and_exact_coverage_contract(self):
        document = self.document()
        document["tests"].update(additional=["test/Outside.csproj"], reports={"trx": True, "coverage": {
            "projects": ["test/Outside.csproj"], "lineScope": ["src/Product"], "minimumLine": 80,
            "lineComparison": "greater-than", "branchTargets": ["src/Product/Isolation.cs"], "minimumBranch": 100}})
        self.valid(document)

    def test_unsafe_or_incomplete_execution_contract_fails_before_running(self):
        cases = [
            {"runner": "ubuntu-latest", "timeoutMinutes": 361},
            {"runner": "ubuntu-latest", "matrix": {"include": [{"id": "x", "value": "${{ secrets.X }}"}]}},
            {"runner": "ubuntu-latest", "matrix": {"include": [{"id": "x"}, {"id": "x", "value": "different"}]}},
            {"runner": "ubuntu-latest", "maxParallel": 2},
            {"runner": "windows-latest", "setup": {"dapr": True}},
            {"runner": {"group": "private", "labels": ["self-hosted", "linux", "x64"]}},
            {"runner": "ubuntu-latest", "trigger": "manual", "environment": "azure", "secrets": {"AUTH": "SECRET"}},
            {"runner": "ubuntu-latest", "needs": ["absent"]},
            {"runner": "ubuntu-latest", "runAfterFailure": True},
        ]
        for options in cases:
            with self.subTest(options=options), self.assertRaises(CONFIG.ConfigError):
                CONFIG.validate_config(self.document(jobs={"product": options}), repository=BUILDS)

    def test_cycles_and_cross_schedule_dependencies_are_rejected(self):
        for second in ({"runner": "ubuntu-latest", "needs": ["one"]},
                       {"runner": "ubuntu-latest", "trigger": "nightly"}):
            with self.subTest(second=second), self.assertRaises(CONFIG.ConfigError):
                CONFIG.validate_config(self.document(jobs={"one": {"runner": "ubuntu-latest", "needs": ["two"]},
                                                           "two": second}), repository=BUILDS)

    def test_reporting_never_admits_empty_scopes_or_excluded_coverage(self):
        for coverage in ({"projects": []}, {"projects": ["test/X.csproj"], "minimumLine": 80},
                         {"projects": ["test/X.csproj"], "minimumBranch": 100},
                         {"projects": ["test/X.csproj"], "lineScope": ["../elsewhere"]}):
            document = self.document()
            document["tests"]["reports"] = {"coverage": coverage}
            with self.subTest(coverage=coverage), self.assertRaises(CONFIG.ConfigError):
                CONFIG.validate_config(document, repository=BUILDS)


if __name__ == "__main__":
    unittest.main()
