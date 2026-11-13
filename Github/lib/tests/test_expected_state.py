"""Exercise accepted-state acquisition/comparison with disposable Git histories."""

from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import expected_state as state

REAL_GIT = shutil.which("git")
REPOSITORY = "Example/Fixture"
BUILDS = "Hexalith/Hexalith.Builds"


def configuration(**changes):
    return {"schema": "hexalith.ci.v1", "buildsTag": "v1.2.3", "solution": "Fixture.slnx",
            "dapr": False, "browsers": False, "registry": False, "codeql": [],
            "tests": {"integration": [], "exclude": {}}, "hooks": [], "jobs": {},
            "submodules": [], **changes}


class Repository:
    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir()
        self.command("init", "-q", "-b", "main")
        self.command("config", "user.name", "Fixture")
        self.command("config", "user.email", "fixture@example.invalid")

    def command(self, *args, data=None):
        result = subprocess.run([REAL_GIT, "-c", "core.hooksPath=" + os.devnull,
            "-c", "protocol.file.allow=always", "-C", str(self.root), *args],
            input=data, capture_output=True, env=state.Git(self.root).environment)
        if result.returncode:
            raise AssertionError((args, result.stderr))
        return result.stdout.decode().strip()

    def write(self, path, value):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(value if isinstance(value, bytes) else value.encode())

    def commit(self):
        self.command("add", "--all")
        self.command("commit", "-qm", "test: create disposable fixture", "--allow-empty")
        return self.command("rev-parse", "HEAD")


class LocalTransport(state.AnonymousTransport):
    """An explicit Python-only harness; production has no remote override."""

    def __init__(self, delivered, builds):
        self.delivered, self.builds_source = delivered, builds
        self.calls = []

    def command(self, root, *args):
        result = subprocess.run([REAL_GIT, "-c", "core.hooksPath=" + os.devnull,
            "-c", "protocol.file.allow=always", "-C", str(root), *args],
            env=state.Git(root).environment, capture_output=True)
        if result.returncode:
            raise state.ExpectedStateError("test anonymous transport failed: " + args[0])
        return result.stdout

    def clone(self, directory, branch):
        self.calls.append(("clone", branch))
        self.command(directory.parent, "clone", "--depth=1", "--sparse", "--no-checkout", "--no-tags",
                     "--single-branch", "--branch", branch, self.delivered.root.as_uri(), str(directory))
        return state.Git(directory)

    def deepen(self, git, branch):
        self.calls.append(("deepen", branch))
        self.command(git.root, "fetch", "--unshallow", "--no-tags", self.delivered.root.as_uri(),
                     "+refs/heads/" + branch + ":refs/remotes/origin/" + branch)

    def builds(self, directory, tags):
        self.calls.append(("builds", tags))
        directory.mkdir()
        self.command(directory, "init", "--bare", "--template=")
        self.command(directory, "fetch", "--depth=1", self.builds_source.root.as_uri(),
                     *("+refs/tags/" + tag + ":refs/tags/" + tag for tag in tags))
        return state.Git(directory)


class ExpectedStateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.module = Repository(self.root / "module")
        self.delivered = Repository(self.root / "delivered")
        self.builds = Repository(self.root / "builds")
        self.builds.write("source", "trusted Builds tag\n")
        self.builds_sha = self.builds.commit()
        self.builds.command("tag", "-a", "v1.2.3", "-m", "test annotated tag")
        self.document = {"schema": "hexalith.perimeter.v1", "entries": [
            {"kind": "whole", "path": ".github/hexalith-ci.json"}]}
        data = json.dumps(configuration())
        self.module.write(".github/hexalith-ci.json", data)
        self.module_sha = self.module.commit()
        self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", data)
        self.accepted = self.publish()
        self.transport = LocalTransport(self.delivered, self.builds)

    def publish(self):
        self.delivered.write("rendered/Fixture.perimeter.json", json.dumps(self.document))
        commit = self.delivered.commit()
        self.delivered.command("branch", "-f", "delivered/Fixture", commit)
        return commit

    def compare(self, **changes):
        return state.compare_repository(state.Git(self.module.root), repository=REPOSITORY,
                                        transport=self.transport, **changes)

    def test_pinned_head_ignores_dirty_staged_and_sparse_worktree_content(self):
        self.module.write(".github/hexalith-ci.json", "staged invalid config")
        self.module.command("add", ".github/hexalith-ci.json")
        self.module.write(".github/hexalith-ci.json", "dirty invalid config")
        before = (self.module.root / ".git/index").read_bytes()
        comparison = self.compare()
        self.assertEqual((), comparison.drift)
        self.assertEqual(self.accepted, comparison.delivered_sha)
        self.assertEqual(before, (self.module.root / ".git/index").read_bytes())
        self.assertEqual("dirty invalid config", (self.module.root / ".github/hexalith-ci.json").read_text())
        self.module.command("reset", "--hard", self.module_sha)
        self.module.command("sparse-checkout", "set", "--no-cone", "/nonexistent")
        self.assertFalse((self.module.root / ".github/hexalith-ci.json").exists())
        before = (self.module.root / ".git/index").read_bytes()
        self.assertEqual((), self.compare().drift)
        self.assertEqual(before, (self.module.root / ".git/index").read_bytes())

    def test_exact_old_delivered_commit_requires_branch_history_and_never_falls_back(self):
        old = self.accepted
        for label in ("second", "third"):
            self.delivered.write("unowned", label)
            self.publish()
        comparison = self.compare(delivered_sha=old)
        self.assertEqual(old, comparison.delivered_sha)
        self.assertEqual((), comparison.drift)
        self.assertEqual([("clone", "delivered/Fixture"), ("deepen", "delivered/Fixture")], self.transport.calls)
        with self.assertRaisesRegex(state.ExpectedStateError, "unavailable"):
            self.compare(delivered_sha="f" * 40)
        with self.assertRaisesRegex(state.ExpectedStateError, "full commit SHA"):
            self.compare(delivered_sha="HEAD")

    def test_filtered_transport_lazily_reads_owned_blobs_and_deepens_without_historical_blobs(self):
        self.delivered.command("config", "uploadpack.allowFilter", "true")
        self.delivered.command("config", "uploadpack.allowAnySHA1InWant", "true")
        self.delivered.write("unrelated/historical.bin", "unrelated historical payload")
        historical = self.publish()
        unrelated = self.delivered.command("rev-parse", historical + ":unrelated/historical.bin")
        (self.delivered.root / "unrelated/historical.bin").unlink()
        ancestor = self.publish()
        owned = self.delivered.command("rev-parse", ancestor + ":rendered/Fixture/.github/hexalith-ci.json")
        self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", json.dumps(configuration(codeql=["ruby"])))
        self.publish()
        wrapper = self.root / "transport-bin/git"
        wrapper.parent.mkdir()
        wrapper.write_text(f"#!{sys.executable}\n" + f'''
import subprocess, sys
arguments=[{self.delivered.root.as_uri()!r} if value=={state.CICD_REMOTE!r}
           else value.replace("protocol.file.allow=never", "protocol.file.allow=always")
           for value in sys.argv[1:]]
raise SystemExit(subprocess.call([{REAL_GIT!r}, *arguments]))
''')
        wrapper.chmod(0o755)
        observed = {}

        def objects(git):
            # Enumerate only present objects; probing a missing oid with
            # cat-file -e would itself trigger the lazy retrieval under test.
            return set(subprocess.check_output([REAL_GIT, "-C", str(git.root), "cat-file",
                "--batch-all-objects", "--batch-check=%(objectname)"],
                env=git.environment).decode().splitlines())

        class ObservedGit(state.Git):
            def blob(self, oid):
                before = objects(self)
                data = super().blob(oid)
                observed.setdefault("reads", []).append((oid, before, objects(self)))
                return data

        class FilteringTransport(state.AnonymousTransport):
            def clone(self, directory, branch):
                acquired = super().clone(directory, branch)
                git = ObservedGit(acquired.root)
                observed["clone"] = objects(git)
                observed["promisor"] = git.run("config", "--get", "remote.origin.promisor").strip()
                return git

            def deepen(self, git, branch):
                super().deepen(git, branch)
                observed["deepen"] = objects(git)

        with patch.dict(os.environ, PATH=str(wrapper.parent) + os.pathsep + os.environ["PATH"]):
            comparison = state.compare_repository(state.Git(self.module.root), repository=REPOSITORY,
                delivered_sha=ancestor, transport=FilteringTransport())
        self.assertEqual((), comparison.drift)
        self.assertEqual(ancestor, comparison.delivered_sha)
        self.assertEqual(b"true", observed["promisor"])
        for phase in ("clone", "deepen"):
            self.assertNotIn(owned, observed[phase])
            self.assertNotIn(unrelated, observed[phase])
        read = next(record for record in observed["reads"] if record[0] == owned)
        self.assertNotIn(owned, read[1])
        self.assertIn(owned, read[2])
        self.assertTrue(all(unrelated not in after for _, _, after in observed["reads"]))

    def test_branch_advance_during_deepening_cannot_change_the_acquired_tip(self):
        ancestor = self.accepted
        self.delivered.write("unowned", "original acquired tip")
        tip = self.publish()
        self.delivered.write("rendered/Fixture/.github/hexalith-ci.json", json.dumps(configuration(codeql=["ruby"])))
        advanced = self.publish()
        fixture = self

        class AdvancingTransport(LocalTransport):
            def deepen(self, git, branch):
                fixture.assertEqual(tip, git.run("rev-parse", "HEAD").decode().strip())
                fixture.delivered.command("branch", "-f", branch, advanced)
                super().deepen(git, branch)
                fixture.assertEqual(advanced, git.run("rev-parse", "refs/remotes/origin/" + branch).decode().strip())

        for requested in (ancestor, advanced):
            self.delivered.command("branch", "-f", "delivered/Fixture", tip)
            transport = AdvancingTransport(self.delivered, self.builds)
            with self.subTest(requested=requested):
                if requested == ancestor:
                    comparison = state.compare_repository(state.Git(self.module.root), repository=REPOSITORY,
                        delivered_sha=requested, transport=transport)
                    self.assertEqual(ancestor, comparison.delivered_sha)
                    self.assertEqual((), comparison.drift)
                else:
                    with self.assertRaisesRegex(state.ExpectedStateError, "not an ancestor"):
                        state.compare_repository(state.Git(self.module.root), repository=REPOSITORY,
                            delivered_sha=requested, transport=transport)

    def test_sibling_and_future_commits_are_rejected_using_only_delivered_branch_history(self):
        base = self.accepted
        self.delivered.write("unowned", "live accepted descendant")
        self.publish()
        self.delivered.command("checkout", "--detach", base)
        self.delivered.write("unowned", "unaccepted sibling")
        sibling = self.delivered.commit()
        self.delivered.command("checkout", "main")
        self.delivered.write("unowned", "future main content")
        future = self.delivered.commit()
        for supplied in (sibling, future):
            self.transport.calls.clear()
            with self.subTest(sha=supplied), self.assertRaisesRegex(state.ExpectedStateError, "not an ancestor"):
                self.compare(delivered_sha=supplied)
            self.assertEqual([("clone", "delivered/Fixture"), ("deepen", "delivered/Fixture")], self.transport.calls)

    def test_missing_branch_manifest_blob_and_invalid_manifest_fail_closed(self):
        self.delivered.command("branch", "-D", "delivered/Fixture")
        with self.assertRaises(state.ExpectedStateError):
            self.compare()
        self.delivered.command("branch", "delivered/Fixture")
        manifest = self.delivered.root / "rendered/Fixture.perimeter.json"
        manifest.unlink()
        sha = self.delivered.commit()
        self.delivered.command("branch", "-f", "delivered/Fixture", sha)
        with self.assertRaisesRegex(state.ExpectedStateError, "expected perimeter"):
            self.compare()
        for document, message in (({}, "missing required"),
                                  ({"schema": "hexalith.perimeter.v99", "entries": []}, "unsupported schema"),
                                  ({"schema": "hexalith.perimeter.v1", "entries": [{"kind": "new-kind"}]}, "unsupported perimeter entry kind")):
            self.document = document
            self.publish()
            with self.subTest(message=message), self.assertRaisesRegex(state.ExpectedStateError, message) as raised:
                self.compare()
            self.assertIn(state.OWNERSHIP_MESSAGE, str(raised.exception))
        self.document = {"schema": "hexalith.perimeter.v1", "entries": [{"kind": "whole", "path": "missing"}]}
        self.publish()
        with self.assertRaisesRegex(state.ExpectedStateError, "missing expected source"):
            self.compare()

    def test_corrupt_manifest_bytes_and_missing_expected_blob_fail_closed(self):
        manifest = self.delivered.root / "rendered/Fixture.perimeter.json"
        manifest.write_bytes(b"\xff invalid UTF-8")
        sha = self.delivered.commit()
        self.delivered.command("branch", "-f", "delivered/Fixture", sha)
        with self.assertRaisesRegex(state.ExpectedStateError, "utf-8"):
            self.compare()
        self.publish()
        fixture = self

        class MissingBlobTransport(LocalTransport):
            def clone(self, directory, branch):
                # Copy the disposable repository so removing a loose source
                # object corrupts only the acquired test source, not its input.
                shutil.copytree(fixture.delivered.root, directory)
                git = state.Git(directory)
                git.run("symbolic-ref", "HEAD", "refs/heads/" + branch)
                oid = fixture.delivered.command("rev-parse", "HEAD:rendered/Fixture/.github/hexalith-ci.json")
                (directory / ".git/objects" / oid[:2] / oid[2:]).unlink()
                return git

        with self.assertRaisesRegex(state.ExpectedStateError, "cat-file") as raised:
            state.compare_repository(state.Git(self.module.root), repository=REPOSITORY,
                transport=MissingBlobTransport(self.delivered, self.builds))
        self.assertIn(state.OWNERSHIP_MESSAGE, str(raised.exception))

    def test_all_nine_kinds_use_the_existing_perimeter_contract_and_tag_repository(self):
        corpus = json.loads((Path(__file__).parent / "fixtures/perimeter-contract.json").read_text())
        self.document = copy.deepcopy(corpus[0]["document"])
        for entry in self.document["entries"]:
            if entry["kind"] == "gitlink":
                entry["buildsTag"] = "v1.2.3"
        self.delivered.write("rendered/Fixture/.github/workflows/ci.yml", "expected workflow\n")
        self.delivered.write("rendered/Fixture/.github/dependabot.yml", "version: 2\n")
        self.publish()
        self.module.write(".github/hexalith-ci.json", "malformed actual JSON")
        self.module.write(".github/workflows/extra.yml", "unexpected workflow\n")
        self.module.write(".gitmodules", '[submodule "bad"]\n path = references/Hexalith.Builds\n url = bad\n update = !touch SHOULD_NOT_RUN\n')
        self.module.write(".claude/settings.json", '{"permissions":{"deny":[]}}')
        self.module.write("AGENTS.md", "unowned instructions\n")
        self.module.write("package.json", '{"release":{},"devDependencies":{"@commitlint/cli":"x"}}')
        self.module.write(".husky/commit-msg", "npx commitlint --edit\n")
        self.module.write("Directory.Build.rsp", "forbidden\n")
        self.module.commit()
        before = (self.module.root / ".git/index").read_bytes()
        comparison = self.compare()
        self.assertEqual(set(state.perimeter.config.FIELDS), {item.kind for item in comparison.drift})
        self.assertEqual(sorted(comparison.drift), list(comparison.drift))
        self.assertIn(("builds", ("v1.2.3",)), self.transport.calls)
        report = "\n".join(comparison.diagnostics())
        self.assertIn(self.builds_sha, report)
        self.assertIn("--- actual/.github/hexalith-ci.json", report)
        with self.assertRaisesRegex(state.ExpectedStateError, "Fix the product code"):
            comparison.require_clean()
        self.assertEqual(before, (self.module.root / ".git/index").read_bytes())
        self.assertFalse((self.module.root / "SHOULD_NOT_RUN").exists())

    def test_tag_resolution_failure_and_noncommit_tag_fail_without_action_git_metadata(self):
        self.document["entries"].append({"kind": "gitlink", "path": "references/Builds", "buildsTag": "v1.2.3"})
        self.publish()
        self.builds.command("tag", "-d", "v1.2.3")
        with self.assertRaises(state.ExpectedStateError):
            self.compare()
        blob = self.builds.command("rev-parse", "HEAD:source")
        self.builds.command("tag", "v1.2.3", blob)
        with self.assertRaisesRegex(state.ExpectedStateError, "expected tag must resolve to a commit"):
            self.compare()

    def test_diagnostics_escape_control_characters_and_actions_commands(self):
        self.module.write(".github/hexalith-ci.json", "::error::injected\nline\r\x1b[31m\n")
        self.module.write(".github/workflows/line\n::warning::path", "x")
        self.module.commit()
        self.document["entries"].append({"kind": "whole-dir", "path": ".github/workflows"})
        self.delivered.write("rendered/Fixture/.github/workflows/ci.yml", "accepted\n")
        self.publish()
        with self.assertRaises(state.ExpectedStateError) as raised:
            self.compare().require_clean()
        diagnostic = str(raised.exception)
        self.assertNotIn("\x1b", diagnostic)
        self.assertIn("\\u001b", diagnostic)
        self.assertIn("\\n::warning::path", diagnostic)
        self.assertFalse(any(line.startswith("::") for line in diagnostic.splitlines()))

    def test_builds_root_has_no_expected_state_io_and_fixture_controls_are_checked_first(self):
        with patch.object(self.transport, "clone", side_effect=AssertionError("unexpected source IO")):
            self.assertIsNone(state.compare_repository(state.Git(self.module.root), repository=BUILDS,
                                                       transport=self.transport))
        for repository, module_root, expected_source in ((REPOSITORY, state.SAMPLE_ROOT, state.SAMPLE_ROOT + "/expected-source"),
                 (BUILDS, "", "x"), (BUILDS, state.SAMPLE_ROOT, ""), (BUILDS, "../bad", "../bad/expected-source")):
            with self.subTest(repository=repository, root=module_root), self.assertRaises(state.ExpectedStateError):
                state.compare_repository(state.Git(self.module.root), repository=repository,
                    module_root=module_root, expected_source=expected_source, transport=self.transport)

    def test_git_environment_has_no_tokens_global_rewrites_headers_or_execution_configuration(self):
        poison = {"GH_TOKEN": "secret-marker", "GITHUB_TOKEN": "secret-marker", "CI_CUSTOM_TOKEN": "secret-marker",
                  "GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "url.file:///poison.insteadOf",
                  "GIT_CONFIG_VALUE_0": "https://github.com/", "GIT_ASKPASS": "/poison", "HOME": "/poison"}
        with patch.dict(os.environ, poison):
            git = state.Git(self.module.root)
            self.assertNotIn("secret-marker", repr(git.environment))
            self.assertNotIn("GIT_CONFIG_COUNT", git.environment)
            self.assertEqual(os.devnull, git.environment["HOME"])
            self.assertEqual(self.module_sha, git.run("rev-parse", "HEAD").decode().strip())

    def test_module_filters_and_hooks_are_never_executed_and_no_index_or_worktree_repair_runs(self):
        self.module.write(".gitattributes", "*.json filter=poison\n")
        self.module.commit()
        marker = self.module.root / "module-code-executed"
        poison = self.module.root / ".git/hooks/post-checkout"
        poison.write_text(f"#!{sys.executable}\nfrom pathlib import Path\nPath({str(marker)!r}).touch()\nraise SystemExit(94)\n")
        poison.chmod(0o755)
        self.module.command("config", "filter.poison.smudge", str(poison))
        self.module.command("config", "filter.poison.clean", str(poison))
        self.module.command("config", "filter.poison.required", "true")
        index = (self.module.root / ".git/index").read_bytes()
        configuration_bytes = (self.module.root / ".github/hexalith-ci.json").read_bytes()
        self.assertEqual((), self.compare().drift)
        self.assertFalse(marker.exists())
        self.assertEqual(index, (self.module.root / ".git/index").read_bytes())
        self.assertEqual(configuration_bytes, (self.module.root / ".github/hexalith-ci.json").read_bytes())

    def test_index_snapshot_is_unavailable_at_the_shared_runtime_boundary(self):
        with self.assertRaisesRegex(state.ExpectedStateError, "committed Git tree"):
            self.compare(ref="index")


if __name__ == "__main__":
    unittest.main()
