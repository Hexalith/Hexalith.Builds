"""Unit tests for the BUILD-REL-1 governed workflow-provenance evaluator.

The evaluator is the only thing standing between a governed caller and a closure it
cannot actually verify, so these tests concentrate on the ways a closure can silently
stop being provable: a mutable reference, an expression, a source outside the approved
checkout, a cycle, and an identity that does not match the approved reusable workflow.
"""

import hashlib
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

MODULE_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = MODULE_ROOT.parents[1]
sys.path.insert(0, str(MODULE_ROOT))

import governed_provenance as evaluator  # noqa: E402  # path is prepared above.


COMMIT = "0123456789abcdef0123456789abcdef01234567"
OTHER_COMMIT = "89abcdef0123456789abcdef0123456789abcdef"
CANDIDATE = "fedcba9876543210fedcba9876543210fedcba98"
POLICY_SHA256 = "a" * 64
EVALUATOR_DIGEST = "b" * 64
WORKFLOW_PATH = ".github/workflows/domain-ci.yml"
PINNED_CHECKOUT = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"


class Arguments:
    """Mutable stand-in for the parsed command line the evaluator consumes."""

    def __init__(self, builds_root, **overrides):
        """Build a complete, valid governed-provenance request."""
        self.stage = "ci"
        self.builds_root = str(builds_root)
        self.builds_execution_sha = COMMIT
        self.workflow_path = WORKFLOW_PATH
        self.workflow_repository = evaluator.BUILDS_REPOSITORY
        self.workflow_sha = COMMIT
        self.workflow_ref = f"{evaluator.BUILDS_REPOSITORY}/{WORKFLOW_PATH}@refs/heads/main"
        self.workflow_file_path = WORKFLOW_PATH
        self.caller_repository = "Hexalith/Hexalith.FrontComposer"
        self.caller_workflow_ref = "Hexalith/Hexalith.FrontComposer/.github/workflows/ci.yml@refs/heads/main"
        self.run_id = "42"
        self.run_attempt = "1"
        self.event = "push"
        self.candidate = CANDIDATE
        self.policy_repository = "github.com/hexalith/hexalith.frontcomposer"
        self.policy_path = "eng/dependency-graph-policy.json"
        self.policy_commit = OTHER_COMMIT
        self.policy_sha256 = POLICY_SHA256
        self.expected_evaluator_digest = EVALUATOR_DIGEST
        self.output = str(Path(builds_root) / "provenance.json")
        for name, value in overrides.items():
            setattr(self, name, value)


class CheckoutBuilder:
    """Build a synthetic Hexalith.Builds checkout for one closure scenario."""

    def __init__(self, root):
        """Bind the builder to an empty directory."""
        self.root = Path(root)

    def workflow(self, body, path=WORKFLOW_PATH):
        """Write one reusable workflow source."""
        return self.write(path, body)

    def composite(self, directory, steps, using="composite"):
        """Write one composite action's metadata."""
        return self.write(
            f"{directory}/action.yml",
            f"name: 'Test'\ndescription: 'Test'\nruns:\n  using: {using}\n  steps:\n{steps}",
        )

    def write(self, relative_path, body):
        """Write one arbitrary source into the checkout."""
        destination = self.root / relative_path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(body, encoding="utf-8")
        return destination


class ScalarParsingTests(unittest.TestCase):
    """The closed YAML subset must never silently drop or mis-read a uses: edge."""

    def test_plain_quoted_and_commented_scalars_are_read_identically(self):
        source = "\n".join(
            (
                "jobs:",
                "  a:",
                "    steps:",
                f"      - uses: {PINNED_CHECKOUT} # v7.0.1",
                f"      - uses: '{PINNED_CHECKOUT}'",
                f'      - uses: "{PINNED_CHECKOUT}"',
                f"        uses:   {PINNED_CHECKOUT}",
            )
        )

        literals = [literal for _line, literal in evaluator.scan_uses(source, "source")]

        self.assertEqual([PINNED_CHECKOUT] * 4, literals)

    def test_a_hash_inside_a_quoted_scalar_is_not_a_comment(self):
        self.assertEqual("a#b", evaluator.strip_yaml_comment("'a#b'").strip("'"))
        self.assertEqual("value", evaluator.strip_yaml_comment("value # trailing"))
        self.assertEqual("a#b", evaluator.strip_yaml_comment("a#b"))

    def test_prose_that_merely_mentions_uses_is_not_an_edge(self):
        source = "description: >-\n  Evaluates the bounded static uses: closure of that workflow.\n"

        self.assertEqual([], evaluator.scan_uses(source, "source"))

    def test_ambiguous_or_unsupported_scalars_fail_closed(self):
        unsupported = (
            "      - uses:\n",
            "      - uses: [a, b]\n",
            "      - uses: *anchor\n",
            "      - uses: 'unterminated\n",
            "      - 'uses': actions/checkout@v7\n",
        )

        for source in unsupported:
            with self.subTest(source=source):
                with self.assertRaises(evaluator.ProvenanceError):
                    evaluator.scan_uses(source, "source")


class ClosureResolutionTests(unittest.TestCase):
    """Only immutable, in-checkout sources may enter a governed closure."""

    def setUp(self):
        """Create an isolated synthetic checkout for each scenario."""
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.temporary = directory.name
        self.builder = CheckoutBuilder(self.temporary)
        self.collector = evaluator.ClosureCollector(self.temporary, COMMIT)

    def test_a_mutable_reference_is_rejected(self):
        for literal in (
            "actions/checkout@v7.0.1",
            "actions/checkout@main",
            "Hexalith/Hexalith.Builds/Github/dapr-init@main",
            "actions/checkout@3D3C42E5AAC5BA805825DA76410C181273BA90B1",
            "actions/checkout@3d3c42e5",
        ):
            with self.subTest(literal=literal):
                with self.assertRaises(evaluator.ProvenanceError) as raised:
                    self.collector._resolve_literal(literal, "source:1")

                self.assertIn("40-hex", str(raised.exception))

    def test_an_expression_or_docker_reference_is_rejected(self):
        for literal, expected in (
            ("actions/checkout@${{ env.SHA }}", "must not be an expression"),
            ("docker://alpine:3.20", "not governable"),
        ):
            with self.subTest(literal=literal):
                with self.assertRaises(evaluator.ProvenanceError) as raised:
                    self.collector._resolve_literal(literal, "source:1")

                self.assertIn(expected, str(raised.exception))

    def test_a_local_reference_must_load_from_the_approved_checkout(self):
        self.assertEqual(
            ("local", "Github/dapr-init"),
            self.collector._resolve_literal("./.hexalith/builds-execution/Github/dapr-init", "source:1"),
        )

        for literal in ("./Github/dapr-init", "./.github/actions/x", "./.hexalith/builds-execution/../evil"):
            with self.subTest(literal=literal):
                with self.assertRaises(evaluator.ProvenanceError):
                    self.collector._resolve_literal(literal, "source:1")

    def test_a_builds_action_pinned_to_the_executing_commit_is_hashed_locally(self):
        self.assertEqual(
            ("local", "Github/dapr-init"),
            self.collector._resolve_literal(
                f"Hexalith/Hexalith.Builds/Github/dapr-init@{COMMIT}", "source:1"
            ),
        )

    def test_a_builds_action_pinned_to_another_commit_stays_an_external_coordinate(self):
        kind, target = self.collector._resolve_literal(
            f"Hexalith/Hexalith.Builds/Github/dapr-init@{OTHER_COMMIT}", "source:1"
        )

        self.assertEqual("external", kind)
        self.assertEqual(("github.com/hexalith/hexalith.builds", "Github/dapr-init", OTHER_COMMIT), target)

    def test_a_source_outside_the_checkout_is_refused(self):
        with self.assertRaises(evaluator.ProvenanceError):
            self.collector._read_source("../escape.yml", "reusable workflow")

    def test_a_composite_cycle_is_refused(self):
        self.builder.workflow(
            "jobs:\n  a:\n    steps:\n      - uses: ./.hexalith/builds-execution/Github/first\n"
        )
        self.builder.composite("Github/first", "    - uses: ./.hexalith/builds-execution/Github/second\n")
        self.builder.composite("Github/second", "    - uses: ./.hexalith/builds-execution/Github/first\n")

        with self.assertRaises(evaluator.ProvenanceError) as raised:
            self.collector.visit_workflow(WORKFLOW_PATH)

        self.assertIn("cycle", str(raised.exception))

    def test_a_diamond_is_visited_once_and_projected_once(self):
        self.builder.workflow(
            "jobs:\n"
            "  a:\n"
            "    steps:\n"
            "      - uses: ./.hexalith/builds-execution/Github/left\n"
            "      - uses: ./.hexalith/builds-execution/Github/right\n"
        )
        for side in ("left", "right"):
            self.builder.composite(
                f"Github/{side}", "    - uses: ./.hexalith/builds-execution/Github/shared\n"
            )
        self.builder.composite("Github/shared", f"    - uses: {PINNED_CHECKOUT}\n")

        self.collector.visit_workflow(WORKFLOW_PATH)
        closure = self.collector.projection(WORKFLOW_PATH)

        self.assertEqual(
            ["Github/left/action.yml", "Github/right/action.yml", "Github/shared/action.yml"],
            [action["path"] for action in closure["actions"]],
        )
        self.assertEqual(1, len(closure["external_actions"]))

    def test_a_docker_action_in_the_closure_is_refused(self):
        self.builder.workflow(
            "jobs:\n  a:\n    steps:\n      - uses: ./.hexalith/builds-execution/Github/docker\n"
        )
        self.builder.composite("Github/docker", "    - run: true\n", using="docker")

        with self.assertRaises(evaluator.ProvenanceError) as raised:
            self.collector.visit_workflow(WORKFLOW_PATH)

        self.assertIn("Docker actions are not governable", str(raised.exception))

    def test_composite_helper_files_are_hashed_alongside_the_metadata(self):
        self.builder.workflow(
            "jobs:\n  a:\n    steps:\n      - uses: ./.hexalith/builds-execution/Github/helper\n"
        )
        self.builder.composite("Github/helper", "    - run: python3 helper.py\n")
        payload = "print('helper')\n"
        self.builder.write("Github/helper/helper.py", payload)

        self.collector.visit_workflow(WORKFLOW_PATH)
        closure = self.collector.projection(WORKFLOW_PATH)

        self.assertEqual(
            ["Github/helper/helper.py"], [helper["path"] for helper in closure["helpers"]]
        )
        self.assertEqual(
            hashlib.sha256(payload.encode("utf-8")).hexdigest(),
            closure["helpers"][0]["blob_sha256"],
        )

    def test_helpers_in_nested_directories_are_hashed_too(self):
        # A helper one directory down executes exactly like a top-level one. Hashing only
        # the composite's immediate siblings would leave a nested Python package or a
        # bundled JavaScript entrypoint outside the digest the consumer accepted.
        self.builder.workflow(
            "jobs:\n  a:\n    steps:\n      - uses: ./.hexalith/builds-execution/Github/helper\n"
        )
        self.builder.composite("Github/helper", "    - run: node dist/main.js\n")
        self.builder.write("Github/helper/dist/main.js", "process.exit(0);\n")
        nested = "print('nested')\n"
        self.builder.write("Github/helper/lib/support/tool.py", nested)

        self.collector.visit_workflow(WORKFLOW_PATH)
        closure = self.collector.projection(WORKFLOW_PATH)
        helpers = {helper["path"]: helper["blob_sha256"] for helper in closure["helpers"]}

        self.assertEqual(
            {"Github/helper/dist/main.js", "Github/helper/lib/support/tool.py"}, set(helpers)
        )
        self.assertEqual(
            hashlib.sha256(nested.encode("utf-8")).hexdigest(),
            helpers["Github/helper/lib/support/tool.py"],
        )

    def test_never_tracked_build_output_is_not_hashed(self):
        # __pycache__ is regenerated by whichever interpreter happens to run, so hashing it
        # would make the closure digest depend on the runner rather than on the sources.
        self.builder.workflow(
            "jobs:\n  a:\n    steps:\n      - uses: ./.hexalith/builds-execution/Github/helper\n"
        )
        self.builder.composite("Github/helper", "    - run: python3 helper.py\n")
        self.builder.write("Github/helper/helper.py", "print('helper')\n")
        self.builder.write("Github/helper/__pycache__/helper.cpython-312.pyc", "bytecode")
        self.builder.write("Github/helper/lib/__pycache__/nested.pyc", "bytecode")
        self.builder.write("Github/helper/node_modules/pkg/index.js", "module.exports = 1;\n")

        self.collector.visit_workflow(WORKFLOW_PATH)
        closure = self.collector.projection(WORKFLOW_PATH)

        self.assertEqual(
            ["Github/helper/helper.py"], [helper["path"] for helper in closure["helpers"]]
        )

    def test_helper_bytes_count_toward_the_source_ceiling(self):
        # Counting only workflows and actions would let a composite carry unlimited hashed
        # helper bytes past a bound the consumer agreed to validate.
        self.builder.workflow(
            "jobs:\n  a:\n    steps:\n      - uses: ./.hexalith/builds-execution/Github/helper\n"
        )
        self.builder.composite("Github/helper", "    - run: true\n")
        for index in range(evaluator.MAX_CLOSURE_SOURCES):
            self.builder.write(f"Github/helper/helper{index:04d}.py", f"# {index}\n")

        with self.assertRaises(evaluator.ProvenanceError) as raised:
            self.collector.visit_workflow(WORKFLOW_PATH)

        self.assertIn("source ceiling", str(raised.exception))

    def test_the_closure_digest_covers_every_recorded_source(self):
        self.builder.workflow("jobs:\n  a:\n    steps:\n      - run: true\n")
        self.collector.visit_workflow(WORKFLOW_PATH)
        closure = self.collector.projection(WORKFLOW_PATH)
        recomputed = dict(closure)
        digest = recomputed.pop("closure_digest")

        self.assertEqual(digest, evaluator.canonical_digest(recomputed))

        self.builder.workflow("jobs:\n  a:\n    steps:\n      - run: false\n")
        changed = evaluator.ClosureCollector(self.temporary, COMMIT)
        changed.visit_workflow(WORKFLOW_PATH)

        self.assertNotEqual(digest, changed.projection(WORKFLOW_PATH)["closure_digest"])

    def test_only_reusable_workflow_paths_may_be_the_closure_root(self):
        for path in ("Github/dapr-init/action.yml", ".github/workflows/notes.md", "domain-ci.yml"):
            with self.subTest(path=path):
                with self.assertRaises(evaluator.ProvenanceError):
                    self.collector.visit_workflow(path)


class WorkflowIdentityTests(unittest.TestCase):
    """A governed run must prove it is the approved reusable workflow, not a look-alike."""

    def test_a_matching_identity_is_accepted(self):
        self.assertEqual(COMMIT, evaluator.validate_workflow_identity(Arguments("/tmp")))

    def test_a_mismatched_identity_is_rejected(self):
        scenarios = (
            ({"workflow_repository": "attacker/Hexalith.Builds"}, "must be defined by"),
            ({"workflow_sha": OTHER_COMMIT}, "does not match the approved builds-execution-sha"),
            ({"builds_execution_sha": "main"}, "40-character commit SHA"),
            ({"workflow_ref": "refs/heads/main"}, "is not an owner/repository"),
            (
                {"workflow_ref": f"attacker/Hexalith.Builds/{WORKFLOW_PATH}@refs/heads/main"},
                "does not name the approved",
            ),
            (
                {
                    "workflow_ref": (
                        f"{evaluator.BUILDS_REPOSITORY}/.github/workflows/other.yml@refs/heads/main"
                    )
                },
                "does not match the declared governed workflow",
            ),
            (
                {"workflow_file_path": ".github/workflows/other.yml"},
                "does not match the declared",
            ),
        )

        for override, expected in scenarios:
            with self.subTest(expected=expected):
                with self.assertRaises(evaluator.ProvenanceError) as raised:
                    evaluator.validate_workflow_identity(Arguments("/tmp", **override))

                self.assertIn(expected, str(raised.exception))

    def test_an_absent_workflow_file_path_is_tolerated(self):
        # job.workflow_file_path is newer than the other job identity fields; treating an
        # empty value as a mismatch would break the contract on runners that omit it.
        self.assertEqual(
            COMMIT, evaluator.validate_workflow_identity(Arguments("/tmp", workflow_file_path=""))
        )


class DependencyPolicyProjectionTests(unittest.TestCase):
    """Policy coordinates are all-or-nothing so a partial claim cannot look complete."""

    def test_a_complete_coordinate_is_normalized(self):
        projection = evaluator.dependency_policy_projection(Arguments("/tmp"))

        self.assertEqual(
            {
                "repository": "github.com/hexalith/hexalith.frontcomposer",
                "path": "eng/dependency-graph-policy.json",
                "commit": OTHER_COMMIT,
                "sha256": POLICY_SHA256,
            },
            projection,
        )

    def test_an_absent_coordinate_projects_to_the_closed_null(self):
        arguments = Arguments(
            "/tmp", policy_repository="", policy_path="", policy_commit="", policy_sha256=""
        )

        self.assertIsNone(evaluator.dependency_policy_projection(arguments))

    def test_a_partial_coordinate_is_rejected(self):
        for absent in ("policy_repository", "policy_path", "policy_commit", "policy_sha256"):
            with self.subTest(absent=absent):
                with self.assertRaises(evaluator.ProvenanceError) as raised:
                    evaluator.dependency_policy_projection(Arguments("/tmp", **{absent: ""}))

                self.assertIn("must be declared together", str(raised.exception))

    def test_an_unnormalized_coordinate_is_rejected(self):
        for override in (
            {"policy_repository": "Hexalith/Hexalith.FrontComposer"},
            {"policy_path": "/etc/policy.json"},
            {"policy_path": "eng/../../policy.json"},
            {"policy_commit": "main"},
            {"policy_sha256": "a" * 63},
        ):
            with self.subTest(override=override):
                with self.assertRaises(evaluator.ProvenanceError):
                    evaluator.dependency_policy_projection(Arguments("/tmp", **override))


class EndToEndTests(unittest.TestCase):
    """The evaluator must produce a stable document for the real shipped workflows."""

    def setUp(self):
        """Point each scenario at this repository as its approved checkout."""
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.temporary = directory.name
        self.output = Path(self.temporary) / "provenance.json"

    def arguments(self, stage, workflow_path, **overrides):
        """Build a request that evaluates one of this repository's shipped workflows."""
        commit = overrides.pop("commit", COMMIT)
        # The expected digest is now enforced against the real closure, so a scenario that
        # is not about digest enforcement must declare no expectation rather than a
        # placeholder that could never match.
        overrides.setdefault("expected_evaluator_digest", "")
        return Arguments(
            REPOSITORY_ROOT,
            stage=stage,
            workflow_path=workflow_path,
            builds_execution_sha=commit,
            workflow_sha=commit,
            workflow_ref=f"{evaluator.BUILDS_REPOSITORY}/{workflow_path}@refs/heads/main",
            workflow_file_path=workflow_path,
            output=str(self.output),
            **overrides,
        )

    def test_both_shipped_governed_workflows_evaluate_to_a_complete_closure(self):
        for stage, workflow_path in (
            ("ci", ".github/workflows/domain-ci.yml"),
            ("release", ".github/workflows/domain-release.yml"),
        ):
            with self.subTest(stage=stage):
                provenance = evaluator.build_provenance(self.arguments(stage, workflow_path))
                closure = provenance["closure"]
                local = {action["path"] for action in closure["actions"]}

                self.assertEqual(evaluator.PROVENANCE_SCHEMA, provenance["schema"])
                self.assertEqual(CANDIDATE, provenance["candidate"])
                self.assertEqual(workflow_path, closure["reusable"]["workflow_path"])
                self.assertEqual("github.com/hexalith/hexalith.builds", closure["reusable"]["repository"])
                # The evaluator hashes itself: a change to the collector changes the digest
                # the consumer accepted, which is the whole point of the closure.
                self.assertIn("Github/governed-provenance/action.yml", local)
                self.assertIn(
                    "Github/governed-provenance/governed_provenance.py",
                    {helper["path"] for helper in closure["helpers"]},
                )
                self.assertIn("Github/initialize-build/action.yml", local)
                for external in closure["external_actions"]:
                    self.assertRegex(external["commit"], r"^[0-9a-f]{40}$")

    def test_governed_ci_loads_builds_composites_from_the_reusable_workflow_commit(self):
        # domain-ci.yml carries two forms of each Builds-owned composite. The governed form
        # must resolve against the executing commit; the legacy 40-hex pin may name another
        # commit and is therefore only ever an external coordinate.
        provenance = evaluator.build_provenance(self.arguments("ci", ".github/workflows/domain-ci.yml"))
        closure = provenance["closure"]
        local = {action["path"] for action in closure["actions"]}
        builds_pins = {
            source["path"]
            for source in closure["external_actions"]
            if source["repository"] == "github.com/hexalith/hexalith.builds"
        }

        self.assertEqual(
            {
                "Github/dapr-init/action.yml",
                "Github/governed-provenance/action.yml",
                "Github/initialize-build/action.yml",
            },
            local,
        )
        self.assertEqual({"Github/dapr-init", "Github/initialize-build"}, builds_pins)
        for action in closure["actions"]:
            self.assertEqual(COMMIT, action["commit"])

    def test_the_release_closure_includes_the_container_publisher(self):
        provenance = evaluator.build_provenance(
            self.arguments("release", ".github/workflows/domain-release.yml")
        )
        local = {action["path"] for action in provenance["closure"]["actions"]}
        external = {source["repository"] for source in provenance["closure"]["external_actions"]}

        self.assertIn("Github/publish-containers/action.yml", local)
        self.assertIn("github.com/actions/attest-build-provenance", external)

    def test_the_document_is_deterministic_apart_from_its_timestamp(self):
        first = evaluator.build_provenance(self.arguments("ci", ".github/workflows/domain-ci.yml"))
        second = evaluator.build_provenance(self.arguments("ci", ".github/workflows/domain-ci.yml"))
        for document in (first, second):
            document.pop("generated_at_utc")

        self.assertEqual(evaluator.canonical_bytes(first), evaluator.canonical_bytes(second))

    def test_the_emitted_outputs_describe_the_written_document(self):
        provenance = evaluator.build_provenance(self.arguments("ci", ".github/workflows/domain-ci.yml"))
        github_output = Path(self.temporary) / "github-output"
        github_output.touch()
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(self.temporary)
        with mock.patch.dict("os.environ", {"GITHUB_OUTPUT": str(github_output)}):
            outputs = evaluator.write_outputs(provenance, "provenance.json")

        payload = self.output.read_bytes()
        emitted = dict(
            line.split("=", 1) for line in github_output.read_text(encoding="utf-8").splitlines()
        )

        self.assertEqual(hashlib.sha256(payload).hexdigest(), outputs["provenance-sha256"])
        self.assertEqual(json.loads(payload), provenance)
        self.assertEqual(outputs["closure-digest"], emitted["closure-digest"])
        self.assertEqual(provenance["closure"]["actions"], json.loads(emitted["actions-json"]))
        self.assertEqual(provenance, json.loads(emitted["provenance-json"]))
        self.assertNotIn("\n", emitted["provenance-json"])

    def test_one_canonical_serialization_backs_the_file_the_output_and_the_digest(self):
        # A consumer hashes the artifact it received and compares it with the emitted
        # digest. That only works if the file, the provenance-json output, and the hashed
        # bytes are the same byte string, not three equivalent serializations.
        provenance = evaluator.build_provenance(self.arguments("ci", ".github/workflows/domain-ci.yml"))
        github_output = Path(self.temporary) / "github-output"
        github_output.touch()
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(self.temporary)
        with mock.patch.dict("os.environ", {"GITHUB_OUTPUT": str(github_output)}):
            outputs = evaluator.write_outputs(provenance, "provenance.json")

        payload = self.output.read_bytes()

        self.assertEqual(evaluator.canonical_bytes(provenance), payload)
        self.assertEqual(payload, outputs["provenance-json"].encode("utf-8"))
        self.assertEqual(hashlib.sha256(payload).hexdigest(), outputs["provenance-sha256"])
        self.assertFalse(payload.endswith(b"\n"))

    def test_the_output_path_must_stay_inside_the_workspace(self):
        provenance = evaluator.build_provenance(self.arguments("ci", ".github/workflows/domain-ci.yml"))
        self.addCleanup(os.chdir, os.getcwd())
        os.chdir(self.temporary)
        for output_path in ("/tmp/outside.json", "../outside.json", "nested/../../outside.json"):
            with self.subTest(output_path=output_path):
                with self.assertRaises(evaluator.ProvenanceError) as raised:
                    evaluator.write_outputs(provenance, output_path)

                self.assertRegex(str(raised.exception), "relative POSIX path|unsafe path segment|escapes the workspace")
                self.assertFalse((Path(self.temporary) / "outside.json").exists())

    def test_the_expected_evaluator_digest_is_enforced_for_both_stages(self):
        # Recording the expected digest without comparing it would make the field
        # decorative: the caller would believe it authorized one closure while any closure
        # passed. Both the CI and the Release path must reject a mismatch.
        for stage, workflow_path in (
            ("ci", ".github/workflows/domain-ci.yml"),
            ("release", ".github/workflows/domain-release.yml"),
        ):
            with self.subTest(stage=stage):
                unconstrained = evaluator.build_provenance(self.arguments(stage, workflow_path))
                digest = unconstrained["closure"]["closure_digest"]

                accepted = evaluator.build_provenance(
                    self.arguments(stage, workflow_path, expected_evaluator_digest=digest)
                )

                self.assertEqual(digest, accepted["closure"]["closure_digest"])
                self.assertEqual(digest, accepted["expected_evaluator_digest"])

                with self.assertRaises(evaluator.ProvenanceError) as raised:
                    evaluator.build_provenance(
                        self.arguments(stage, workflow_path, expected_evaluator_digest="c" * 64)
                    )

                self.assertIn("does not match the expected evaluator digest", str(raised.exception))

    def test_a_digest_mismatch_writes_no_provenance_document(self):
        arguments = self.arguments(
            "release", ".github/workflows/domain-release.yml", expected_evaluator_digest="c" * 64
        )
        argv = [
            "--stage", arguments.stage,
            "--builds-root", arguments.builds_root,
            "--builds-execution-sha", arguments.builds_execution_sha,
            "--workflow-path", arguments.workflow_path,
            "--workflow-repository", arguments.workflow_repository,
            "--workflow-sha", arguments.workflow_sha,
            "--workflow-ref", arguments.workflow_ref,
            "--caller-repository", arguments.caller_repository,
            "--candidate", arguments.candidate,
            "--expected-evaluator-digest", arguments.expected_evaluator_digest,
            "--output", str(self.output),
        ]

        self.assertEqual(1, evaluator.main(argv))
        self.assertFalse(self.output.exists())

    def test_the_command_line_fails_closed_with_a_support_safe_diagnostic(self):
        arguments = self.arguments("ci", ".github/workflows/domain-ci.yml", commit=OTHER_COMMIT)
        argv = [
            "--stage", arguments.stage,
            "--builds-root", arguments.builds_root,
            "--builds-execution-sha", COMMIT,
            "--workflow-path", arguments.workflow_path,
            "--workflow-repository", arguments.workflow_repository,
            "--workflow-sha", OTHER_COMMIT,
            "--workflow-ref", arguments.workflow_ref,
            "--caller-repository", arguments.caller_repository,
            "--candidate", arguments.candidate,
            "--output", str(self.output),
        ]

        self.assertEqual(1, evaluator.main(argv))
        self.assertFalse(self.output.exists())

    def test_an_unknown_stage_is_rejected(self):
        with self.assertRaises(evaluator.ProvenanceError):
            evaluator.build_provenance(self.arguments("publish", ".github/workflows/domain-ci.yml"))


if __name__ == "__main__":
    unittest.main()
