import os
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
PUBLISHER = SCRIPT_DIRECTORY / "publish-containers.sh"
RUNTIME_IDENTIFIERS = "linux-musl-x64;linux-musl-arm64"


def write_executable(path, content):
    path.write_text(content, encoding="utf-8")
    path.chmod(0o755)


class PublishScriptContractTests(unittest.TestCase):
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
            self.assertIn(f"-p:RuntimeIdentifiers={RUNTIME_IDENTIFIERS}", arguments)
            self.assertIn(f"-p:ContainerRuntimeIdentifiers={RUNTIME_IDENTIFIERS}", arguments)
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
