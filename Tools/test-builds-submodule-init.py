"""Exercise Builds onboarding with ordinary local Git repositories and PowerShell."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest


HELPER = Path(__file__).resolve().with_name("builds-submodule-init.ps1")
SUBMODULE = "references/Hexalith.Builds"


class OnboardingTests(unittest.TestCase):
    def test_pinned_init_retries_dirty_checkout_and_git_failures(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / "global-config"
            config.write_text("[submodule]\n\trecurse = true\n")
            initial_config = config.read_bytes()
            env = dict(os.environ, GIT_ALLOW_PROTOCOL="file", GIT_CONFIG_GLOBAL=str(config),
                       GIT_CONFIG_NOSYSTEM="1", GIT_AUTHOR_NAME="Fixture", GIT_COMMITTER_NAME="Fixture",
                       GIT_AUTHOR_EMAIL="fixture@example.invalid", GIT_COMMITTER_EMAIL="fixture@example.invalid")

            def git(cwd, *args):
                return subprocess.run(["git", *args], cwd=cwd, env=env, capture_output=True,
                                      text=True, check=True).stdout.strip()

            def init(name):
                repo = root / name
                repo.mkdir()
                git(repo, "init", "-q", "-b", "main")
                (repo / "seed").write_text(name)
                git(repo, "add", "seed")
                git(repo, "commit", "-qm", "seed")
                return repo

            def invoke(cwd):
                return subprocess.run(["pwsh", "-NoProfile", "-File", str(HELPER)], cwd=cwd, env=env,
                                      text=True, capture_output=True)

            nested = init("nested")
            builds = init("builds")
            git(builds, "submodule", "add", str(nested), "nested")
            git(builds, "commit", "-qam", "record nested gitlink")
            pinned = git(builds, "rev-parse", "HEAD")
            consumer = init("consumer")
            self.assertNotEqual(0, invoke(consumer).returncode)
            git(consumer, "submodule", "add", str(builds), SUBMODULE)
            git(consumer, "commit", "-qam", "record Builds")
            git(builds, "commit", "-q", "--allow-empty", "-m", "advance main")

            checkout = root / "checkout"
            git(root, "clone", "--no-recurse-submodules", str(consumer), str(checkout))
            for _ in range(2):
                result = invoke(checkout)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(pinned, git(checkout / SUBMODULE, "rev-parse", "HEAD"))
                self.assertFalse((checkout / SUBMODULE / "nested/.git").exists())
            (checkout / SUBMODULE / "seed").write_text("local change")
            self.assertNotEqual(0, invoke(checkout).returncode)
            self.assertEqual("local change", (checkout / SUBMODULE / "seed").read_text())
            broken = root / "broken"
            git(root, "clone", "--no-recurse-submodules", str(consumer), str(broken))
            git(broken, "config", f"submodule.{SUBMODULE}.url", str(root / "absent"))
            self.assertNotEqual(0, invoke(broken).returncode)
            self.assertEqual(initial_config, config.read_bytes())


if __name__ == "__main__":
    unittest.main()
