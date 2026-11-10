"""Exercise frozen release selection with disposable Git histories and CI clocks."""

from __future__ import annotations

import copy
import http.server
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from contextlib import contextmanager, redirect_stdout
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ci_config import ConfigError
import release_state as state


REPOSITORY = "Example/ReleaseFixture"
BUILDS = "Hexalith/Hexalith.Builds"


def configuration(*, codeql=None, nightly=False):
    return {"schema": "hexalith.ci.v1", "buildsTag": "v1.0.0", "solution": "Fixture.slnx",
            "dapr": False, "browsers": False, "registry": False,
            "codeql": ["csharp"] if codeql is None else codeql,
            "tests": {"integration": [], "exclude": {}},
            "hooks": ["nightly"] if nightly else [], "jobs": {}, "submodules": []}


class Clock:
    def __init__(self):
        self.now = 0
        self.sleeps = []

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.now += seconds


class ApiControl:
    def __init__(self, *, repository=REPOSITORY, drafts=(), runs=()):
        self.repository = repository
        self.drafts = list(drafts)
        self.runs = list(runs)
        self.requests = []
        self.ci_reads = 0

    def pages(self, path, key=None, deadline=None):
        self.requests.append((path, key, deadline))
        if "/releases?" in path:
            yield from self.drafts
        else:
            value = self.runs[min(self.ci_reads, len(self.runs) - 1)] if self.runs else []
            self.ci_reads += 1
            if isinstance(value, Exception):
                raise value
            yield from value


def ci_run(sha, *, identifier=10, status="completed", conclusion="success", event="push"):
    return {"id": identifier, "head_sha": sha, "event": event, "status": status, "conclusion": conclusion}


class ApiDeadlineTests(unittest.TestCase):
    @contextmanager
    def streaming_http(self, phase):
        release, started, finished = threading.Event(), threading.Event(), threading.Event()

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_args):
                pass

            def do_GET(self):
                started.set()
                try:
                    if phase == "headers":
                        self.wfile.write(b"HTTP/1.1 200 OK\r\n")
                    else:
                        self.send_response(200)
                        self.end_headers()
                        self.wfile.write(b"[")
                    self.wfile.flush()
                    limit = time.monotonic() + 3
                    while time.monotonic() < limit and not release.wait(0.02):
                        self.wfile.write(b"X-Progress: x\r\n" if phase == "headers" else b" ")
                        self.wfile.flush()
                    self.wfile.write(b"Content-Length: 2\r\n\r\n[]" if phase == "headers" else b"]")
                    self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    pass
                finally:
                    finished.set()

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        serving = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        serving.start()
        try:
            yield state.Api(REPOSITORY, "deadline-token", f"http://127.0.0.1:{server.server_port}"), started, finished
        finally:
            release.set()
            server.shutdown()
            server.server_close()
            serving.join()
            self.assertTrue(finished.wait(1))

    def assert_deadline_returns_before_worker(self, operation, finished):
        start = time.monotonic()
        with self.assertRaisesRegex(state.ReleaseError, "deadline exceeded"):
            operation(start + 0.2)
        self.assertLess(time.monotonic() - start, 0.7)
        self.assertFalse(finished.is_set(), "timeout must not wait for the unfinished worker")

    def test_absolute_deadline_bounds_a_steadily_streaming_json_body(self):
        with self.streaming_http("body") as (api, started, finished):
            self.assert_deadline_returns_before_worker(
                lambda deadline: api.get(f"/repos/{REPOSITORY}/actions/workflows/ci.yml/runs", deadline=deadline), finished)
            self.assertTrue(started.is_set())

    def test_absolute_deadline_bounds_steadily_streaming_response_headers(self):
        with self.streaming_http("headers") as (api, started, finished):
            self.assert_deadline_returns_before_worker(
                lambda deadline: api.get(f"/repos/{REPOSITORY}/releases", deadline=deadline), finished)
            self.assertTrue(started.is_set())

    def test_absolute_deadline_does_not_join_slow_response_acquisition(self):
        release, finished = threading.Event(), threading.Event()
        api = state.Api(REPOSITORY, "deadline-token")

        def slow_open(*_args, **_kwargs):
            try:
                release.wait(3)
                response = io.BytesIO(b"[]")
                response.headers = {}
                return response
            finally:
                finished.set()

        try:
            with patch.object(api.opener, "open", side_effect=slow_open):
                self.assert_deadline_returns_before_worker(
                    lambda deadline: api.get(f"/repos/{REPOSITORY}/releases", deadline=deadline), finished)
        finally:
            release.set()
            self.assertTrue(finished.wait(1))


class ReleaseStateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.command("init", "--initial-branch=main")
        self.command("config", "user.name", "Release Test")
        self.command("config", "user.email", "release-test@example.invalid")
        self.first = self.commit(configuration(codeql=["python"]), "first")
        self.command("tag", "-a", "v1.0.0", "-m", "stable baseline", self.first)
        self.dispatch = self.commit(configuration(nightly=True), "dispatch")
        self.git = state.Git(self.root)

    def command(self, *arguments, input_text=None):
        environment = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull,
                           GIT_TERMINAL_PROMPT="0")
        result = subprocess.run(["git", "-c", "core.hooksPath=" + os.devnull, "-C", str(self.root), *arguments],
                                input=input_text, env=environment, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        return result.stdout.strip()

    def commit(self, document, label):
        path = self.root / ".github/hexalith-ci.json"
        path.parent.mkdir(exist_ok=True)
        path.write_text(json.dumps(document))
        (self.root / "source.txt").write_text(label)
        self.command("add", ".github/hexalith-ci.json", "source.txt")
        self.command("commit", "-m", "test: create disposable release fixture")
        return self.command("rev-parse", "HEAD")

    def draft(self, *, sha=None, tag="v1.1.0", identifier=11, author="github-actions[bot]", **changes):
        sha = sha or self.first
        document = {"id": identifier, "draft": True, "prerelease": False,
                    "author": {"login": author}, "target_commitish": sha,
                    "tag_name": tag, "body": "Release notes\n\nhexalith-release: " + sha + "\n"}
        return {**document, **changes}

    def resolve(self, api, **options):
        emitted = {}
        options = {"sha": self.dispatch, "ref": "refs/heads/main", "switch": "false", **options}
        result = state.resolve(self.git, api, emit=emitted.update, **options)
        return result, emitted

    def test_highest_stable_tag_uses_numeric_order_ancestry_annotations_and_exclusion(self):
        self.command("tag", "v1.2.0", self.dispatch)
        self.command("tag", "-a", "v1.10.0", "-m", "annotated stable", self.dispatch)
        self.command("tag", "v2.0.0-rc.1", self.dispatch)
        self.command("tag", "v01.20.0", self.dispatch)
        tree = self.command("rev-parse", "HEAD^{tree}")
        unmerged = self.command("commit-tree", tree, "-m", "test: create unrelated history")
        self.command("tag", "v99.0.0", unmerged)
        self.assertEqual("v1.10.0", state.highest_reachable_stable_tag(self.git, self.dispatch))
        self.assertEqual("v1.2.0", state.highest_reachable_stable_tag(self.git, self.dispatch, exclude="v1.10.0"))
        self.assertEqual("v1.0.0", state.highest_reachable_stable_tag(self.git, self.first))
        with self.assertRaisesRegex(state.ReleaseError, "full source"):
            state.highest_reachable_stable_tag(self.git, "HEAD")

    def test_no_reachable_tag_accepts_only_bootstrap_one_zero_zero(self):
        self.command("tag", "-d", "v1.0.0")
        self.assertEqual("", state.highest_reachable_stable_tag(self.git, self.first))
        selected = state.select_release(self.git, [self.draft(tag="v1.0.0")], self.dispatch)
        self.assertEqual("1.0.0", selected["version"])
        self.assertEqual("", selected["last-tag"])
        for tag in ("v0.1.0", "v0.0.0", "v1.0.1", "v2.0.0"):
            with self.subTest(tag=tag), self.assertRaisesRegex(state.ReleaseError, "draft 11"):
                state.select_release(self.git, [self.draft(tag=tag)], self.dispatch)

    def test_complete_draft_excludes_its_own_existing_tag(self):
        self.command("tag", "v1.1.0", self.first)
        selected = state.select_release(self.git, [self.draft()], self.dispatch)
        self.assertEqual({"sha": self.first, "mode": "complete", "version": "1.1.0",
                          "draft-id": "11", "last-tag": "v1.0.0"}, selected)

    def test_unmarked_and_nonbot_drafts_cannot_select_or_mutate_releases(self):
        drafts = [self.draft(author="human"), self.draft(body="ordinary hand-authored draft"),
                  self.draft(draft=False), self.draft(author="human", body="hexalith-release: invalid")]
        original = copy.deepcopy(drafts)
        self.assertEqual({"sha": self.dispatch, "mode": "create"},
                         state.select_release(self.git, drafts, self.dispatch))
        self.assertEqual(original, drafts)

    def test_malformed_bot_drafts_are_named_and_all_remain_unchanged(self):
        invalid = [{"body": "hexalith-release: invalid"}, {"target_commitish": "main"},
                   {"body": "hexalith-release: " + self.dispatch},
                   {"body": "hexalith-release: " + self.first + "\nhexalith-release: " + self.first},
                   {"tag_name": "v0.9.0"}, {"tag_name": "v1.2.0-beta.1"},
                   {"tag_name": "v01.1.0"}, {"prerelease": True}, {"id": True}]
        tree = self.command("rev-parse", "HEAD^{tree}")
        unmerged = self.command("commit-tree", tree, "-m", "test: create unrelated draft history")
        invalid.append({"target_commitish": unmerged, "body": "hexalith-release: " + unmerged})
        for mutation in invalid:
            with self.subTest(mutation=mutation):
                draft = self.draft(**mutation)
                original = copy.deepcopy(draft)
                with self.assertRaisesRegex(state.ReleaseError, "invalid marked draft"):
                    state.select_release(self.git, [draft, self.draft(identifier=12)], self.dispatch)
                self.assertEqual(original, draft)

    def test_multiple_valid_drafts_fail_naming_both_identities(self):
        with self.assertRaisesRegex(state.ReleaseError, "11, 12"):
            state.select_release(self.git, [self.draft(), self.draft(identifier=12, tag="v1.2.0")], self.dispatch)

    def test_complete_uses_dispatch_configuration_and_never_checks_old_sha_ci(self):
        api = ApiControl(drafts=[self.draft()])
        selected, emitted = self.resolve(api, switch="true")
        self.assertEqual(self.first, selected["sha"])
        self.assertEqual('["csharp"]', emitted["codeql"])
        self.assertEqual("true", emitted["nightly-hook"])
        self.assertEqual("true", emitted["config-valid"])
        self.assertEqual(0, api.ci_reads)
        # A later push does not change the frozen dispatch SHA or its configuration.
        self.commit(configuration(codeql=["ruby"]), "later push")
        selected, emitted = self.resolve(api, switch="true")
        self.assertEqual(self.first, selected["sha"])
        self.assertEqual('["csharp"]', emitted["codeql"])

    def test_disabled_and_invalid_switches_retain_settings_and_selected_identity(self):
        api = ApiControl()
        with redirect_stdout(io.StringIO()) as logs:
            selected, emitted = self.resolve(api)
        self.assertIn("publishing disabled", logs.getvalue())
        self.assertEqual(self.dispatch, selected["sha"])
        self.assertEqual("false", emitted["enabled"])
        self.assertEqual(0, api.ci_reads)
        for switch in ("", "TRUE", "False", " true", "false\n", "1"):
            with self.subTest(switch=switch):
                emitted = {}
                with self.assertRaisesRegex(state.ReleaseError, "must be exactly true or false"):
                    state.resolve(self.git, api, sha=self.dispatch, ref="refs/heads/main", switch=switch,
                                  emit=emitted.update)
                self.assertEqual(self.dispatch, emitted["sha"])
                self.assertEqual('["csharp"]', emitted["codeql"])
                self.assertEqual("true", emitted["nightly-hook"])
                self.assertNotIn("enabled", emitted)

    def test_wrong_ref_and_ambiguous_drafts_leave_current_settings_available(self):
        for options, api in [({"ref": "refs/heads/feature"}, ApiControl()),
                             ({}, ApiControl(drafts=[self.draft(), self.draft(identifier=12)]))]:
            with self.subTest(options=options):
                emitted = {}
                with self.assertRaises(state.ReleaseError):
                    state.resolve(self.git, api, sha=self.dispatch, ref=options.get("ref", "refs/heads/main"),
                                  switch="false", emit=emitted.update)
                self.assertEqual("true", emitted["config-valid"])
                self.assertNotIn("sha", emitted)

    def test_invalid_dispatch_configuration_emits_no_fabricated_gate_settings(self):
        invalid = configuration()
        del invalid["hooks"]
        sha = self.commit(invalid, "bad configuration")
        emitted = {}
        with self.assertRaises(ConfigError):
            state.resolve(self.git, ApiControl(), sha=sha, ref="refs/heads/main", switch="false", emit=emitted.update)
        self.assertEqual({}, emitted)

    def test_git_object_reads_ignore_worktree_filters_hooks_and_submodules(self):
        marker = self.root / "executed-module-code"
        poison = self.root / "poison.sh"
        poison.write_text("#!/bin/sh\ntouch " + str(marker) + "\n")
        poison.chmod(0o755)
        self.command("config", "core.hooksPath", str(self.root))
        self.command("config", "filter.release-test.smudge", str(poison))
        (self.root / ".gitattributes").write_text("* filter=release-test\n")
        (self.root / ".github/hexalith-ci.json").write_text("malformed working copy")
        (self.root / ".gitmodules").write_text('[submodule "poison"]\npath=poison\nurl=ext::' + str(poison) + '\n')
        result = state.dispatch_settings(self.git, self.dispatch, REPOSITORY)
        self.assertEqual('["csharp"]', result["codeql"])
        self.assertFalse(marker.exists())

    def test_fixture_and_workflow_overrides_are_restricted(self):
        for repository, workflow in [(REPOSITORY, "builds-ci.yml"), (REPOSITORY, "arbitrary.yml"),
                                     (BUILDS, "arbitrary.yml"), (BUILDS, "../ci.yml")]:
            with self.subTest(repository=repository, workflow=workflow), self.assertRaises(state.ReleaseError):
                state.ci_workflow(repository, workflow)
        self.assertEqual("ci.yml", state.ci_workflow(REPOSITORY, "ci.yml"))
        self.assertEqual("builds-ci.yml", state.ci_workflow(BUILDS, "builds-ci.yml"))
        for repository, module_root in [(REPOSITORY, "Samples/Hexalith.Sample"),
                                        (BUILDS, "../other"), (BUILDS, "Samples/Other")]:
            with self.subTest(repository=repository, module_root=module_root), self.assertRaises(state.ReleaseError):
                state.dispatch_settings(self.git, self.dispatch, repository, module_root)

    def test_latest_exact_sha_push_or_manual_ci_governs(self):
        for event in ("push", "workflow_dispatch"):
            api = ApiControl(runs=[[ci_run(self.dispatch, identifier=1, conclusion="failure"),
                                    ci_run(self.dispatch, identifier=2, event=event),
                                    ci_run(self.first, identifier=99, conclusion="failure"),
                                    ci_run(self.dispatch, identifier=100, conclusion="failure", event="pull_request")]])
            state.require_green_ci(api, self.dispatch, "ci.yml")
            self.assertIn("/actions/workflows/ci.yml/runs?head_sha=" + self.dispatch, api.requests[0][0])
        api = ApiControl(runs=[[ci_run(self.dispatch, identifier=1),
                                ci_run(self.dispatch, identifier=2, conclusion="failure")]])
        with self.assertRaisesRegex(state.ReleaseError, "main is red, nothing released"):
            state.require_green_ci(api, self.dispatch, "ci.yml")

    def test_no_ci_and_each_non_success_conclusion_fail_without_waiting(self):
        for runs in ([], [ci_run(self.first)], [ci_run(self.dispatch, event="pull_request")],
                     *[[ci_run(self.dispatch, conclusion=conclusion)] for conclusion in
                       ("failure", "cancelled", "neutral", "skipped", "timed_out", "action_required", None)]):
            with self.subTest(runs=runs):
                clock = Clock()
                with self.assertRaisesRegex(state.ReleaseError, "main is red, nothing released"):
                    state.require_green_ci(ApiControl(runs=[runs]), self.dispatch, "ci.yml",
                                           clock=clock, sleeper=clock.sleep)
                self.assertEqual([], clock.sleeps)

    def test_unfinished_ci_waits_once_per_minute_until_success_or_fixed_deadline(self):
        clock = Clock()
        api = ApiControl(runs=[[ci_run(self.dispatch, status="in_progress", conclusion=None)],
                               [ci_run(self.dispatch, status="queued", conclusion=None)], [ci_run(self.dispatch)]])
        state.require_green_ci(api, self.dispatch, "ci.yml", clock=clock, sleeper=clock.sleep)
        self.assertEqual([60, 60], clock.sleeps)
        self.assertEqual([1800] * 3, [deadline for _path, _key, deadline in api.requests])
        clock = Clock()
        api = ApiControl(runs=[[ci_run(self.dispatch, status="in_progress", conclusion=None)]])
        with self.assertRaisesRegex(state.ReleaseError, "main is red, nothing released.*30 minutes"):
            state.require_green_ci(api, self.dispatch, "ci.yml", clock=clock, sleeper=clock.sleep)
        self.assertEqual(1800, clock.now)
        self.assertEqual([60] * 30, clock.sleeps)
        self.assertEqual(30, api.ci_reads)
        self.assertEqual({1800}, {deadline for _path, _key, deadline in api.requests})

    def test_api_time_counts_toward_fixed_deadline_even_when_the_run_is_green(self):
        class TimedApi(ApiControl):
            def pages(self, path, key=None, deadline=None):
                clock.now += elapsed
                yield from super().pages(path, key=key, deadline=deadline)

        for elapsed in (1799, 1800, 1801):
            with self.subTest(api_elapsed=elapsed):
                clock = Clock()
                api = TimedApi(runs=[[ci_run(self.dispatch)]])
                if elapsed < 1800:
                    state.require_green_ci(api, self.dispatch, "ci.yml", clock=clock, sleeper=clock.sleep)
                else:
                    with self.assertRaisesRegex(state.ReleaseError, "main is red, nothing released.*30 minutes"):
                        state.require_green_ci(api, self.dispatch, "ci.yml", clock=clock, sleeper=clock.sleep)
                self.assertEqual([], clock.sleeps)
                self.assertEqual([1800], [deadline for _path, _key, deadline in api.requests])

        elapsed = 1790
        clock = Clock()
        api = TimedApi(runs=[[ci_run(self.dispatch, status="in_progress", conclusion=None)]])
        with self.assertRaisesRegex(state.ReleaseError, "main is red, nothing released.*30 minutes"):
            state.require_green_ci(api, self.dispatch, "ci.yml", clock=clock, sleeper=clock.sleep)
        self.assertEqual(1800, clock.now)
        self.assertEqual([10], clock.sleeps, "API time must shorten the final polling sleep")

    def test_ci_api_errors_unknown_status_and_invalid_identity_fail_visibly(self):
        for runs, diagnostic in [(state.ReleaseError("controlled API failure"), "controlled API failure"),
                                 ([ci_run(self.dispatch, status="mystery")], "unknown status"),
                                 ([ci_run(self.dispatch, identifier=True)], "invalid identity")]:
            with self.subTest(runs=runs), self.assertRaisesRegex(state.ReleaseError, diagnostic):
                state.require_green_ci(ApiControl(runs=[runs]), self.dispatch, "ci.yml")

    def test_selected_sha_and_settings_survive_red_ci(self):
        emitted = {}
        api = ApiControl(runs=[[ci_run(self.dispatch, conclusion="failure")]])
        with self.assertRaisesRegex(state.ReleaseError, "main is red, nothing released"):
            state.resolve(self.git, api, sha=self.dispatch, ref="refs/heads/main", switch="true", emit=emitted.update)
        self.assertEqual(self.dispatch, emitted["sha"])
        self.assertEqual("create", emitted["mode"])
        self.assertEqual("true", emitted["enabled"])
        self.assertEqual("true", emitted["nightly-hook"])


if __name__ == "__main__":
    unittest.main()
