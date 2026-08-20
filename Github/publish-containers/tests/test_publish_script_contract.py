import json
import os
import re
import shutil
import subprocess  # nosec B404 -- tests execute only repository-owned fixture scripts.
import tempfile
import unittest
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
PUBLISHER = SCRIPT_DIRECTORY / "publish-containers.sh"
ACTION = SCRIPT_DIRECTORY / "action.yml"
DOMAIN_RELEASE = SCRIPT_DIRECTORY.parents[1] / ".github" / "workflows" / "domain-release.yml"
BUILD_RELEASE = SCRIPT_DIRECTORY.parents[1] / ".github" / "workflows" / "build-release.yml"
RUNTIME_IDENTIFIERS = "linux-musl-x64;linux-musl-arm64"
# The inventory size the fixture module declares — a per-module value, not a shared invariant.
FIXTURE_PACKAGE_COUNT = 14


def write_executable(path, content):
    path.write_text(content, encoding="utf-8")
    path.chmod(0o755)


def write_package_manifest(root):
    path = root / "release-packages.json"
    path.write_text(
        json.dumps({"packages": [{"id": f"Package.{index}"} for index in range(FIXTURE_PACKAGE_COUNT)]}),
        encoding="utf-8",
    )
    return path


def extract_run_block(path, step_name):
    lines = path.read_text(encoding="utf-8").splitlines()
    step_index = next(index for index, line in enumerate(lines) if line.strip() == f"- name: {step_name}")
    run_index = next(
        index for index in range(step_index + 1, len(lines)) if lines[index].strip() == "run: |"
    )
    run_indent = len(lines[run_index]) - len(lines[run_index].lstrip())
    block = []
    for line in lines[run_index + 1 :]:
        indent = len(line) - len(line.lstrip())
        if line.strip() and indent <= run_indent:
            break
        block.append(line[run_indent + 2 :] if line.strip() else "")
    return "\n".join(block) + "\n"


def create_action_mismatch_fixture(root):
    approved = root / "approved"
    action_path = root / "action"
    fake_bin = root / "bin"
    approved.mkdir()
    fake_bin.mkdir()
    files = (
        "action.yml",
        "publish-containers.sh",
        "oci_registry_validator.py",
        "publication_preflight.py",
        "smoke-container-platforms.sh",
        "smoke_container_platforms.py",
    )
    for name in files:
        shutil.copy2(SCRIPT_DIRECTORY / name, approved / name)
    shutil.copytree(approved, action_path)
    (action_path / "publish-containers.sh").write_text("changed bytes\n", encoding="utf-8")
    write_executable(
        fake_bin / "curl",
        """#!/usr/bin/env bash
set -euo pipefail
output=''
source_url=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --output) shift; output="$1" ;;
    https://*) source_url="$1" ;;
  esac
  shift
done
cp "$FAKE_APPROVED/$(basename "$source_url")" "$output"
""",
    )
    return approved, action_path, fake_bin


def run_late_source_guard(checked_out_sha, live_source_sha, api_exit_code=0, dispatch_sha=None):
    with tempfile.TemporaryDirectory() as temporary_directory:
        fake_bin = Path(temporary_directory) / "bin"
        fake_bin.mkdir()
        write_executable(
            fake_bin / "gh",
            """#!/usr/bin/env bash
set -euo pipefail
expected_endpoint="repos/${REPOSITORY}/git/ref/heads/${SOURCE_BRANCH}"
if [ "$#" -ne 4 ] || [ "$1" != "api" ] || [ "$2" != "$expected_endpoint" ] || [ "$3" != "--jq" ] || [ "$4" != ".object.sha" ]; then
  echo "unexpected gh invocation: $*" >&2
  exit 97
fi
if [ "${FAKE_GH_EXIT_CODE:-0}" -ne 0 ]; then
  echo "simulated GitHub API failure" >&2
  exit "$FAKE_GH_EXIT_CODE"
fi
printf '%s\n' "$FAKE_LIVE_SOURCE_SHA"
""",
        )
        write_executable(
            fake_bin / "git",
            """#!/usr/bin/env bash
set -euo pipefail
if [ "$#" -ne 2 ] || [ "$1" != "rev-parse" ] || [ "$2" != "HEAD" ]; then
  echo "unexpected git invocation: $*" >&2
  exit 97
fi
printf '%s\n' "$FAKE_CHECKED_OUT_SHA"
""",
        )
        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{fake_bin}:{environment['PATH']}",
                "DISPATCH_SHA": checked_out_sha if dispatch_sha is None else dispatch_sha,
                "SOURCE_BRANCH": "main",
                "REPOSITORY": "Hexalith/Hexalith.EventStore",
                "FAKE_CHECKED_OUT_SHA": checked_out_sha,
                "FAKE_LIVE_SOURCE_SHA": live_source_sha,
                "FAKE_GH_EXIT_CODE": str(api_exit_code),
            }
        )
        return subprocess.run(  # nosec B603  # NOSONAR -- repository-owned workflow script.
            ["bash", "-c", extract_run_block(DOMAIN_RELEASE, "Revalidate current source before Semantic Release")],
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )


class PublishScriptContractTests(unittest.TestCase):
    def test_action_installs_immutable_registry_validator(self):
        action = ACTION.read_text(encoding="utf-8")

        self.assertIn(
            'cp "${GITHUB_ACTION_PATH}/oci_registry_validator.py" '
            ".hexalith/release/oci_registry_validator.py",
            action,
        )
        self.assertIn(
            "chmod +x .hexalith/release/oci_registry_validator.py",
            action,
        )

    def test_action_installs_platform_smoke_helper(self):
        action = ACTION.read_text(encoding="utf-8")

        self.assertIn(
            'cp "${GITHUB_ACTION_PATH}/smoke-container-platforms.sh" '
            ".hexalith/release/smoke-container-platforms.sh",
            action,
        )
        self.assertIn(
            "chmod +x .hexalith/release/smoke-container-platforms.sh",
            action,
        )

    def test_action_installs_publication_preflight(self):
        action = ACTION.read_text(encoding="utf-8")

        self.assertIn(
            'cp "${GITHUB_ACTION_PATH}/publication_preflight.py" '
            ".hexalith/release/publication_preflight.py",
            action,
        )
        self.assertIn(
            "chmod +x .hexalith/release/publication_preflight.py",
            action,
        )

    def test_action_installs_preflight_and_binds_approved_builds_bytes(self):
        action = ACTION.read_text(encoding="utf-8")

        self.assertIn("builds-execution-sha:", action)
        self.assertIn("HEXALITH_BUILDS_EXECUTION_SHA", action)
        self.assertIn("raw.githubusercontent.com/Hexalith/Hexalith.Builds", action)
        self.assertIn("cmp --silent", action)
        self.assertIn(
            'cp "${GITHUB_ACTION_PATH}/publication_preflight.py" '
            ".hexalith/release/publication_preflight.py",
            action,
        )
        self.assertIn("chmod +x .hexalith/release/publication_preflight.py", action)

    def test_domain_release_requires_one_exact_builds_identity_for_workflow_and_action(self):
        workflow = DOMAIN_RELEASE.read_text(encoding="utf-8")

        self.assertIn("builds-execution-sha:", workflow)
        self.assertIn(
            "builds-execution-sha:\n"
            "        description: 'Exact approved Hexalith.Builds commit resolved for the reusable workflow and nested publisher action.'\n"
            "        required: true",
            workflow,
        )
        self.assertIn("actions: read", workflow)
        self.assertIn("environment-name:", workflow)
        self.assertIn("default: 'production'", workflow)
        self.assertIn("environment: ${{ inputs.environment-name }}", workflow)
        self.assertIn("secrets:\n      NUGET_API_KEY:\n", workflow)
        self.assertIn("NUGET_API_KEY:\n        description:", workflow)
        self.assertIn("NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}", workflow)
        self.assertIn(
            "HEXALITH_ZOT_USERNAME: ${{ secrets.HEXALITH_ZOT_USERNAME }}",
            workflow,
        )
        self.assertIn(
            "HEXALITH_ZOT_API_KEY: ${{ secrets.HEXALITH_ZOT_API_KEY }}",
            workflow,
        )
        self.assertEqual(1, workflow.count("NUGET_API_KEY:\n        description:"))
        self.assertIn(
            "NUGET_API_KEY:\n        description: 'NuGet.org API key supplied explicitly by the caller.'\n        required: true",
            workflow,
        )
        self.assertIn(
            "HEXALITH_ZOT_USERNAME:\n"
            "        description: 'Zot username supplied explicitly by the caller when publishing containers.'\n"
            "        required: false",
            workflow,
        )
        self.assertIn(
            "HEXALITH_ZOT_API_KEY:\n"
            "        description: 'Zot API key supplied explicitly by the caller when publishing containers.'\n"
            "        required: false",
            workflow,
        )
        self.assertIn("release-authority-issue-url:", workflow)
        self.assertNotIn("release-owner-allowlist:", workflow)
        self.assertIn("job.workflow_sha", workflow)
        self.assertIn("job.workflow_repository", workflow)
        self.assertIn("BUILD_EXECUTION_SHA", workflow)
        self.assertIn("repository: Hexalith/Hexalith.Builds", workflow)
        self.assertIn("ref: ${{ inputs.builds-execution-sha }}", workflow)
        self.assertIn("path: .hexalith/builds-execution", workflow)
        self.assertIn(
            "uses: ./.hexalith/builds-execution/Github/publish-containers",
            workflow,
        )
        self.assertNotIn(
            "uses: Hexalith/Hexalith.Builds/Github/publish-containers@main",
            workflow,
        )
        # Every Builds-owned composite must load from the immutable local checkout. The
        # check is scoped to `uses:` edges because the governed identity gate legitimately
        # names Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml as the
        # workflow_ref path it requires.
        builds_uses = [
            match.group("value")
            for match in re.finditer(r"^\s*(?:-\s+)?uses:\s*(?P<value>\S+)", workflow, re.MULTILINE)
            if match.group("value").startswith("Hexalith/Hexalith.Builds/")
        ]
        self.assertEqual([], builds_uses)
        self.assertIn("builds-execution-sha: ${{ inputs.builds-execution-sha }}", workflow)
        self.assertIn("HEXALITH_BUILDS_EXECUTION_SHA: ${{ inputs.builds-execution-sha }}", workflow)
        self.assertIn("HEXALITH_RELEASE_ENVIRONMENT: ${{ inputs.environment-name }}", workflow)
        self.assertIn("HEXALITH_RELEASE_SOURCE_BRANCH: ${{ inputs.source-branch }}", workflow)
        self.assertIn(
            "HEXALITH_RELEASE_SOURCE_CI_WORKFLOW: ${{ inputs.source-ci-workflow }}",
            workflow,
        )
        self.assertIn(
            "HEXALITH_RELEASE_PACKAGE_MANIFEST: ${{ inputs.package-manifest }}",
            workflow,
        )
        self.assertIn(
            "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT: ${{ inputs.expected-package-count }}",
            workflow,
        )
        self.assertIn("HEXALITH_RELEASE_RESERVED_VERSION: ${{ inputs.reserved-version }}", workflow)
        self.assertIn(
            "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL: ${{ inputs.release-authority-issue-url }}",
            workflow,
        )
        self.assertIn("HEXALITH_RELEASE_AUTHORITY_OWNER: ${{ inputs.release-authority-owner }}", workflow)
        self.assertNotIn("HEXALITH_RELEASE_OWNER_ALLOWLIST_PATH", workflow)
        identity_index = workflow.index("- name: Validate approved Builds execution identity")
        checkout_index = workflow.index("- name: Checkout approved Builds actions")
        initialize_index = workflow.index("- name: Initialize root-declared submodules\n")
        self.assertLess(identity_index, checkout_index)
        self.assertLess(checkout_index, initialize_index)
        self.assertIn("uses: ./.hexalith/builds-execution/Github/initialize-build", workflow)
        self.assertIn("- name: Upload complete release evidence", workflow)
        self.assertIn("if: ${{ always() && inputs.publish-containers }}", workflow)
        self.assertIn("include-hidden-files: true", workflow)

    def test_workflow_sha_mismatch_fails_behaviorally(self):
        identity_script = extract_run_block(DOMAIN_RELEASE, "Validate approved Builds execution identity")
        identity_environment = os.environ.copy()
        identity_environment.update(
            {
                "BUILD_EXECUTION_SHA": "a" * 40,
                "RESOLVED_WORKFLOW_REPOSITORY": "Hexalith/Hexalith.Builds",
                "RESOLVED_WORKFLOW_SHA": "b" * 40,
            }
        )
        identity_result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned fixture script.
            ["bash", "-c", identity_script],
            env=identity_environment,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertNotEqual(0, identity_result.returncode)
        self.assertIn("does not match", identity_result.stderr)

    def test_domain_release_late_source_guard_accepts_matching_source(self):
        source_sha = "a" * 40

        result = run_late_source_guard(source_sha, source_sha)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_domain_release_late_source_guard_rejects_stale_source_before_semantic_release(self):
        workflow = DOMAIN_RELEASE.read_text(encoding="utf-8")

        result = run_late_source_guard("a" * 40, "b" * 40)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("became stale", result.stderr)
        self.assertIn("Redispatch Release", result.stderr)
        guard_index = workflow.index("- name: Revalidate current source before Semantic Release")
        semantic_release_index = workflow.index("- name: Semantic Release")
        self.assertLess(guard_index, semantic_release_index)
        guard_step = workflow[guard_index:semantic_release_index]
        self.assertNotIn("continue-on-error:", guard_step)
        self.assertNotIn("\n        if:", guard_step)

    def test_domain_release_late_source_guard_rejects_checkout_mismatch(self):
        result = run_late_source_guard("a" * 40, "a" * 40, dispatch_sha="b" * 40)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not match the dispatched commit SHA", result.stderr)

    def test_domain_release_late_source_guard_rejects_malformed_source_proofs(self):
        scenarios = (
            ("a" * 40, "a" * 40, "", "dispatched release source"),
            ("a" * 40, "a" * 40, "A" * 40, "dispatched release source"),
            ("not-a-sha", "a" * 40, "a" * 40, "checked-out release source"),
            ("a" * 40, "not-a-sha", "a" * 40, "live main SHA"),
        )

        for checked_out_sha, live_source_sha, dispatch_sha, expected_error in scenarios:
            with self.subTest(expected_error=expected_error):
                result = run_late_source_guard(
                    checked_out_sha,
                    live_source_sha,
                    dispatch_sha=dispatch_sha,
                )

                self.assertNotEqual(0, result.returncode)
                self.assertIn(expected_error, result.stderr)

    def test_domain_release_late_source_guard_fails_closed_on_api_error(self):
        result = run_late_source_guard("a" * 40, "a" * 40, api_exit_code=22)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("simulated GitHub API failure", result.stderr)

    def test_action_byte_mismatch_fails_behaviorally(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            approved, action_path, fake_bin = create_action_mismatch_fixture(root)
            action_environment = os.environ.copy()
            action_environment.update(
                {
                    "PATH": f"{fake_bin}:{action_environment['PATH']}",
                    "GITHUB_ACTION_PATH": str(action_path),
                    "HEXALITH_CONTAINER_PROJECTS": "EventStore.csproj|eventstore",
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "FAKE_APPROVED": str(approved),
                }
            )
            action_result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned fixture script.
                ["bash", "-c", extract_run_block(ACTION, "Install container publish helper")],
                cwd=root,
                env=action_environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertNotEqual(0, action_result.returncode)
            self.assertIn("do not match", action_result.stderr)
            self.assertFalse((root / ".hexalith" / "release" / "publish-containers.sh").exists())

    def test_domain_release_sha_pins_arm64_emulation_setup_before_publisher(self):
        workflow = DOMAIN_RELEASE.read_text(encoding="utf-8")
        qemu_marker = "docker/setup-qemu-action@"
        qemu_index = workflow.index(qemu_marker)
        publisher_index = workflow.index("- name: Prepare release container publisher")
        semantic_release_index = workflow.index("- name: Semantic Release")
        revision = workflow[qemu_index + len(qemu_marker) :].split()[0]

        self.assertEqual(40, len(revision))
        self.assertTrue(all(character in "0123456789abcdef" for character in revision))
        self.assertLess(qemu_index, publisher_index)
        self.assertLess(qemu_index, semantic_release_index)

    def test_builds_release_runs_publisher_contract_suite_before_release(self):
        workflow = BUILD_RELEASE.read_text(encoding="utf-8")

        publisher_gate = workflow.index("./Tools/test-publish-containers.ps1")
        release_step = workflow.index("- name: Create Release")

        self.assertLess(publisher_gate, release_step)

    def test_builds_release_is_manual_protected_and_does_not_push_git(self):
        workflow = BUILD_RELEASE.read_text(encoding="utf-8")
        package = json.loads((SCRIPT_DIRECTORY.parents[1] / "package.json").read_text(encoding="utf-8"))

        self.assertIn("on:\n  workflow_dispatch:", workflow)
        self.assertNotIn("\n  push:", workflow)
        self.assertIn("environment: production", workflow)
        self.assertIn("DISPATCH_REF", workflow)
        self.assertIn("refs/heads/main", workflow)
        self.assertIn("persist-credentials: false", workflow)
        self.assertEqual(["main"], package["release"]["branches"])
        self.assertNotIn("@semantic-release/changelog", package["release"]["plugins"])
        self.assertNotIn("@semantic-release/git", package["release"]["plugins"])
        self.assertNotIn("@semantic-release/changelog", package["devDependencies"])
        self.assertNotIn("@semantic-release/git", package["devDependencies"])

    def test_multi_platform_publish_is_exact_and_validation_gated(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            project = root / "EventStore.csproj"
            project.write_text("<Project />\n", encoding="utf-8")
            dotnet_arguments = root / "dotnet-arguments.txt"
            validator_arguments = root / "validator-arguments.txt"
            smoke_arguments = root / "smoke-arguments.txt"
            preflight_arguments = root / "preflight-arguments.txt"
            package_manifest = write_package_manifest(root)

            write_executable(
                fake_bin / "docker",
                "#!/usr/bin/env bash\nset -euo pipefail\ncat >/dev/null\nexit 0\n",
            )
            write_executable(
                fake_bin / "dotnet",
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$@\" > \"$FAKE_DOTNET_ARGUMENTS\"\n",
            )
            write_executable(
                root / "validate",
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$@\" > \"$FAKE_VALIDATOR_ARGUMENTS\"\n",
            )
            write_executable(
                root / "smoke",
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$@\" > \"$FAKE_SMOKE_ARGUMENTS\"\n",
            )
            write_executable(
                root / "preflight",
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$@\" > \"$FAKE_PREFLIGHT_ARGUMENTS\"\n",
            )

            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HEXALITH_CONTAINER_PROJECTS": f"{project}|eventstore",
                    "HEXALITH_ZOT_USERNAME": "fixture-user",
                    "HEXALITH_ZOT_API_KEY": "fixture-token",
                    "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                    "HEXALITH_OCI_VALIDATOR": str(root / "validate"),
                    "HEXALITH_CONTAINER_SMOKE": str(root / "smoke"),
                    "HEXALITH_PUBLICATION_PREFLIGHT": str(root / "preflight"),
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "HEXALITH_RELEASE_ENVIRONMENT": "production",
                    "HEXALITH_RELEASE_RESERVED_VERSION": "3.76.1",
                    "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                    "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                    "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                    "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": str(FIXTURE_PACKAGE_COUNT),
                    "GITHUB_REPOSITORY": "Hexalith/Hexalith.EventStore",
                    "GITHUB_SHA": "b" * 40,
                    "FAKE_DOTNET_ARGUMENTS": str(dotnet_arguments),
                    "FAKE_VALIDATOR_ARGUMENTS": str(validator_arguments),
                    "FAKE_SMOKE_ARGUMENTS": str(smoke_arguments),
                    "FAKE_PREFLIGHT_ARGUMENTS": str(preflight_arguments),
                }
            )

            result = subprocess.run(
                ["bash", str(PUBLISHER), "3.76.1"],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)

            arguments = dotnet_arguments.read_text(encoding="utf-8").splitlines()
            self.assertIn("--configuration", arguments)
            self.assertIn("Release", arguments)
            self.assertIn("/t:PublishContainer", arguments)
            self.assertIn(f'-p:RuntimeIdentifiers="{RUNTIME_IDENTIFIERS}"', arguments)
            self.assertIn(f'-p:ContainerRuntimeIdentifiers="{RUNTIME_IDENTIFIERS}"', arguments)
            self.assertIn("-p:ContainerImageFormat=OCI", arguments)
            self.assertIn("-p:UseHexalithProjectReferences=false", arguments)
            self.assertNotIn("--os", arguments)
            self.assertNotIn("--arch", arguments)
            self.assertFalse(any(value.startswith("-p:RuntimeIdentifier=") for value in arguments))

            validator = validator_arguments.read_text(encoding="utf-8").splitlines()
            smoke = smoke_arguments.read_text(encoding="utf-8").splitlines()
            expected_image = "registry.example.test/eventstore:3.76.1"
            self.assertIn(expected_image, validator)
            self.assertIn(expected_image, smoke)
            self.assertIn(str(root / "evidence" / "eventstore"), validator)
            self.assertIn(str(root / "evidence" / "eventstore"), smoke)
            preflight = preflight_arguments.read_text(encoding="utf-8").splitlines()
            self.assertIn("--phase", preflight)
            self.assertIn("container", preflight)
            self.assertIn("registry.example.test/eventstore", preflight)
            self.assertIn("--environment-name", preflight)
            self.assertIn("production", preflight)
            self.assertIn("--source-branch", preflight)
            self.assertIn("main", preflight)
            self.assertIn("--source-ci-workflow", preflight)
            self.assertIn("ci.yml", preflight)
            self.assertIn("--package-manifest", preflight)
            self.assertIn(str(package_manifest), preflight)
            self.assertIn("--expected-package-count", preflight)
            self.assertEqual(
                str(FIXTURE_PACKAGE_COUNT),
                preflight[preflight.index("--expected-package-count") + 1],
            )

    def test_three_container_set_is_preflighted_once_before_any_write(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            projects = []
            mappings = []
            for name in ("parties-ui", "parties", "parties-mcp"):
                project = root / f"{name}.csproj"
                project.write_text("<Project />\n", encoding="utf-8")
                projects.append(project)
                mappings.append(f"{project}|{name}")

            preflight_arguments = root / "preflight-arguments.txt"
            preflight_marker = root / "preflight-ran"
            docker_marker = root / "docker-ran"
            dotnet_invocations = root / "dotnet-invocations.txt"
            validator_invocations = root / "validator-invocations.txt"
            smoke_invocations = root / "smoke-invocations.txt"
            package_manifest = write_package_manifest(root)

            write_executable(
                root / "preflight",
                """#!/usr/bin/env bash
set -euo pipefail
[ ! -e "$FAKE_DOCKER_MARKER" ]
[ ! -e "$FAKE_DOTNET_INVOCATIONS" ]
printf '%s\n' "$@" > "$FAKE_PREFLIGHT_ARGUMENTS"
touch "$FAKE_PREFLIGHT_MARKER"
""",
            )
            write_executable(
                fake_bin / "docker",
                """#!/usr/bin/env bash
set -euo pipefail
[ -e "$FAKE_PREFLIGHT_MARKER" ]
cat >/dev/null
touch "$FAKE_DOCKER_MARKER"
""",
            )
            write_executable(
                fake_bin / "dotnet",
                """#!/usr/bin/env bash
set -euo pipefail
[ -e "$FAKE_PREFLIGHT_MARKER" ]
[ -e "$FAKE_DOCKER_MARKER" ]
printf '%s\n' "$*" >> "$FAKE_DOTNET_INVOCATIONS"
""",
            )
            write_executable(
                root / "validate",
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$*\" >> \"$FAKE_VALIDATOR_INVOCATIONS\"\n",
            )
            write_executable(
                root / "smoke",
                "#!/usr/bin/env bash\nset -euo pipefail\nprintf '%s\\n' \"$*\" >> \"$FAKE_SMOKE_INVOCATIONS\"\n",
            )

            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HEXALITH_CONTAINER_PROJECTS": "\n".join(mappings),
                    "HEXALITH_ZOT_USERNAME": "fixture-user",
                    "HEXALITH_ZOT_API_KEY": "fixture-token",
                    "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                    "HEXALITH_OCI_VALIDATOR": str(root / "validate"),
                    "HEXALITH_CONTAINER_SMOKE": str(root / "smoke"),
                    "HEXALITH_PUBLICATION_PREFLIGHT": str(root / "preflight"),
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "HEXALITH_RELEASE_ENVIRONMENT": "production",
                    "HEXALITH_RELEASE_RESERVED_VERSION": "3.89.0",
                    "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                    "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                    "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                    "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": str(FIXTURE_PACKAGE_COUNT),
                    "GITHUB_REPOSITORY": "Hexalith/Hexalith.Parties",
                    "GITHUB_SHA": "b" * 40,
                    "FAKE_PREFLIGHT_ARGUMENTS": str(preflight_arguments),
                    "FAKE_PREFLIGHT_MARKER": str(preflight_marker),
                    "FAKE_DOCKER_MARKER": str(docker_marker),
                    "FAKE_DOTNET_INVOCATIONS": str(dotnet_invocations),
                    "FAKE_VALIDATOR_INVOCATIONS": str(validator_invocations),
                    "FAKE_SMOKE_INVOCATIONS": str(smoke_invocations),
                }
            )

            result = subprocess.run(
                ["bash", str(PUBLISHER), "3.89.0"],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            preflight = preflight_arguments.read_text(encoding="utf-8").splitlines()
            self.assertEqual(1, preflight.count("--phase"))
            self.assertEqual("container", preflight[preflight.index("--phase") + 1])
            self.assertEqual(3, preflight.count("--container-repository"))
            declared_repositories = [
                preflight[index + 1]
                for index, value in enumerate(preflight)
                if value == "--container-repository"
            ]
            self.assertEqual(
                [f"registry.example.test/{name}" for name in ("parties-ui", "parties", "parties-mcp")],
                declared_repositories,
            )
            self.assertEqual(3, len(dotnet_invocations.read_text(encoding="utf-8").splitlines()))
            self.assertEqual(3, len(validator_invocations.read_text(encoding="utf-8").splitlines()))
            self.assertEqual(3, len(smoke_invocations.read_text(encoding="utf-8").splitlines()))

    def test_three_container_collision_blocks_login_and_every_container_write(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            mappings = []
            for name in ("parties", "parties-mcp", "parties-ui"):
                project = root / f"{name}.csproj"
                project.write_text("<Project />\n", encoding="utf-8")
                mappings.append(f"{project}|{name}")
            mutation_marker = root / "mutation-ran"
            package_manifest = write_package_manifest(root)

            write_executable(
                root / "preflight",
                """#!/usr/bin/env bash
set -euo pipefail
arguments=" $* "
[[ "$arguments" == *" --container-repository registry.example.test/parties "* ]]
[[ "$arguments" == *" --container-repository registry.example.test/parties-mcp "* ]]
[[ "$arguments" == *" --container-repository registry.example.test/parties-ui "* ]]
exit 1
""",
            )
            for command in ("docker", "dotnet"):
                write_executable(
                    fake_bin / command,
                    "#!/usr/bin/env bash\nset -euo pipefail\ntouch \"$FAKE_MUTATION_MARKER\"\n",
                )

            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HEXALITH_CONTAINER_PROJECTS": "\n".join(mappings),
                    "HEXALITH_ZOT_USERNAME": "fixture-user",
                    "HEXALITH_ZOT_API_KEY": "fixture-token",
                    "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                    "HEXALITH_OCI_VALIDATOR": "/bin/true",
                    "HEXALITH_CONTAINER_SMOKE": "/bin/true",
                    "HEXALITH_PUBLICATION_PREFLIGHT": str(root / "preflight"),
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "HEXALITH_RELEASE_ENVIRONMENT": "production",
                    "HEXALITH_RELEASE_RESERVED_VERSION": "3.89.0",
                    "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                    "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                    "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                    "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": str(FIXTURE_PACKAGE_COUNT),
                    "GITHUB_REPOSITORY": "Hexalith/Hexalith.Parties",
                    "GITHUB_SHA": "b" * 40,
                    "FAKE_MUTATION_MARKER": str(mutation_marker),
                }
            )

            result = subprocess.run(
                ["bash", str(PUBLISHER), "3.89.0"],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertFalse(mutation_marker.exists())

    def test_invalid_later_mapping_blocks_preflight_and_every_mutation(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            project = root / "parties.csproj"
            project.write_text("<Project />\n", encoding="utf-8")
            mutation_marker = root / "mutation-ran"
            package_manifest = write_package_manifest(root)
            for command in ("docker", "dotnet"):
                write_executable(
                    fake_bin / command,
                    "#!/usr/bin/env bash\nset -euo pipefail\ntouch \"$FAKE_MUTATION_MARKER\"\n",
                )
            write_executable(
                root / "preflight",
                "#!/usr/bin/env bash\nset -euo pipefail\ntouch \"$FAKE_MUTATION_MARKER\"\n",
            )

            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HEXALITH_CONTAINER_PROJECTS": (
                        f"{project}|parties\n{root / 'missing.csproj'}|parties-ui"
                    ),
                    "HEXALITH_ZOT_USERNAME": "fixture-user",
                    "HEXALITH_ZOT_API_KEY": "fixture-token",
                    "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                    "HEXALITH_OCI_VALIDATOR": "/bin/true",
                    "HEXALITH_CONTAINER_SMOKE": "/bin/true",
                    "HEXALITH_PUBLICATION_PREFLIGHT": str(root / "preflight"),
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "HEXALITH_RELEASE_ENVIRONMENT": "production",
                    "HEXALITH_RELEASE_RESERVED_VERSION": "3.89.0",
                    "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                    "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                    "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                    "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": str(FIXTURE_PACKAGE_COUNT),
                    "GITHUB_REPOSITORY": "Hexalith/Hexalith.Parties",
                    "GITHUB_SHA": "b" * 40,
                    "FAKE_MUTATION_MARKER": str(mutation_marker),
                }
            )

            result = subprocess.run(
                ["bash", str(PUBLISHER), "3.89.0"],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("Container project not found", result.stderr)
            self.assertFalse(mutation_marker.exists())

    def test_rejected_preflight_blocks_sdk_container_mutation(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            project = root / "EventStore.csproj"
            project.write_text("<Project />\n", encoding="utf-8")
            mutation_marker = root / "dotnet-ran"
            package_manifest = write_package_manifest(root)
            write_executable(fake_bin / "docker", "#!/usr/bin/env bash\ncat >/dev/null\n")
            write_executable(
                fake_bin / "dotnet",
                "#!/usr/bin/env bash\ntouch \"$FAKE_MUTATION_MARKER\"\n",
            )
            write_executable(root / "preflight", "#!/usr/bin/env bash\nexit 1\n")
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HEXALITH_CONTAINER_PROJECTS": f"{project}|eventstore",
                    "HEXALITH_ZOT_USERNAME": "fixture-user",
                    "HEXALITH_ZOT_API_KEY": "fixture-token",
                    "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                    "HEXALITH_OCI_VALIDATOR": "/bin/true",
                    "HEXALITH_CONTAINER_SMOKE": "/bin/true",
                    "HEXALITH_PUBLICATION_PREFLIGHT": str(root / "preflight"),
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "HEXALITH_RELEASE_ENVIRONMENT": "production",
                    "HEXALITH_RELEASE_RESERVED_VERSION": "3.76.1",
                    "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                    "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                    "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                    "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": str(FIXTURE_PACKAGE_COUNT),
                    "GITHUB_REPOSITORY": "Hexalith/Hexalith.EventStore",
                    "GITHUB_SHA": "b" * 40,
                    "FAKE_MUTATION_MARKER": str(mutation_marker),
                }
            )

            result = subprocess.run(
                ["bash", str(PUBLISHER), "3.76.1"],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertFalse(mutation_marker.exists())

    def test_repository_path_escape_is_rejected_before_evidence_or_publication(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            project = root / "EventStore.csproj"
            project.write_text("<Project />\n", encoding="utf-8")
            mutation_marker = root / "dotnet-ran"
            escaped_path = root.parent / f"escaped-{root.name}"
            package_manifest = write_package_manifest(root)
            write_executable(fake_bin / "docker", "#!/usr/bin/env bash\ncat >/dev/null\n")
            write_executable(
                fake_bin / "dotnet",
                "#!/usr/bin/env bash\ntouch \"$FAKE_MUTATION_MARKER\"\n",
            )
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HEXALITH_CONTAINER_PROJECTS": f"{project}|../{escaped_path.name}",
                    "HEXALITH_ZOT_USERNAME": "fixture-user",
                    "HEXALITH_ZOT_API_KEY": "fixture-token",
                    "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                    "HEXALITH_OCI_VALIDATOR": "/bin/true",
                    "HEXALITH_CONTAINER_SMOKE": "/bin/true",
                    "HEXALITH_PUBLICATION_PREFLIGHT": "/bin/true",
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                    "HEXALITH_RELEASE_ENVIRONMENT": "production",
                    "HEXALITH_RELEASE_RESERVED_VERSION": "3.76.1",
                    "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                    "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                    "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                    "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT": str(FIXTURE_PACKAGE_COUNT),
                    "GITHUB_REPOSITORY": "Hexalith/Hexalith.EventStore",
                    "GITHUB_SHA": "b" * 40,
                    "FAKE_MUTATION_MARKER": str(mutation_marker),
                }
            )

            result = subprocess.run(
                ["bash", str(PUBLISHER), "3.76.1"],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn(f"Container repository '../{escaped_path.name}' is invalid", result.stderr)
            self.assertFalse(mutation_marker.exists())
            self.assertFalse(escaped_path.exists())

    def test_absent_or_invalid_expected_package_count_blocks_publication(self):
        for declared in (None, "", "0", "-1", "05", "5.0", "five", " 5"):
            with self.subTest(declared=declared), tempfile.TemporaryDirectory() as temporary_directory:
                root = Path(temporary_directory)
                fake_bin = root / "bin"
                fake_bin.mkdir()
                project = root / "EventStore.csproj"
                project.write_text("<Project />\n", encoding="utf-8")
                mutation_marker = root / "dotnet-ran"
                package_manifest = write_package_manifest(root)
                write_executable(fake_bin / "docker", "#!/usr/bin/env bash\ncat >/dev/null\n")
                write_executable(
                    fake_bin / "dotnet",
                    "#!/usr/bin/env bash\ntouch \"$FAKE_MUTATION_MARKER\"\n",
                )
                environment = os.environ.copy()
                environment.pop("HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT", None)
                environment.update(
                    {
                        "PATH": f"{fake_bin}:{environment['PATH']}",
                        "HEXALITH_CONTAINER_PROJECTS": f"{project}|eventstore",
                        "HEXALITH_ZOT_USERNAME": "fixture-user",
                        "HEXALITH_ZOT_API_KEY": "fixture-token",
                        "HEXALITH_ZOT_REGISTRY": "registry.example.test",
                        "HEXALITH_OCI_VALIDATOR": "/bin/true",
                        "HEXALITH_CONTAINER_SMOKE": "/bin/true",
                        "HEXALITH_PUBLICATION_PREFLIGHT": "/bin/true",
                        "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                        "HEXALITH_BUILDS_EXECUTION_SHA": "a" * 40,
                        "HEXALITH_RELEASE_ENVIRONMENT": "production",
                        "HEXALITH_RELEASE_RESERVED_VERSION": "3.76.1",
                        "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL": "https://api.github.com/repos/Hexalith/Fixture/issues/123",
                        "HEXALITH_RELEASE_AUTHORITY_OWNER": "github:release-owner",
                        "HEXALITH_RELEASE_PACKAGE_MANIFEST": str(package_manifest),
                        "GITHUB_REPOSITORY": "Hexalith/Hexalith.EventStore",
                        "GITHUB_SHA": "b" * 40,
                        "FAKE_MUTATION_MARKER": str(mutation_marker),
                    }
                )
                if declared is not None:
                    environment["HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT"] = declared

                result = subprocess.run(
                    ["bash", str(PUBLISHER), "3.76.1"],
                    cwd=root,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )

                self.assertNotEqual(0, result.returncode)
                self.assertIn("HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT", result.stderr)
                self.assertFalse(mutation_marker.exists())


if __name__ == "__main__":
    unittest.main()
