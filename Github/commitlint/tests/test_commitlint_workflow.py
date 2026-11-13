import json
import os
import subprocess  # nosec B404 -- tests execute only a repository-owned workflow block.
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github" / "workflows" / "commitlint.yml"
STANDARDS = ROOT / ".github" / "workflows" / "ci-cd-standards.md"


def legacy_consumer(root):
    """The frozen workflow runs legacy module policy; Builds uses its guard now."""
    (root / "node_modules").symlink_to(ROOT / "node_modules", target_is_directory=True)
    (root / "package.json").write_text('{"private":true,"type":"module"}\n', encoding="utf-8")
    policy = {"extends": ["@commitlint/config-conventional"], "defaultIgnores": False,
              "rules": {"body-max-line-length": [2, "always", 200], "header-max-length": [2, "always", 200],
                        "type-enum": [2, "always", ["build", "ci", "docs", "feat", "fix", "perf", "refactor", "revert", "style", "test"]]}}
    (root / "commitlint.config.mjs").write_text("export default " + json.dumps(policy) + ";\n", encoding="utf-8")


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


class CommitlintWorkflowTests(unittest.TestCase):

    def test_pull_request_title_is_not_evaluated_as_shell_code(self):
        script = extract_run_block(WORKFLOW, "Validate commit messages")
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            legacy_consumer(root)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            marker = root / "injected"
            stdin_log = root / "stdin.log"
            stdin_log.touch()
            args_log = root / "args.log"
            npx = fake_bin / "npx"
            npx.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "printf '%s\\n' \"$*\" >> \"$ARGS_LOG\"\n"
                "if [ ! -t 0 ]; then cat >> \"$STDIN_LOG\"; fi\n",
                encoding="utf-8",
            )
            npx.chmod(0o755)
            title = f"fix: preserve $(touch {marker}) literally"
            environment = os.environ.copy()
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "EVENT_NAME": "pull_request",
                    "PR_TITLE": title,
                    "PR_BASE_SHA": "a" * 40,
                    "PR_HEAD_SHA": "b" * 40,
                    "PUSH_BEFORE_SHA": "",
                    "PUSH_AFTER_SHA": "",
                    "ARGS_LOG": str(args_log),
                    "STDIN_LOG": str(stdin_log),
                }
            )

            result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned fixture script.
                ["bash", "-c", script],
                cwd=root,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertFalse(marker.exists())
            invocations = args_log.read_text(encoding="utf-8").splitlines()
            self.assertEqual(1, len(invocations))
            self.assertIn("commitlint --from", invocations[0])
            self.assertFalse(stdin_log.read_text(encoding="utf-8"))

    def test_shared_guidance_requires_title_edit_events_and_exact_title_input(self):
        standards = STANDARDS.read_text(encoding="utf-8")

        self.assertIn("types: [opened, synchronize, reopened, edited]", standards)
        self.assertIn("pull-request-title: ${{ github.event.pull_request.title }}", standards)


    def test_pinned_commitlint_rejects_malformed_and_default_ignored_titles(self):
        cases = {
            "fix: validate intentional releases": True,
            "not conventional": False,
            "Merge branch main": False,
            "1.2.3": False,
            "chore: hide release policy": False,
            "fix: Uppercase subject": False,
            "fix: " + ("x" * 195): True,
            "fix: " + ("x" * 196): False,
        }
        for title, expected in cases.items():
            with self.subTest(title=title), tempfile.TemporaryDirectory() as temporary_directory:
                root = Path(temporary_directory)
                legacy_consumer(root)
                fake_bin = root / "bin"
                fake_bin.mkdir()
                npx = fake_bin / "npx"
                npx.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
                npx.chmod(0o755)
                environment = os.environ.copy()
                environment.update(
                    {
                        "PATH": f"{fake_bin}:{environment['PATH']}",
                        "EVENT_NAME": "pull_request",
                        "PR_TITLE": title,
                        "PR_BASE_SHA": "a" * 40,
                        "PR_HEAD_SHA": "b" * 40,
                        "PUSH_BEFORE_SHA": "",
                        "PUSH_AFTER_SHA": "",
                    }
                )
                result = subprocess.run(  # nosec B603  # NOSONAR -- repository-owned workflow script.
                    ["bash", "-c", extract_run_block(WORKFLOW, "Validate commit messages")],
                    cwd=root,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(expected, result.returncode == 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
