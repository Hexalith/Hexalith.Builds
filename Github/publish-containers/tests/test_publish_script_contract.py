import os
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


def write_executable(path, content):
    path.write_text(content, encoding="utf-8")
    path.chmod(0o755)


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
        self.assertIn("environment-name:", workflow)
        self.assertIn("default: 'production'", workflow)
        self.assertIn("environment: ${{ inputs.environment-name }}", workflow)
        self.assertNotIn("release-authority-url:", workflow)
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
        self.assertIn("builds-execution-sha: ${{ inputs.builds-execution-sha }}", workflow)
        self.assertIn("HEXALITH_BUILDS_EXECUTION_SHA: ${{ inputs.builds-execution-sha }}", workflow)
        self.assertIn("HEXALITH_RELEASE_ENVIRONMENT: ${{ inputs.environment-name }}", workflow)
        self.assertNotIn("HEXALITH_RELEASE_AUTHORITY_URL", workflow)
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

    def test_rejected_preflight_blocks_sdk_container_mutation(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            project = root / "EventStore.csproj"
            project.write_text("<Project />\n", encoding="utf-8")
            mutation_marker = root / "dotnet-ran"
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


if __name__ == "__main__":
    unittest.main()
