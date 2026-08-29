"""BUILD-REL-1 contract tests for the opt-in governed NuGet release mode.

The suite covers the four properties the governed contract must never lose: the common
release-freeze gate skips instead of failing, a caller that leaves the governed inputs
unset keeps the pre-BUILD-REL-1 contract, governed mode fails closed without its signing
secrets, and every governed path proves its workflow identity from immutable references.
"""

import json
import os
import re
import subprocess  # nosec B404 -- tests execute only repository-owned workflow fixtures.
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = REPOSITORY_ROOT / ".github" / "workflows"
DOMAIN_RELEASE = WORKFLOWS / "domain-release.yml"
DOMAIN_CI = WORKFLOWS / "domain-ci.yml"
DAPR_INIT = REPOSITORY_ROOT / "Github" / "dapr-init" / "action.yml"

FREEZE_VARIABLE = "HEXALITH_RELEASE_PUBLISH_ENABLED"
USES_PATTERN = re.compile(r"^\s*(?:-\s+)?uses:\s*(?P<value>\S+)", re.MULTILINE)
PINNED_USES_PATTERN = re.compile(r"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+(?:/[^@\s]+)?@[0-9a-f]{40}$")
GOVERNED_CANDIDATE = "0123456789abcdef0123456789abcdef01234567"
GOVERNED_POLICY_COMMIT = "1123456789abcdef0123456789abcdef01234567"
GOVERNED_SHA256 = "a" * 64


def read(path):
    """Read one workflow or action source as text."""
    return path.read_text(encoding="utf-8")


def step_indices(workflow, step_name):
    """Return every start offset of a named step in a workflow source."""
    marker = f"- name: {step_name}\n"
    offsets = []
    start = workflow.find(marker)
    while start != -1:
        offsets.append(start)
        start = workflow.find(marker, start + 1)
    return offsets


def extract_run_block(path, step_name, occurrence=0):
    """Extract the shell body of one named step, selecting the nth identical step name."""
    lines = read(path).splitlines()
    matches = [index for index, line in enumerate(lines) if line.strip() == f"- name: {step_name}"]
    if len(matches) <= occurrence:
        raise AssertionError(f"{path.name} has no step {step_name!r} at occurrence {occurrence}")
    step_index = matches[occurrence]
    run_index = next(
        index for index in range(step_index + 1, len(lines)) if lines[index].strip() == "run: |"
    )
    run_indent = len(lines[run_index]) - len(lines[run_index].lstrip())
    block = []
    for line in lines[run_index + 1:]:
        indent = len(line) - len(line.lstrip())
        if line.strip() and indent <= run_indent:
            break
        block.append(line[run_indent + 2:] if line.strip() else "")
    return "\n".join(block) + "\n"


def job_slice(workflow, job_name):
    """Return the source of one top-level job."""
    start = workflow.index(f"\n  {job_name}:\n")
    remainder = workflow[start + 1:]
    next_job = re.search(r"\n  [a-z][a-z0-9-]*:\n", remainder)
    return remainder if next_job is None else remainder[: next_job.start()]


def run_block(script, environment, cwd=None):
    """Execute one extracted workflow shell body with a controlled environment."""
    merged = os.environ.copy()
    merged.update(environment)
    return subprocess.run(  # nosec B603  # NOSONAR -- repository-owned workflow script.
        ["bash", "-c", script],
        env=merged,
        cwd=cwd,
        capture_output=True,
        text=True,
        check=False,
    )


def run_freeze_gate(value, occurrence=0):
    """Run the release-freeze gate with one caller variable value and return its verdict."""
    script = extract_run_block(DOMAIN_RELEASE, "Resolve release publication freeze", occurrence)
    with tempfile.TemporaryDirectory() as temporary_directory:
        output_path = Path(temporary_directory) / "github-output"
        output_path.touch()
        environment = {"GITHUB_OUTPUT": str(output_path)}
        if value is None:
            environment["__UNSET_FREEZE_VARIABLE__"] = "1"
        else:
            environment[FREEZE_VARIABLE] = value
        merged = os.environ.copy()
        merged.update(environment)
        if value is None:
            merged.pop(FREEZE_VARIABLE, None)
        result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned workflow script.
            ["bash", "-c", script],
            env=merged,
            capture_output=True,
            text=True,
            check=False,
        )
        return result, output_path.read_text(encoding="utf-8")


def governed_contract_environment(**overrides):
    """Build a complete, valid governed release-contract environment."""
    environment = {
        "RELEASE_COMMIT": GOVERNED_CANDIDATE,
        "CI_RUN_ID": "42",
        "CI_RUN_ATTEMPT": "1",
        "CI_HANDOFF_ARTIFACT": "dependency-release-handoff",
        "POLICY_REPOSITORY": "github.com/hexalith/hexalith.frontcomposer",
        "POLICY_PATH": "eng/dependency-graph-policy.json",
        "POLICY_COMMIT": GOVERNED_POLICY_COMMIT,
        "POLICY_SHA256": GOVERNED_SHA256,
        "EXPECTED_EVALUATOR_DIGEST": "b" * 64,
        "CANDIDATE_COMMAND": "bash scripts/pack-governed-candidate.sh",
        "CANDIDATE_DIRECTORY": ".hexalith/release/candidate",
        "EXPECTED_PACKAGE_COUNT": "5",
        "SIGNING_TIMESTAMPER": "http://timestamp.example.test",
        "PUBLISH_ENABLED": "true",
        "SIGNING_CERTIFICATE_PRESENT": "true",
        "SIGNING_PASSWORD_PRESENT": "true",
    }
    environment.update(overrides)
    return environment


def candidate_phase_environment(workspace, **overrides):
    """Build a governed candidate-phase environment rooted in a scratch workspace."""
    environment = {
        "GITHUB_OUTPUT": str(Path(workspace) / "github-output"),
        "GITHUB_TOKEN": "unused-in-tests",
        "CANDIDATE_COMMAND": "true",
        "HEXALITH_RELEASE_CANDIDATE_DIRECTORY": "nupkgs",
        "HEXALITH_RELEASE_GOVERNED": "true",
        "HEXALITH_RELEASE_COMMIT": GOVERNED_CANDIDATE,
        "HEXALITH_RELEASE_ENVIRONMENT": "production",
        "HEXALITH_RELEASE_PACKAGE_MANIFEST": "eng/packages.json",
        "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": "",
        "HEXALITH_NUGET_SIGNING_TIMESTAMPER": "http://timestamp.example.test",
        "NUGET_SIGNING_CERTIFICATE_BASE64": "unused-in-tests",
        "NUGET_SIGNING_CERTIFICATE_PASSWORD": "unused-in-tests",
    }
    environment.update(overrides)
    return environment


def stub_semantic_release(workspace, stdout, exit_code=0):
    """Install an `npm` stub so the candidate phase can run without a real release toolchain."""
    binaries = Path(workspace) / "stub-bin"
    binaries.mkdir(exist_ok=True)
    npm = binaries / "npm"
    npm.write_text(
        "#!/usr/bin/env bash\n"
        'printf "%s\\n" "$FAKE_SEMANTIC_RELEASE_STDOUT"\n'
        'exit "${FAKE_SEMANTIC_RELEASE_EXIT:-0}"\n',
        encoding="utf-8",
    )
    npm.chmod(0o755)
    return {
        "PATH": f"{binaries}:{os.environ['PATH']}",
        "FAKE_SEMANTIC_RELEASE_STDOUT": stdout,
        "FAKE_SEMANTIC_RELEASE_EXIT": str(exit_code),
    }


def run_candidate_phase(workspace, **overrides):
    """Execute the governed candidate phase against a scratch workspace."""
    script = extract_run_block(DOMAIN_RELEASE, "Prepare governed release candidate")
    stubs = overrides.pop(
        "stubs", stub_semantic_release(workspace, "The next release version is 1.2.3")
    )
    Path(workspace, "github-output").touch()
    environment = candidate_phase_environment(workspace, **overrides)
    environment.update(stubs)
    result = run_block(script, environment, cwd=workspace)
    outputs = dict(
        line.split("=", 1)
        for line in Path(workspace, "github-output").read_text(encoding="utf-8").splitlines()
        if "=" in line
    )
    return result, outputs


class ReleaseFreezeGateTests(unittest.TestCase):
    """The common REL-4 freeze gate must skip publication instead of failing the run."""

    def test_exact_true_is_the_only_value_that_authorizes_publication(self):
        result, output = run_freeze_gate("true")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("publish-enabled=true", output)

    def test_missing_or_malformed_values_skip_without_failing(self):
        for value in (None, "", "TRUE", "True", "tRue", " true", "true ", "1", "yes", "false"):
            with self.subTest(value=value):
                result, output = run_freeze_gate(value)

                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("publish-enabled=false", output)
                self.assertNotIn("publish-enabled=true", output)
                self.assertIn("::notice title=Release publication frozen::", result.stdout)

    def test_the_notice_documents_the_repository_over_organization_shadowing_hazard(self):
        result, _output = run_freeze_gate("")

        self.assertIn("shadows the organization value", result.stdout)

    def test_both_release_jobs_evaluate_the_same_gate(self):
        workflow = read(DOMAIN_RELEASE)

        self.assertEqual(2, len(step_indices(workflow, "Resolve release publication freeze")))
        for occurrence in (0, 1):
            with self.subTest(occurrence=occurrence):
                result, output = run_freeze_gate("true", occurrence)

                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("publish-enabled=true", output)

    def test_the_gate_precedes_and_conditions_semantic_release(self):
        workflow = read(DOMAIN_RELEASE)
        expected = {
            "release": "if: ${{ steps.publish-gate.outputs.publish-enabled == 'true' }}",
            # Governed publication additionally requires an attested candidate; see
            # test_semantic_release_requires_both_the_gate_and_an_attested_candidate.
            "governed-release": (
                "if: ${{ steps.publish-gate.outputs.publish-enabled == 'true'"
                " && steps.governed-candidate.outputs.release-required == 'true' }}"
            ),
        }

        for job_name, condition in expected.items():
            with self.subTest(job=job_name):
                source = job_slice(workflow, job_name)
                gate_index = source.index("- name: Resolve release publication freeze")
                semantic_index = source.index("- name: Semantic Release")
                self.assertLess(gate_index, semantic_index)
                semantic_step = source[semantic_index:]
                self.assertIn(
                    condition, semantic_step[: semantic_step.index("run: npm exec --no -- semantic-release")]
                )

    def test_release_jobs_install_and_run_lockfile_node_binaries_only(self):
        workflow = read(DOMAIN_RELEASE)

        self.assertIn("run: npm ci --ignore-scripts", workflow)
        self.assertIn("run: npm exec --no -- semantic-release", workflow)
        self.assertNotIn("npx semantic-release", workflow)
        self.assertNotRegex(workflow, r"(?m)^[ \t]+run: npm ci$")

    def test_semantic_release_requires_both_the_gate_and_an_attested_candidate(self):
        # publish-enabled alone would let a run whose candidate phase resolved no version,
        # or failed before producing one, reach semantic-release and publish bytes that
        # were never packed, signed, or attested.
        source = job_slice(read(DOMAIN_RELEASE), "governed-release")
        semantic_index = source.index("- name: Semantic Release")
        condition = next(
            line.strip()
            for line in source[semantic_index:].splitlines()
            if line.strip().startswith("if:")
        )

        self.assertIn("steps.publish-gate.outputs.publish-enabled == 'true'", condition)
        self.assertIn("steps.governed-candidate.outputs.release-required == 'true'", condition)
        self.assertIn("&&", condition)

    def test_the_governed_gate_is_the_first_step_of_the_governed_job(self):
        # A frozen module must not be told its signing secrets are missing, and must not
        # pay for a build whose only purpose is a publication that will not happen.
        source = job_slice(read(DOMAIN_RELEASE), "governed-release")
        names = re.findall(r"^      - name: (?P<name>.+)$", source, re.MULTILINE)

        self.assertEqual("Resolve release publication freeze", names[0])
        self.assertLess(
            source.index("- name: Resolve release publication freeze"),
            source.index("- name: Validate governed release contract"),
        )

    def test_the_frozen_verdict_skips_every_governed_publication_step(self):
        source = job_slice(read(DOMAIN_RELEASE), "governed-release")
        gated = (
            "Initialize .NET",
            "Initialize Node.js",
            "Cache NuGet packages",
            "Install npm dependencies",
            "Restore",
            "Build",
            "Prepare release container publisher",
            "Revalidate governed release source before Semantic Release",
            "Prepare governed release candidate",
            "Attest governed release candidate",
            "Verify governed candidate attestation",
            "Semantic Release",
        )

        for step in gated:
            with self.subTest(step=step):
                index = source.index(f"- name: {step}\n")
                condition = next(
                    line.strip()
                    for line in source[index:].splitlines()
                    if line.strip().startswith("if:")
                )

                self.assertIn("steps.publish-gate.outputs.publish-enabled == 'true'", condition)

    def test_a_frozen_governed_caller_does_not_need_its_signing_secrets(self):
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")

        frozen = run_block(
            script,
            governed_contract_environment(
                PUBLISH_ENABLED="false",
                SIGNING_CERTIFICATE_PRESENT="false",
                SIGNING_PASSWORD_PRESENT="false",
            ),
        )

        self.assertEqual(0, frozen.returncode, frozen.stderr)
        self.assertIn("Signing secrets not required", frozen.stdout)

        unfrozen = run_block(
            script,
            governed_contract_environment(
                PUBLISH_ENABLED="true",
                SIGNING_CERTIFICATE_PRESENT="false",
                SIGNING_PASSWORD_PRESENT="false",
            ),
        )

        self.assertNotEqual(0, unfrozen.returncode)
        self.assertIn("refusing to publish unsigned packages", unfrozen.stderr)

    def test_a_frozen_caller_still_fails_on_a_malformed_coordinate(self):
        # Freezing suppresses the signing requirement only. A frozen run that misdeclares
        # its coordinates is still a broken caller and must say so.
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")

        result = run_block(
            script,
            governed_contract_environment(PUBLISH_ENABLED="false", RELEASE_COMMIT="not-a-sha"),
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("release-commit", result.stderr)

    def test_the_source_guard_skips_only_on_an_explicit_frozen_verdict(self):
        script = extract_run_block(DOMAIN_RELEASE, "Revalidate current source before Semantic Release")

        frozen = run_block(
            script,
            {
                "PUBLISH_ENABLED": "false",
                "DISPATCH_SHA": "not-a-sha",
                "SOURCE_BRANCH": "main",
                "REPOSITORY": "Hexalith/Hexalith.EventStore",
            },
        )

        self.assertEqual(0, frozen.returncode, frozen.stderr)
        self.assertIn("Source revalidation skipped", frozen.stdout)

    def test_an_absent_or_empty_gate_verdict_still_runs_the_source_guard(self):
        script = extract_run_block(DOMAIN_RELEASE, "Revalidate current source before Semantic Release")

        for verdict in (None, ""):
            with self.subTest(verdict=verdict):
                environment = {
                    "DISPATCH_SHA": "not-a-sha",
                    "SOURCE_BRANCH": "main",
                    "REPOSITORY": "Hexalith/Hexalith.EventStore",
                }
                if verdict is not None:
                    environment["PUBLISH_ENABLED"] = verdict
                merged = os.environ.copy()
                merged.update(environment)
                merged.pop("PUBLISH_ENABLED", None) if verdict is None else None
                result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned script.
                    ["bash", "-c", script],
                    env=merged,
                    capture_output=True,
                    text=True,
                    check=False,
                )

                self.assertNotEqual(0, result.returncode)
                self.assertIn("dispatched release source", result.stderr)


class GovernedReleaseContractTests(unittest.TestCase):
    """Governed mode must fail closed on any incomplete governed request."""

    def test_a_complete_governed_request_is_accepted(self):
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")

        result = run_block(script, governed_contract_environment())

        self.assertEqual(0, result.returncode, result.stderr)

    def test_missing_signing_secrets_fail_closed_with_an_explicit_diagnostic(self):
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")

        for absent in ("SIGNING_CERTIFICATE_PRESENT", "SIGNING_PASSWORD_PRESENT"):
            with self.subTest(absent=absent):
                result = run_block(script, governed_contract_environment(**{absent: "false"}))

                self.assertNotEqual(0, result.returncode)
                self.assertIn("refusing to publish unsigned packages", result.stderr)

        both_absent = run_block(
            script,
            governed_contract_environment(
                SIGNING_CERTIFICATE_PRESENT="false",
                SIGNING_PASSWORD_PRESENT="false",
            ),
        )

        self.assertNotEqual(0, both_absent.returncode)
        self.assertIn("NUGET_SIGNING_CERTIFICATE_BASE64", both_absent.stderr)
        self.assertIn("NUGET_SIGNING_CERTIFICATE_PASSWORD", both_absent.stderr)

    def test_malformed_governed_coordinates_are_rejected(self):
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")
        scenarios = (
            ({"RELEASE_COMMIT": ""}, "release-commit"),
            ({"RELEASE_COMMIT": GOVERNED_CANDIDATE.upper()}, "release-commit"),
            ({"CI_RUN_ID": "0"}, "ci-run-id"),
            ({"CI_RUN_ATTEMPT": "one"}, "ci-run-attempt"),
            ({"CI_HANDOFF_ARTIFACT": "   "}, "ci-handoff-artifact"),
            ({"POLICY_REPOSITORY": "Hexalith/Hexalith.FrontComposer"}, "dependency-policy-repository"),
            ({"POLICY_PATH": "/etc/policy.json"}, "dependency-policy-path"),
            ({"POLICY_COMMIT": "abc"}, "dependency-policy-commit"),
            ({"POLICY_SHA256": "a" * 63}, "dependency-policy-sha256"),
            ({"EXPECTED_EVALUATOR_DIGEST": ""}, "expected-release-evaluator-digest"),
            ({"CANDIDATE_COMMAND": ""}, "candidate-command"),
            ({"CANDIDATE_DIRECTORY": "/tmp/candidate"}, "candidate-directory"),
            ({"EXPECTED_PACKAGE_COUNT": "many"}, "expected-package-count"),
            ({"SIGNING_TIMESTAMPER": "timestamp.example.test"}, "nuget-signing-timestamper"),
        )

        for override, expected in scenarios:
            with self.subTest(expected=expected):
                result = run_block(script, governed_contract_environment(**override))

                self.assertNotEqual(0, result.returncode)
                self.assertIn(expected, result.stderr)

    def test_an_unsafe_candidate_directory_is_rejected_before_any_delete(self):
        # The value reaches `rm -rf`. A traversing, absolute, or self-referential path
        # would delete something the caller never named.
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")
        unsafe = (
            "",
            "   ",
            "/",
            "/tmp/candidate",
            "~/candidate",
            ".",
            "..",
            "../candidate",
            "nupkgs/../../etc",
            "nupkgs/./out",
            "nupkgs//out",
            "nupkgs/..",
            "a/b/../../../c",
        )

        for value in unsafe:
            with self.subTest(candidate_directory=value):
                result = run_block(script, governed_contract_environment(CANDIDATE_DIRECTORY=value))

                self.assertNotEqual(0, result.returncode)
                self.assertIn("candidate-directory", result.stderr)

    def test_a_safe_relative_candidate_directory_is_accepted(self):
        script = extract_run_block(DOMAIN_RELEASE, "Validate governed release contract")

        for value in ("nupkgs", ".hexalith/release/candidate", "artifacts/nuget/out"):
            with self.subTest(candidate_directory=value):
                result = run_block(script, governed_contract_environment(CANDIDATE_DIRECTORY=value))

                self.assertEqual(0, result.returncode, result.stderr)

    def test_governed_mode_consumes_only_the_release_commit(self):
        source = job_slice(read(DOMAIN_RELEASE), "governed-release")

        self.assertIn("ref: ${{ inputs.release-commit }}", source)
        self.assertIn("HEXALITH_RELEASE_COMMIT: ${{ inputs.release-commit }}", source)
        self.assertIn("candidate: ${{ inputs.release-commit }}", source)
        self.assertNotIn("DISPATCH_SHA", source)

        guard = extract_run_block(DOMAIN_RELEASE, "Revalidate governed release source before Semantic Release")
        self.assertIn('"$checked_out_sha" != "$RELEASE_COMMIT"', guard)
        self.assertIn('"$RELEASE_COMMIT" != "$live_source_sha"', guard)

    def test_the_candidate_is_attested_before_any_publication_side_effect(self):
        source = job_slice(read(DOMAIN_RELEASE), "governed-release")
        candidate_index = source.index("- name: Prepare governed release candidate")
        attest_index = source.index("- name: Attest governed release candidate")
        verify_index = source.index("- name: Verify governed candidate attestation")
        semantic_index = source.index("- name: Semantic Release")

        self.assertLess(candidate_index, attest_index)
        self.assertLess(attest_index, verify_index)
        self.assertLess(verify_index, semantic_index)
        self.assertIn("subject-path: ${{ inputs.candidate-directory }}/*.nupkg", source)
        self.assertIn(
            "HEXALITH_RELEASE_ATTESTATION_BUNDLE: ${{ steps.candidate-attestation.outputs.bundle-path }}",
            source,
        )

    def test_a_missing_attestation_bundle_blocks_publication(self):
        script = extract_run_block(DOMAIN_RELEASE, "Verify governed candidate attestation")

        with tempfile.TemporaryDirectory() as temporary_directory:
            empty_bundle = Path(temporary_directory) / "empty.jsonl"
            empty_bundle.touch()
            for bundle in ("", str(empty_bundle)):
                with self.subTest(bundle=bundle):
                    result = run_block(script, {"ATTESTATION_BUNDLE": bundle}, cwd=temporary_directory)

                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("refusing to publish", result.stderr)

    def test_verification_data_is_always_collected_with_closed_null_fields(self):
        workflow = read(DOMAIN_RELEASE)
        source = job_slice(workflow, "governed-release")
        collect_index = source.index("- name: Collect governed release verification data")
        self.assertIn("if: ${{ always() }}", source[collect_index: collect_index + 400])

        script = extract_run_block(DOMAIN_RELEASE, "Collect governed release verification data")
        with tempfile.TemporaryDirectory() as temporary_directory:
            output_path = Path(temporary_directory) / "github-output"
            output_path.touch()
            result = run_block(
                script,
                {
                    "GITHUB_OUTPUT": str(output_path),
                    "RELEASE_REPOSITORY": "Hexalith/Hexalith.FrontComposer",
                    "RELEASE_WORKFLOW_REF": "Hexalith/Hexalith.FrontComposer/.github/workflows/release.yml@refs/heads/main",
                    "RELEASE_RUN_ID": "77",
                    "RELEASE_RUN_ATTEMPT": "1",
                    "RELEASE_CONCLUSION": "failure",
                    "RELEASE_COMMIT": GOVERNED_CANDIDATE,
                    "CI_HANDOFF_ARTIFACT": "dependency-release-handoff",
                    "CI_RUN_ID": "42",
                    "CI_RUN_ATTEMPT": "1",
                    "POLICY_REPOSITORY": "github.com/hexalith/hexalith.frontcomposer",
                    "POLICY_PATH": "eng/dependency-graph-policy.json",
                    "POLICY_COMMIT": GOVERNED_POLICY_COMMIT,
                    "POLICY_SHA256": GOVERNED_SHA256,
                    "EXPECTED_EVALUATOR_DIGEST": "b" * 64,
                    "PUBLISH_ENABLED": "false",
                    "RELEASE_REQUIRED": "",
                    "RELEASE_VERSION": "",
                    "CANDIDATE_INVENTORY": "",
                    "ATTESTATION_BUNDLE": "",
                    "ATTESTATION_ID": "",
                    "WORKFLOW_PROVENANCE": "",
                    "CLOSURE_DIGEST": "",
                },
                cwd=temporary_directory,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            document = json.loads(
                (Path(temporary_directory) / ".hexalith/release/governed/release-verification-data.json")
                .read_text(encoding="utf-8")
            )

        self.assertEqual("hexalith.builds-release-verification-data.v1", document["schema"])
        self.assertEqual(GOVERNED_CANDIDATE, document["candidate"])
        self.assertEqual(GOVERNED_POLICY_COMMIT, document["dependency_policy"]["commit"])
        self.assertFalse(document["publication"]["published"])
        self.assertIsNone(document["publication"]["version"])
        self.assertIsNone(document["attestation"]["bundle_path"])
        self.assertEqual([], document["assets"])
        self.assertEqual("failure", document["release_run"]["conclusion"])

    def test_governed_evidence_upload_never_omits_the_artifact(self):
        source = job_slice(read(DOMAIN_RELEASE), "governed-release")
        upload_index = source.index("- name: Upload governed release evidence")
        upload_step = source[upload_index: upload_index + 600]

        self.assertIn("if: ${{ always() }}", upload_step)
        self.assertIn("if-no-files-found: error", upload_step)


class GovernedCandidatePhaseTests(unittest.TestCase):
    """The candidate phase is the last point at which unattested bytes can be stopped."""

    def setUp(self):
        """Give each scenario an isolated workspace with a stubbed release toolchain."""
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.workspace = directory.name

    def pack(self, *names):
        """Return a candidate command that writes one .nupkg per given name."""
        writes = " && ".join(
            f'printf "%s" "{name} payload" > "$HEXALITH_RELEASE_CANDIDATE_DIRECTORY/{name}"'
            for name in names
        )
        return writes or "true"

    def test_a_resolved_version_produces_an_attestable_inventory(self):
        result, outputs = run_candidate_phase(
            self.workspace,
            CANDIDATE_COMMAND=self.pack("Hexalith.A.1.2.3.nupkg", "Hexalith.B.1.2.3.nupkg"),
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("true", outputs["release-required"])
        self.assertEqual("1.2.3", outputs["version"])
        self.assertEqual("2", outputs["package-count"])

        inventory = json.loads(
            Path(self.workspace, outputs["inventory-path"]).read_text(encoding="utf-8")
        )
        self.assertEqual("hexalith.builds-release-candidate.v1", inventory["schema"])
        self.assertEqual("1.2.3", inventory["version"])
        self.assertEqual(
            ["Hexalith.A.1.2.3.nupkg", "Hexalith.B.1.2.3.nupkg"],
            [row["name"] for row in inventory["packages"]],
        )
        for row in inventory["packages"]:
            self.assertRegex(row["sha256"], r"^[0-9a-f]{64}$")

    def test_no_resolvable_version_records_that_no_release_was_warranted(self):
        result, outputs = run_candidate_phase(
            self.workspace,
            stubs=stub_semantic_release(self.workspace, "There are no relevant changes"),
            CANDIDATE_COMMAND="exit 1",
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("false", outputs["release-required"])
        self.assertNotIn("version", outputs)

    def test_a_failed_dry_run_fails_closed_instead_of_publishing(self):
        result, outputs = run_candidate_phase(
            self.workspace,
            stubs=stub_semantic_release(self.workspace, "ERELEASEBRANCHES", exit_code=1),
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("could not resolve the next release version", result.stderr)
        self.assertEqual({}, outputs)

    def test_a_version_with_no_packages_fails_closed(self):
        # A resolved version with an empty candidate directory means the caller's pack
        # command silently did nothing. Publishing here would ship nothing attested.
        result, outputs = run_candidate_phase(self.workspace, CANDIDATE_COMMAND="true")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("produced no .nupkg packages", result.stderr)
        self.assertNotIn("release-required", outputs)

    def test_a_symlinked_package_is_refused(self):
        # A symlink lets the inventory and the attestation describe bytes that are not the
        # bytes in the candidate directory, and the target can be swapped afterwards.
        outside = Path(self.workspace) / "outside.nupkg"
        outside.write_text("someone else's bytes", encoding="utf-8")
        command = (
            'printf "%s" "real" > "$HEXALITH_RELEASE_CANDIDATE_DIRECTORY/Hexalith.A.1.2.3.nupkg"'
            f' && ln -s "{outside}" "$HEXALITH_RELEASE_CANDIDATE_DIRECTORY/Hexalith.B.1.2.3.nupkg"'
        )

        result, outputs = run_candidate_phase(self.workspace, CANDIDATE_COMMAND=command)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("must be regular files, not symlinks", result.stderr)
        self.assertNotIn("release-required", outputs)

    def test_a_package_count_that_contradicts_the_caller_declaration_is_refused(self):
        result, outputs = run_candidate_phase(
            self.workspace,
            HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT="3",
            CANDIDATE_COMMAND=self.pack("Hexalith.A.1.2.3.nupkg", "Hexalith.B.1.2.3.nupkg"),
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("expected-package-count declares 3", result.stderr)
        self.assertNotIn("release-required", outputs)

    def test_a_matching_package_count_is_accepted(self):
        result, outputs = run_candidate_phase(
            self.workspace,
            HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT="2",
            CANDIDATE_COMMAND=self.pack("Hexalith.A.1.2.3.nupkg", "Hexalith.B.1.2.3.nupkg"),
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("2", outputs["package-count"])

    def test_an_undeclared_package_count_does_not_constrain_the_inventory(self):
        # expected-package-count is only meaningful as a positive caller declaration; an
        # empty or zero value must not be read as "expect zero packages".
        for declared in ("", "0"):
            with self.subTest(declared=declared):
                with tempfile.TemporaryDirectory() as workspace:
                    result, outputs = run_candidate_phase(
                        workspace,
                        HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT=declared,
                        CANDIDATE_COMMAND=(
                            'printf "%s" "a" > '
                            '"$HEXALITH_RELEASE_CANDIDATE_DIRECTORY/Hexalith.A.1.2.3.nupkg"'
                        ),
                    )

                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual("1", outputs["package-count"])

    def test_the_candidate_phase_refuses_an_unsafe_directory_at_the_point_of_delete(self):
        # The contract gate already rejects these, but this step performs the delete, so it
        # must not depend on a check a future reordering could move away from it.
        sentinel = Path(self.workspace) / "keep.txt"
        sentinel.write_text("must survive", encoding="utf-8")

        for value in ("", ".", "..", "../escape", "nupkgs/../../etc", "/tmp/candidate"):
            with self.subTest(candidate_directory=value):
                result, _outputs = run_candidate_phase(
                    self.workspace, HEXALITH_RELEASE_CANDIDATE_DIRECTORY=value
                )

                self.assertNotEqual(0, result.returncode)
                self.assertIn("unsafe candidate-directory", result.stderr)

        self.assertTrue(sentinel.exists())

    def test_a_symlinked_candidate_directory_is_refused(self):
        target = Path(self.workspace) / "elsewhere"
        target.mkdir()
        Path(self.workspace, "nupkgs").symlink_to(target, target_is_directory=True)

        result, _outputs = run_candidate_phase(self.workspace)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("symlinked candidate-directory", result.stderr)
        self.assertTrue(target.exists())


class GovernedSourceRevalidationTests(unittest.TestCase):
    """The governed source guard is the last check before any publication side effect."""

    def setUp(self):
        """Stub `git` and `gh` so the guard can be executed without a network or a clone."""
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.workspace = directory.name
        self.script = extract_run_block(
            DOMAIN_RELEASE, "Revalidate governed release source before Semantic Release"
        )

    def environment(self, head, live, **overrides):
        """Build a guard environment whose stubbed git/gh report the given commits."""
        binaries = Path(self.workspace) / "bin"
        binaries.mkdir(exist_ok=True)
        git = binaries / "git"
        git.write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$FAKE_HEAD"\n', encoding="utf-8"
        )
        git.chmod(0o755)
        gh = binaries / "gh"
        gh.write_text('#!/usr/bin/env bash\nprintf "%s\\n" "$FAKE_LIVE"\n', encoding="utf-8")
        gh.chmod(0o755)
        environment = {
            "PATH": f"{binaries}:{os.environ['PATH']}",
            "FAKE_HEAD": head,
            "FAKE_LIVE": live,
            "GH_TOKEN": "unused-in-tests",
            "REPOSITORY": "Hexalith/Hexalith.FrontComposer",
            "SOURCE_BRANCH": "main",
            "RELEASE_COMMIT": GOVERNED_CANDIDATE,
        }
        environment.update(overrides)
        return environment

    def test_a_matching_checkout_and_live_tip_is_accepted(self):
        result = run_block(
            self.script,
            self.environment(GOVERNED_CANDIDATE, GOVERNED_CANDIDATE),
            cwd=self.workspace,
        )

        self.assertEqual(0, result.returncode, result.stderr)

    def test_a_checkout_that_is_not_the_release_commit_is_refused(self):
        result = run_block(
            self.script,
            self.environment("f" * 40, GOVERNED_CANDIDATE),
            cwd=self.workspace,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("checked-out", result.stderr.lower())

    def test_a_candidate_that_went_stale_during_setup_is_refused(self):
        result = run_block(
            self.script,
            self.environment(GOVERNED_CANDIDATE, "f" * 40),
            cwd=self.workspace,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("stale", result.stderr)


class GovernedOffParityTests(unittest.TestCase):
    """A caller that leaves the governed inputs unset keeps the pre-BUILD-REL-1 contract."""

    def test_the_two_release_jobs_are_mutually_exclusive(self):
        workflow = read(DOMAIN_RELEASE)

        self.assertIn("  release:\n", workflow)
        self.assertIn("if: ${{ !inputs.governed-release }}", job_slice(workflow, "release"))
        self.assertIn("if: ${{ inputs.governed-release }}", job_slice(workflow, "governed-release"))

    def test_every_governed_input_defaults_to_the_legacy_behavior(self):
        workflow = read(DOMAIN_RELEASE)
        declarations = (
            ("governed-release", "type: boolean", "default: false"),
            ("candidate-command", "type: string", "default: ''"),
            ("release-commit", "type: string", "default: ''"),
            ("ci-run-id", "type: string", "default: ''"),
            ("ci-run-attempt", "type: string", "default: ''"),
            ("dependency-policy-repository", "type: string", "default: ''"),
            ("dependency-policy-path", "type: string", "default: ''"),
            ("dependency-policy-commit", "type: string", "default: ''"),
            ("dependency-policy-sha256", "type: string", "default: ''"),
            ("expected-release-evaluator-digest", "type: string", "default: ''"),
        )

        for name, declared_type, default in declarations:
            with self.subTest(input=name):
                start = workflow.index(f"\n      {name}:\n")
                block = workflow[start: start + 700]
                self.assertIn("required: false", block)
                self.assertIn(declared_type, block)
                self.assertIn(default, block)

    def test_the_signing_secrets_are_optional_and_scoped_to_governed_steps(self):
        workflow = read(DOMAIN_RELEASE)
        legacy = job_slice(workflow, "release")
        governed = job_slice(workflow, "governed-release")

        self.assertIn(
            "NUGET_SIGNING_CERTIFICATE_BASE64:\n"
            "        description: 'Base64 PKCS#12 signing certificate used only by the governed candidate phase.'\n"
            "        required: false",
            workflow,
        )
        self.assertIn("NUGET_SIGNING_CERTIFICATE_PASSWORD:\n        description:", workflow)
        self.assertNotIn("NUGET_SIGNING_CERTIFICATE", legacy)
        self.assertIn(
            "NUGET_SIGNING_CERTIFICATE_BASE64: ${{ secrets.NUGET_SIGNING_CERTIFICATE_BASE64 }}",
            governed,
        )
        candidate_index = governed.index("- name: Prepare governed release candidate")
        attest_index = governed.index("- name: Attest governed release candidate")
        secret_uses = [
            index
            for index in range(len(governed))
            if governed.startswith("secrets.NUGET_SIGNING_CERTIFICATE", index)
        ]
        # Presence probes in the contract gate plus the candidate phase only: no signing
        # material reaches Semantic Release, the container publisher, or an artifact.
        for index in secret_uses:
            self.assertTrue(
                index < governed.index("- name: Checkout exact governed release candidate")
                or candidate_index < index < attest_index,
                f"signing secret referenced outside the governed candidate phase at offset {index}",
            )

    def test_the_governed_job_mirrors_the_legacy_step_sequence(self):
        workflow = read(DOMAIN_RELEASE)
        pattern = re.compile(r"^      - name: (?P<name>.+)$", re.MULTILINE)
        legacy = [match.group("name") for match in pattern.finditer(job_slice(workflow, "release"))]
        governed = [match.group("name") for match in pattern.finditer(job_slice(workflow, "governed-release"))]
        renamed = {
            "Revalidate current source before Semantic Release":
                "Revalidate governed release source before Semantic Release",
        }
        # The freeze gate is deliberately relocated to the front of the governed job so a
        # frozen caller skips the signing requirement and the build; its position is
        # asserted by test_the_governed_gate_is_the_first_step_of_the_governed_job.
        relocated = {"Resolve release publication freeze"}
        expected = [renamed.get(name, name) for name in legacy if name not in relocated]

        position = 0
        for name in expected:
            self.assertIn(name, governed[position:], f"governed job dropped the legacy step {name!r}")
            position = governed.index(name, position) + 1
        for name in relocated:
            self.assertIn(name, governed)

    def test_only_the_governed_job_requests_attestation_permissions(self):
        workflow = read(DOMAIN_RELEASE)
        governed = job_slice(workflow, "governed-release")

        self.assertEqual(1, workflow.count("id-token: write"))
        self.assertEqual(1, workflow.count("attestations: write"))
        self.assertIn("id-token: write", governed)
        self.assertIn("attestations: write", governed)
        self.assertNotIn("permissions:", job_slice(workflow, "release"))
        workflow_permissions = workflow[workflow.index("\npermissions:\n"): workflow.index("\njobs:\n")]
        self.assertNotIn("id-token", workflow_permissions)
        self.assertNotIn("attestations", workflow_permissions)

    def test_the_legacy_job_carries_no_governed_step(self):
        legacy = job_slice(read(DOMAIN_RELEASE), "release")

        for marker in (
            "attest-build-provenance",
            "governed-provenance",
            "Prepare governed release candidate",
            "release-commit",
        ):
            with self.subTest(marker=marker):
                self.assertNotIn(marker, legacy)

    def test_governed_ci_inputs_default_off_and_add_no_permission(self):
        workflow = read(DOMAIN_CI)

        self.assertIn("governed-ci:", workflow)
        governed_ci = workflow[workflow.index("\n      governed-ci:\n"):]
        self.assertIn("default: false", governed_ci[: governed_ci.index("\n      builds-execution-sha:")])
        self.assertIn("permissions:\n  contents: read\n", workflow)
        self.assertNotIn("id-token", workflow)
        self.assertNotIn("attestations", workflow)
        for step in (
            "Validate governed CI contract",
            "Checkout approved Builds actions",
            "Evaluate governed workflow provenance",
        ):
            with self.subTest(step=step):
                index = workflow.index(f"- name: {step}")
                self.assertIn("if: ${{ inputs.governed-ci }}", workflow[index: index + 400])


class ShellBlockSyntaxTests(unittest.TestCase):
    """Every inline shell body must parse, including the ones a test cannot execute."""

    def test_every_run_block_in_the_governed_workflows_parses(self):
        pattern = re.compile(r"^      - name: (?P<name>.+)$", re.MULTILINE)
        for path in (DOMAIN_RELEASE, DOMAIN_CI):
            workflow = read(path)
            seen = {}
            for match in pattern.finditer(workflow):
                name = match.group("name")
                occurrence = seen.get(name, 0)
                seen[name] = occurrence + 1
                try:
                    script = extract_run_block(path, name, occurrence)
                except (AssertionError, StopIteration):
                    continue
                with self.subTest(workflow=path.name, step=name, occurrence=occurrence):
                    result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned script.
                        ["bash", "-n"],
                        input=script,
                        capture_output=True,
                        text=True,
                        check=False,
                    )

                    self.assertEqual(0, result.returncode, result.stderr)

    def test_the_candidate_phase_heredoc_terminates_at_column_zero(self):
        # A YAML block scalar strips the common indent, so an indented heredoc terminator
        # would leave the whole Python program inside the heredoc and silently do nothing.
        for step in ("Prepare governed release candidate", "Collect governed release verification data"):
            with self.subTest(step=step):
                script = extract_run_block(DOMAIN_RELEASE, step)

                self.assertIn("<<'PY'\n", script)
                self.assertIn("\nPY\n", script)
                self.assertNotIn("\n          PY\n", script)


class GovernedIdentityAndPinningTests(unittest.TestCase):
    """Governed paths must resolve only immutable, identity-proven sources."""

    def test_every_governed_workflow_reference_is_literal_40_hex_or_local(self):
        for path in (DOMAIN_RELEASE, DOMAIN_CI, DAPR_INIT):
            for match in USES_PATTERN.finditer(read(path)):
                value = match.group("value")
                with self.subTest(path=path.name, uses=value):
                    if value.startswith("./"):
                        self.assertTrue(value.startswith("./.hexalith/builds-execution/"))
                        continue
                    self.assertRegex(value, PINNED_USES_PATTERN)

    def test_domain_ci_no_longer_resolves_builds_composites_from_a_branch(self):
        workflow = read(DOMAIN_CI)

        self.assertNotIn("@main", workflow)
        self.assertIn("Hexalith/Hexalith.Builds/Github/initialize-build@", workflow)
        self.assertIn("Hexalith/Hexalith.Builds/Github/dapr-init@", workflow)

    def test_governed_ci_proves_its_reusable_workflow_identity(self):
        script = extract_run_block(DOMAIN_CI, "Validate governed CI contract")
        builds_sha = "c" * 40
        base = {
            "BUILDS_EXECUTION_SHA": builds_sha,
            "CANDIDATE_COMMIT": GOVERNED_CANDIDATE,
            "EVENT_HEAD_SHA": GOVERNED_CANDIDATE,
            "POLICY_REPOSITORY": "github.com/hexalith/hexalith.frontcomposer",
            "POLICY_PATH": "eng/dependency-graph-policy.json",
            "POLICY_COMMIT": GOVERNED_POLICY_COMMIT,
            "POLICY_SHA256": GOVERNED_SHA256,
            "EXPECTED_EVALUATOR_DIGEST": "b" * 64,
            "RESOLVED_WORKFLOW_REPOSITORY": "Hexalith/Hexalith.Builds",
            "RESOLVED_WORKFLOW_SHA": builds_sha,
            "RESOLVED_WORKFLOW_REF":
                "Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@refs/heads/main",
        }

        with tempfile.TemporaryDirectory() as temporary_directory:
            fake_bin = Path(temporary_directory) / "bin"
            fake_bin.mkdir()
            git = fake_bin / "git"
            git.write_text(
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$FAKE_HEAD\"\n",
                encoding="utf-8",
            )
            git.chmod(0o755)
            path_prefix = {"PATH": f"{fake_bin}:{os.environ['PATH']}", "FAKE_HEAD": GOVERNED_CANDIDATE}

            accepted = run_block(script, {**base, **path_prefix})
            self.assertEqual(0, accepted.returncode, accepted.stderr)

            scenarios = (
                ({"RESOLVED_WORKFLOW_SHA": "d" * 40}, "does not match the approved builds-execution-sha"),
                ({"RESOLVED_WORKFLOW_REPOSITORY": "attacker/Hexalith.Builds"}, "approved Hexalith.Builds repository"),
                (
                    {"RESOLVED_WORKFLOW_REF": "Hexalith/Hexalith.Builds/.github/workflows/other.yml@refs/heads/main"},
                    "does not name Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml",
                ),
                ({"EVENT_HEAD_SHA": "e" * 40}, "must be the exact commit this CI run evaluates"),
                ({"CANDIDATE_COMMIT": "not-a-sha"}, "candidate-commit"),
                ({"BUILDS_EXECUTION_SHA": ""}, "builds-execution-sha"),
                ({"POLICY_REPOSITORY": "github.com/Hexalith/Hexalith.FrontComposer"}, "dependency-policy-repository"),
                ({"POLICY_PATH": ""}, "dependency-policy-path"),
                ({"POLICY_COMMIT": GOVERNED_POLICY_COMMIT.upper()}, "dependency-policy-commit"),
                ({"POLICY_SHA256": "zz" + "a" * 62}, "dependency-policy-sha256"),
                ({"EXPECTED_EVALUATOR_DIGEST": "b" * 63}, "expected-ci-evaluator-digest"),
                ({"EXPECTED_EVALUATOR_DIGEST": ""}, "expected-ci-evaluator-digest"),
            )
            for override, expected in scenarios:
                with self.subTest(expected=expected):
                    rejected = run_block(script, {**base, **path_prefix, **override})

                    self.assertNotEqual(0, rejected.returncode)
                    self.assertIn(expected, rejected.stderr)

            mismatched_head = run_block(
                script, {**base, **path_prefix, "FAKE_HEAD": "d" * 40}
            )

            self.assertNotEqual(0, mismatched_head.returncode)
            self.assertIn(
                "checked-out CI source does not match the declared exact candidate",
                mismatched_head.stderr,
            )

    def test_both_governed_stages_hand_their_expected_digest_to_the_evaluator(self):
        # The evaluator compares the digest against the closure it computed and fails
        # closed on a mismatch, so the workflows must actually pass it through.
        for path, input_name in (
            (DOMAIN_CI, "expected-ci-evaluator-digest"),
            (DOMAIN_RELEASE, "expected-release-evaluator-digest"),
        ):
            with self.subTest(workflow=path.name):
                workflow = read(path)
                index = workflow.index("uses: ./.hexalith/builds-execution/Github/governed-provenance")

                self.assertIn(
                    f"expected-evaluator-digest: ${{{{ inputs.{input_name} }}}}",
                    workflow[index: index + 900],
                )

    def test_governed_release_reuses_the_existing_builds_identity_gate(self):
        governed = job_slice(read(DOMAIN_RELEASE), "governed-release")

        self.assertIn("- name: Validate approved Builds execution identity", governed)
        self.assertIn("RESOLVED_WORKFLOW_SHA: ${{ job.workflow_sha }}", governed)
        self.assertIn("RESOLVED_WORKFLOW_REPOSITORY: ${{ job.workflow_repository }}", governed)
        self.assertIn("RESOLVED_WORKFLOW_REF: ${{ job.workflow_ref }}", governed)
        self.assertIn("uses: ./.hexalith/builds-execution/Github/governed-provenance", governed)
        self.assertIn("workflow-path: .github/workflows/domain-release.yml", governed)

    def test_governed_release_validates_its_workflow_ref_path_like_governed_ci(self):
        # Repository plus commit is satisfied by any reusable workflow in the approved
        # commit. Only the ref path proves it is domain-release.yml that is executing.
        # Occurrence 1 is the governed job's gate; occurrence 0 is the legacy job's.
        script = extract_run_block(DOMAIN_RELEASE, "Validate approved Builds execution identity", 1)
        builds_sha = "c" * 40
        base = {
            "BUILD_EXECUTION_SHA": builds_sha,
            "RESOLVED_WORKFLOW_REPOSITORY": "Hexalith/Hexalith.Builds",
            "RESOLVED_WORKFLOW_SHA": builds_sha,
            "RESOLVED_WORKFLOW_REF":
                "Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@refs/heads/main",
        }

        accepted = run_block(script, base)
        self.assertEqual(0, accepted.returncode, accepted.stderr)

        scenarios = (
            ({"BUILD_EXECUTION_SHA": "main"}, "40-character commit SHA"),
            ({"RESOLVED_WORKFLOW_REPOSITORY": "attacker/Hexalith.Builds"}, "approved Hexalith.Builds repository"),
            ({"RESOLVED_WORKFLOW_SHA": "d" * 40}, "does not match the approved Builds identity"),
            (
                {"RESOLVED_WORKFLOW_REF":
                    "Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@refs/heads/main"},
                "does not name Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml",
            ),
            ({"RESOLVED_WORKFLOW_REF": "refs/heads/main"}, "does not name"),
            (
                {"RESOLVED_WORKFLOW_REF":
                    "attacker/Hexalith.Builds/.github/workflows/domain-release.yml@refs/heads/main"},
                "does not name",
            ),
        )
        for override, expected in scenarios:
            with self.subTest(expected=expected):
                rejected = run_block(script, {**base, **override})

                self.assertNotEqual(0, rejected.returncode)
                self.assertIn(expected, rejected.stderr)

    def test_governed_provenance_is_exposed_to_callers_as_workflow_outputs(self):
        release = read(DOMAIN_RELEASE)
        ci = read(DOMAIN_CI)

        for name in (
            "governed-candidate",
            "governed-provenance-sha256",
            "governed-closure-digest",
            "governed-reusable-blob-sha256",
        ):
            with self.subTest(output=name):
                self.assertIn(f"      {name}:\n", release)
                self.assertIn(f"      {name}:\n", ci)
        self.assertIn("governed-attestation-bundle:", release)
        self.assertIn("governed-verification-data-path:", release)
        self.assertIn("governed-provenance-json:", ci)


if __name__ == "__main__":
    unittest.main()
