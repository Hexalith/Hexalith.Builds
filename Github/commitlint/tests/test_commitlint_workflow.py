import os
import subprocess  # nosec B404 -- tests execute only a repository-owned workflow block.
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github" / "workflows" / "commitlint.yml"
CALLER = ROOT / ".github" / "workflows" / "commitlint-caller.yml"
RELEASE = ROOT / ".github" / "workflows" / "build-release.yml"
STANDARDS = ROOT / ".github" / "workflows" / "ci-cd-standards.md"


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
    def test_pull_request_title_is_an_explicit_input_transferred_by_environment_and_stdin(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        script = extract_run_block(WORKFLOW, "Validate commit messages")

        self.assertIn("pull-request-title:", workflow)
        self.assertIn("PR_TITLE: ${{ inputs.pull-request-title }}", workflow)
        self.assertIn(
            "printf '%s\\n' \"$PR_TITLE\" | node --input-type=module -e '",
            script,
        )
        self.assertIn("defaultIgnores: false", script)
        self.assertNotIn("${{", script)
        self.assertLess(
            script.index("printf '%s\\n' \"$PR_TITLE\""),
            script.index('npx --no-install --ignore-scripts commitlint --from "$PR_BASE_SHA"'),
        )

    def test_pull_request_title_is_not_evaluated_as_shell_code(self):
        script = extract_run_block(WORKFLOW, "Validate commit messages")
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
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
                cwd=ROOT,
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

    def test_builds_caller_registers_exact_title_check_on_edits(self):
        caller = CALLER.read_text(encoding="utf-8")

        self.assertIn("name: Commitlint", caller)
        self.assertIn("types: [opened, synchronize, reopened, edited]", caller)
        self.assertIn("uses: ./.github/workflows/commitlint.yml", caller)
        self.assertIn("pull-request-title: ${{ github.event.pull_request.title }}", caller)

    def test_release_requires_exact_source_ci_with_locked_commitlint_contracts(self):
        ci = RELEASE.with_name("ci.yml").read_text(encoding="utf-8")
        contracts = ci.split("\n  contracts:\n", 1)[1].split("\n  python-tests:\n", 1)[0]
        setup_index = contracts.index("uses: actions/setup-node@")
        install_index = contracts.index("run: npm ci --ignore-scripts", setup_index)
        audit_index = contracts.index("run: npm audit signatures", install_index)
        contract_index = contracts.index("run: pwsh -NoProfile -File ./Tools/test-commitlint-workflow.ps1", audit_index)
        self.assertLess(setup_index, install_index)
        self.assertLess(install_index, audit_index)
        self.assertLess(audit_index, contract_index)
        self.assertIn("needs: [catalog, contracts, dotnet, python-tests]", ci)
        release = RELEASE.read_text(encoding="utf-8")
        self.assertIn("    needs: verify-source\n", release)
        guard_index = release.index("- name: Recheck approved release source and push CI")
        release_index = release.index("- name: Create Release")
        self.assertLess(guard_index, release_index)
        self.assertIn("run: bash ./Tools/verify-g4-release-source.sh", release[guard_index:release_index])
        self.assertIn("DISPATCH_SHA: ${{ github.sha }}", release[guard_index:release_index])
        self.assertIn("GH_TOKEN: ${{ github.token }}", release[guard_index:release_index])
        self.assertIn("HEXALITH_G4_SOURCE_CI_RUN_ID: ${{ steps.approved_source.outputs.ci-run-id }}", release[release_index:])
        self.assertIn("uses: ./Github/create-release", release[release_index:])
        action = (ROOT / "Github/create-release/action.yml").read_text(encoding="utf-8")
        setup_index = action.index("uses: actions/setup-node@v7.1.0")
        install_index = action.index("run: npm ci --ignore-scripts", setup_index)
        audit_index = action.index("run: npm audit signatures", install_index)
        publish_index = action.index("run: npx --no-install --ignore-scripts semantic-release", audit_index)
        self.assertIn("node-version: '24'", action[setup_index:install_index])
        self.assertLess(setup_index, install_index)
        self.assertLess(install_index, audit_index)
        self.assertLess(audit_index, publish_index)

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
                    cwd=ROOT,
                    env=environment,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(expected, result.returncode == 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
