import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ACTION_ROOT = Path(__file__).resolve().parents[1]
WRAPPER = ACTION_ROOT / "git-with-retry.sh"
NOTES = ["push", "--", "https://github.com/Hexalith/Hexalith.Builds.git", "refs/notes/semantic-release-v4.46.1"]


class GitNotesRetryTests(unittest.TestCase):
    def invoke(self, arguments, errors, statuses=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state = root / "state.json"
            state.write_text(json.dumps({"errors": errors, "statuses": statuses or [], "calls": []}))
            fake_git = root / "original-git"
            fake_git.write_text(
                "#!/usr/bin/env python3\n"
                "import json,os,sys\n"
                "from pathlib import Path\n"
                "p=Path(os.environ['RETRY_STATE'])\n"
                "s=json.loads(p.read_text())\n"
                "i=len(s['calls'])\n"
                "s['calls'].append(sys.argv[1:])\n"
                "p.write_text(json.dumps(s))\n"
                "print('git stdout')\n"
                "error=s['errors'][min(i,len(s['errors'])-1)]\n"
                "print(error,file=sys.stderr)\n"
                "codes=s['statuses']\n"
                "sys.exit(codes[min(i,len(codes)-1)] if codes else bool(error))\n"
            )
            fake_git.chmod(0o755)
            sleep_log = root / "sleep.log"
            fake_sleep = root / "sleep"
            fake_sleep.write_text('#!/usr/bin/env bash\nprintf "%s\\n" "$*" >> "$SLEEP_LOG"\n')
            fake_sleep.chmod(0o755)
            environment = os.environ.copy()
            environment.update({
                "HEXALITH_RELEASE_GIT": str(fake_git),
                "RETRY_STATE": str(state),
                "SLEEP_LOG": str(sleep_log),
                "PATH": str(root) + os.pathsep + environment["PATH"],
                "TMPDIR": str(root),
            })
            result = subprocess.run(
                ["bash", str(WRAPPER), *arguments], env=environment,
                capture_output=True, text=True, check=False,
            )
            calls = json.loads(state.read_text())["calls"]
            sleeps = sleep_log.read_text().splitlines() if sleep_log.exists() else []
            remaining_temporary_directories = [p for p in root.iterdir() if p.is_dir()]
            self.assertEqual([], remaining_temporary_directories, "Captured Git output must be removed")
            return result, calls, sleeps

    def test_notes_push_retries_transient_server_failures_and_preserves_arguments(self):
        for error in [
            "remote: Internal Server Error        ",
            "remote: Bad Gateway",
            "remote: Service Unavailable",
            "remote: Gateway Timeout",
            "error: RPC failed; HTTP 502 curl 22",
            "fatal: unable to access 'https://github.com/example': The requested URL returned error: 503",
        ]:
            with self.subTest(error=error):
                result, calls, sleeps = self.invoke(NOTES, [error, error, ""])
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([NOTES] * 3, calls)
                self.assertEqual(["2", "4"], sleeps)
                self.assertEqual("", result.stdout)

    def test_retry_limit_retains_failure_exit_code(self):
        result, calls, sleeps = self.invoke(NOTES, ["remote: Internal Server Error"], [73])
        self.assertEqual(73, result.returncode)
        self.assertEqual([NOTES] * 3, calls)
        self.assertEqual(["2", "4"], sleeps)

    def test_permanent_refusal_authentication_and_client_failures_do_not_retry(self):
        for error in [
            "remote: Permission denied", "fatal: Authentication failed",
            "error: failed to push some refs", "remote: non-fast-forward",
            "fatal: unable to access repository: HTTP 403", "remote: HTTP 501",
        ]:
            with self.subTest(error=error):
                result, calls, sleeps = self.invoke(NOTES, [error], [19])
                self.assertEqual(19, result.returncode)
                self.assertEqual([NOTES], calls)
                self.assertEqual([], sleeps)

    def test_all_other_git_invocations_delegate_once_even_for_server_failures(self):
        for arguments in [
            ["push", "--tags", "--", NOTES[2]],
            ["push", "--", NOTES[2], "HEAD:main"],
            ["push", "--", NOTES[2], "refs/notes/other"],
            ["push", "--", NOTES[2], NOTES[3] + ":refs/heads/main"],
            ["push", "--", NOTES[2], NOTES[3] + ":refs/notes/other"],
            ["push", "--", NOTES[2], "+" + NOTES[3]],
            ["push", "--force", "--", NOTES[2], NOTES[3]],
            ["push", "--dry-run", "--", NOTES[2], NOTES[3]],
            ["push", "--", "https://example.com/repo.git", NOTES[3]],
            ["push", "--", "https://github.com.example.com/repo.git", NOTES[3]],
            ["fetch", NOTES[2]], ["status", "--short"], [],
        ]:
            with self.subTest(arguments=arguments):
                result, calls, sleeps = self.invoke(arguments, ["remote: Internal Server Error"], [23])
                self.assertEqual(23, result.returncode)
                self.assertEqual([arguments], calls)
                self.assertEqual([], sleeps)

    def test_successful_notes_push_runs_once(self):
        result, calls, sleeps = self.invoke(NOTES, [""])
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([NOTES], calls)
        self.assertEqual([], sleeps)

    def test_authenticated_remote_and_transport_diagnostics_are_never_echoed(self):
        arguments = [*NOTES]
        arguments[2] = "https://x-access-token:fixture-private-token@github.com/Hexalith/Hexalith.Builds.git"
        error = "fatal: unable to access '" + arguments[2] + "': The requested URL returned error: 503"
        result, calls, sleeps = self.invoke(arguments, [error], [71])
        self.assertEqual(71, result.returncode)
        self.assertEqual([arguments] * 3, calls)
        self.assertEqual(["2", "4"], sleeps)
        self.assertNotIn("fixture-private-token", result.stdout + result.stderr)
        self.assertNotIn(arguments[2], result.stdout + result.stderr)

    def test_actual_action_invokes_lifecycle_once_and_removes_private_proxy(self):
        action = (ACTION_ROOT / "action.yml").read_text()
        block = action.split("      run: |\n", 1)[1]
        script = "\n".join(line[8:] for line in block.splitlines()) + "\n"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            binaries = root / "bin"
            binaries.mkdir()
            events = root / "events"
            original_git = binaries / "git"
            original_git.write_text(
                '#!/usr/bin/env bash\n'
                'printf "git %s\\n" "$*" >> "$RETRY_EVENTS"\n'
                'if [[ $4 == refs/notes/semantic-release-* && ! -f "$RETRY_MARKER" ]]; then\n'
                '  touch "$RETRY_MARKER"\n'
                '  printf "remote: Internal Server Error\\n" >&2\n'
                '  exit 1\n'
                'fi\n'
            )
            original_git.chmod(0o755)
            npx = binaries / "npx"
            npx.write_text(
                '#!/usr/bin/env bash\nset -euo pipefail\n'
                'printf "npx %s\\n" "$*" >> "$RETRY_EVENTS"\n'
                'git push -- https://github.com/Hexalith/Hexalith.Builds.git refs/notes/semantic-release-v4.46.1\n'
                'exit "$LIFECYCLE_STATUS"\n'
            )
            npx.chmod(0o755)
            sleep = binaries / "sleep"
            sleep.write_text('#!/usr/bin/env bash\nexit 0\n')
            sleep.chmod(0o755)
            temporary = root / "temporary"
            temporary.mkdir()
            for status in [0, 17]:
                with self.subTest(status=status):
                    events.write_text("")
                    marker = root / "marker"
                    marker.unlink(missing_ok=True)
                    environment = os.environ.copy()
                    environment.update({
                        "PATH": str(binaries) + os.pathsep + environment["PATH"],
                        "GITHUB_ACTION_PATH": str(ACTION_ROOT),
                        "RETRY_EVENTS": str(events), "RETRY_MARKER": str(marker),
                        "TMPDIR": str(temporary), "LIFECYCLE_STATUS": str(status),
                    })
                    result = subprocess.run(
                        ["bash", "-e", "-o", "pipefail", "-c", script], env=environment,
                        capture_output=True, text=True, check=False,
                    )
                    self.assertEqual(status, result.returncode, result.stderr)
                    calls = events.read_text().splitlines()
                    self.assertEqual(1, sum(line.startswith("npx ") for line in calls))
                    self.assertEqual(2, sum(line.startswith("git ") for line in calls))
                    self.assertEqual([], list(temporary.iterdir()))


if __name__ == "__main__":
    unittest.main()
