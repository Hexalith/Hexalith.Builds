import json
import os
import subprocess  # nosec B404 -- tests execute only repository-owned fixture scripts.
import tempfile
import unittest
from pathlib import Path


SCRIPT_DIRECTORY = Path(__file__).resolve().parent.parent
SMOKE_HELPER = SCRIPT_DIRECTORY / "smoke-container-platforms.sh"
AMD64_DIGEST = "sha256:" + ("a" * 64)
ARM64_DIGEST = "sha256:" + ("b" * 64)
DEFAULT_SETTINGS = {
    "preflight_pass": True,
    "runtime_emulation_pass": True,
    "pull_pass": True,
    "start_pass": True,
    "liveness_status": "200",
    "container_state": "running|0",
    "cleanup_pass": True,
    "timeout_value": "0.25",
}
DOCKER_FIXTURE = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\t' "$@" >> "$FAKE_DOCKER_LOG"
printf '\n' >> "$FAKE_DOCKER_LOG"
if [ "${1:-}" = buildx ]; then
  [ "$FAKE_PREFLIGHT_PASS" = true ] || exit 1
  printf '%s\n' 'Platforms: linux/amd64, linux/arm64'
  exit 0
fi
if [ "${1:-}" = pull ]; then
  [ "$FAKE_PULL_PASS" = true ] || { printf '%s\n' 'fixture registry pull failed' >&2; exit 1; }
  exit 0
fi
if [ "${1:-}" = run ]; then
  if [[ " $* " == *" --entrypoint "* ]]; then
    [ "$FAKE_RUNTIME_EMULATION_PASS" = true ] || { printf '%s\n' 'exec format error' >&2; exit 1; }
    printf '%s\n' 'aarch64'
    exit 0
  fi
  [ "$FAKE_START_PASS" = true ] || { printf '%s\n' 'fixture image start failed' >&2; exit 1; }
  printf '%s\n' 'fixture-container-id'
  exit 0
fi
if [ "${1:-}" = port ]; then printf '%s\n' '127.0.0.1:43123'; exit 0; fi
if [ "${1:-}" = inspect ]; then printf '%s\n' "$FAKE_CONTAINER_STATE"; exit 0; fi
if [ "${1:-}" = logs ]; then printf '%s\n' 'bounded fixture diagnostic'; exit 0; fi
if [ "${1:-}" = rm ]; then [ "$FAKE_CLEANUP_PASS" = true ] || exit 1; exit 0; fi
exit 0
"""
CURL_FIXTURE = """#!/usr/bin/env bash
set -euo pipefail
[ "${*: -1}" = "http://127.0.0.1:43123/alive" ] || exit 64
[[ " $* " != *" --location "* ]] || exit 64
[[ " $* " == *" --output /dev/null "* ]] || exit 64
[[ " $* " == *" --write-out %{http_code} "* ]] || exit 64
printf '%s' "$FAKE_LIVENESS_STATUS"
"""


def write_executable(path, content):
    path.write_text(content, encoding="utf-8")
    path.chmod(0o755)


def write_oci_evidence(evidence):
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


def smoke_environment(fake_bin, docker_log, settings):
    environment = os.environ.copy()
    environment.update(
        {
            "PATH": f"{fake_bin}:{environment['PATH']}",
            "FAKE_DOCKER_LOG": str(docker_log),
            "FAKE_PREFLIGHT_PASS": str(settings["preflight_pass"]).lower(),
            "FAKE_RUNTIME_EMULATION_PASS": str(settings["runtime_emulation_pass"]).lower(),
            "FAKE_PULL_PASS": str(settings["pull_pass"]).lower(),
            "FAKE_START_PASS": str(settings["start_pass"]).lower(),
            "FAKE_LIVENESS_STATUS": settings["liveness_status"],
            "FAKE_CONTAINER_STATE": settings["container_state"],
            "FAKE_CLEANUP_PASS": str(settings["cleanup_pass"]).lower(),
            "HEXALITH_CONTAINER_SMOKE_TIMEOUT_SECONDS": settings["timeout_value"],
            "HEXALITH_CONTAINER_SMOKE_INTERVAL_SECONDS": "0.05",
        }
    )
    return environment


class SmokeContainerPlatformsTests(unittest.TestCase):
    def run_smoke(self, **overrides):
        settings = {**DEFAULT_SETTINGS, **overrides}
        temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(temporary_directory.cleanup)
        root = Path(temporary_directory.name)
        evidence = root / "evidence"
        evidence.mkdir()
        write_oci_evidence(evidence)
        fake_bin = root / "bin"
        fake_bin.mkdir()
        docker_log = root / "docker.log"
        write_executable(fake_bin / "docker", DOCKER_FIXTURE)
        write_executable(fake_bin / "curl", CURL_FIXTURE)
        environment = smoke_environment(fake_bin, docker_log, settings)
        result = subprocess.run(  # nosec B603 -- repository-owned fixture script and fixed arguments.
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
        self.assertIn("Authentication__JwtBearer__Issuer=hexalith-container-smoke", docker_log)
        self.assertIn("Authentication__JwtBearer__Audience=hexalith-eventstore", docker_log)
        self.assertIn(
            "Authentication__JwtBearer__SigningKey=hexalith-container-smoke-only-key-not-a-secret",
            docker_log,
        )
        self.assertIn("Authentication__JwtBearer__AllowInsecureSymmetricKey=true", docker_log)
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
        result, summary, _, _ = self.run_smoke(liveness_status="503")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("liveness-timeout", summary["result"])

    def test_container_exit_during_liveness_preserves_start_failure_and_diagnostics(self):
        result, summary, _, evidence = self.run_smoke(
            liveness_status="503",
            container_state="exited|17",
        )
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("image-start-failure", summary["result"])
        self.assertEqual("pass", summary["platforms"][0]["cleanup"])
        smoke_log = (evidence / "smoke-linux-amd64.log").read_text(encoding="utf-8")
        self.assertIn("container_state=exited|17", smoke_log)
        self.assertIn("diagnostic_sha256=", smoke_log)
        self.assertIn("diagnostic_excerpt=bounded fixture diagnostic", smoke_log)

    def test_redirect_is_not_accepted_as_liveness(self):
        result, summary, _, _ = self.run_smoke(liveness_status="302")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("liveness-timeout", summary["result"])

    def test_runtime_emulation_failure_is_classified_before_product_smoke(self):
        result, summary, docker_log, _ = self.run_smoke(runtime_emulation_pass=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("environment/emulation-setup-failure", summary["result"])
        self.assertNotIn("--detach", docker_log)

    def test_registry_pull_and_cleanup_failures_are_preserved(self):
        result, summary, _, _ = self.run_smoke(pull_pass=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("registry-pull-failure", summary["result"])

        result, summary, _, _ = self.run_smoke(cleanup_pass=False)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("cleanup-failure", summary["result"])

    def test_non_finite_timing_is_rejected(self):
        for value in ("nan", "inf", "-inf"):
            with self.subTest(value=value):
                result, summary, _, _ = self.run_smoke(timeout_value=value)
                self.assertNotEqual(0, result.returncode)
                self.assertIsNone(summary)
                self.assertIn("must be greater than zero", result.stderr)

    def test_default_timeout_allows_bounded_emulated_startup(self):
        source = (SCRIPT_DIRECTORY / "smoke_container_platforms.py").read_text(encoding="utf-8")
        self.assertIn('DEFAULT_SMOKE_TIMEOUT_SECONDS = "180"', source)


if __name__ == "__main__":
    unittest.main()
