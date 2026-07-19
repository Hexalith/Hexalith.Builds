import os
import subprocess
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

    def test_action_installs_publication_authority_validator(self):
        action = ACTION.read_text(encoding="utf-8")

        self.assertIn(
            'cp "${GITHUB_ACTION_PATH}/publication_authority.py" '
            ".hexalith/release/publication_authority.py",
            action,
        )
        self.assertIn(
            "chmod +x .hexalith/release/publication_authority.py",
            action,
        )

    def test_action_installs_authority_validator_and_binds_approved_builds_bytes(self):
        action = ACTION.read_text(encoding="utf-8")

        self.assertIn("builds-execution-sha:", action)
        self.assertIn("HEXALITH_BUILDS_EXECUTION_SHA", action)
        self.assertIn("raw.githubusercontent.com/Hexalith/Hexalith.Builds", action)
        self.assertIn("cmp --silent", action)
        self.assertIn(
            'cp "${GITHUB_ACTION_PATH}/publication_authority.py" '
            ".hexalith/release/publication_authority.py",
            action,
        )
        self.assertIn("chmod +x .hexalith/release/publication_authority.py", action)

    def test_domain_release_requires_one_exact_builds_identity_for_workflow_and_action(self):
        workflow = DOMAIN_RELEASE.read_text(encoding="utf-8")

        self.assertIn("builds-execution-sha:", workflow)
        self.assertIn("release-authority-url:", workflow)
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
        self.assertIn("HEXALITH_RELEASE_AUTHORITY_URL: ${{ inputs.release-authority-url }}", workflow)

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
                    "HEXALITH_CONTAINER_EVIDENCE_DIRECTORY": str(root / "evidence"),
                    "FAKE_DOTNET_ARGUMENTS": str(dotnet_arguments),
                    "FAKE_VALIDATOR_ARGUMENTS": str(validator_arguments),
                    "FAKE_SMOKE_ARGUMENTS": str(smoke_arguments),
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


if __name__ == "__main__":
    unittest.main()
