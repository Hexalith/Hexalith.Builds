import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
SMOKE_HELPER = SCRIPT_DIRECTORY / "smoke-container-platforms.sh"
AMD64_DIGEST = "sha256:" + ("a" * 64)
ARM64_DIGEST = "sha256:" + ("b" * 64)


def write_executable(path, content):
    path.write_text(content, encoding="utf-8")
    path.chmod(0o755)


class SmokeContainerPlatformsTests(unittest.TestCase):
    def run_smoke(self, *, preflight_pass=True, start_pass=True, liveness_pass=True):
        temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(temporary_directory.cleanup)
        root = Path(temporary_directory.name)
        evidence = root / "evidence"
        evidence.mkdir()
        (evidence / "oci-validation.json").write_text(
            json.dumps(
                {
                    "result": "pass",
                    "platforms": ["linux/amd64", "linux/arm64"],
                    "children": [
                        {"platform": "linux/amd64", "digest": AMD64_DIGEST},
                        {"platform": "linux/arm64", "digest": ARM64_DIGEST},
                    ],
                }
            ),
            encoding="utf-8",
        )
        fake_bin = root / "bin"
        fake_bin.mkdir()
        docker_log = root / "docker.log"
        write_executable(
            fake_bin / "docker",
            """#!/usr/bin/env bash
set -euo pipefail
printf '%s\t' "$@" >> "$FAKE_DOCKER_LOG"
printf '\n' >> "$FAKE_DOCKER_LOG"
if [ "${1:-}" = buildx ]; then
  [ "$FAKE_PREFLIGHT_PASS" = true ] || exit 1
  printf '%s\n' 'Platforms: linux/amd64, linux/arm64'
  exit 0
fi
if [ "${1:-}" = run ]; then
  [ "$FAKE_START_PASS" = true ] || { printf '%s\n' 'fixture image start failed' >&2; exit 1; }
  printf '%s\n' 'fixture-container-id'
  exit 0
fi
if [ "${1:-}" = port ]; then
  printf '%s\n' '127.0.0.1:43123'
  exit 0
fi
exit 0
""",
        )
        write_executable(
            fake_bin / "curl",
            """#!/usr/bin/env bash
set -euo pipefail
[ "$FAKE_LIVENESS_PASS" = true ] || exit 22
printf '%s\n' 'Healthy'
""",
        )
        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{fake_bin}:{environment['PATH']}",
                "FAKE_DOCKER_LOG": str(docker_log),
                "FAKE_PREFLIGHT_PASS": str(preflight_pass).lower(),
                "FAKE_START_PASS": str(start_pass).lower(),
                "FAKE_LIVENESS_PASS": str(liveness_pass).lower(),
                "HEXALITH_CONTAINER_SMOKE_TIMEOUT_SECONDS": "0.25",
                "HEXALITH_CONTAINER_SMOKE_INTERVAL_SECONDS": "0.05",
            }
        )
        result = subprocess.run(
            [
                "bash",
                str(SMOKE_HELPER),
                "--image",
                "registry.example.test/eventstore:3.76.1",
                "--evidence-directory",
                str(evidence),
            ],
            env=environment,
            cwd=root,
            capture_output=True,
            text=True,
            check=False,
        )
        summary_path = evidence / "smoke-results.json"
        summary = json.loads(summary_path.read_text(encoding="utf-8")) if summary_path.exists() else None
        return result, summary, docker_log.read_text(encoding="utf-8") if docker_log.exists() else "", evidence

    def test_both_child_digests_pass_the_same_loopback_liveness_smoke(self):
        result, summary, docker_log, evidence = self.run_smoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("pass", summary["result"])
        self.assertEqual(["pass", "pass"], [item["outcome"] for item in summary["platforms"]])
        self.assertIn(f"registry.example.test/eventstore@{AMD64_DIGEST}", docker_log)
        self.assertIn(f"registry.example.test/eventstore@{ARM64_DIGEST}", docker_log)
        self.assertIn("--platform\tlinux/amd64", docker_log)
        self.assertIn("--platform\tlinux/arm64", docker_log)
        self.assertIn("ASPNETCORE_URLS=http://+:8080", docker_log)
        self.assertIn("--publish\t127.0.0.1::8080", docker_log)
        self.assertGreaterEqual(docker_log.count("rm\t--force"), 2)
        self.assertTrue((evidence / "smoke-sha256.txt").is_file())

    def test_emulation_preflight_failure_is_not_a_product_failure(self):
        result, summary, docker_log, _ = self.run_smoke(preflight_pass=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("environment/emulation-setup-failure", summary["result"])
        self.assertNotIn("run\t", docker_log)

    def test_image_start_failure_is_classified(self):
        result, summary, _, _ = self.run_smoke(start_pass=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("image-start-failure", summary["result"])

    def test_liveness_timeout_is_classified(self):
        result, summary, _, _ = self.run_smoke(liveness_pass=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("liveness-timeout", summary["result"])


if __name__ == "__main__":
    unittest.main()
