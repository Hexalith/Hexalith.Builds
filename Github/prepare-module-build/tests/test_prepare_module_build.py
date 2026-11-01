from __future__ import annotations
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

MODULE_PATH = Path(__file__).resolve().parents[1] / "prepare_module_build.py"
spec = importlib.util.spec_from_file_location("prepare_module_build", MODULE_PATH)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class PreparationTests(unittest.TestCase):
    def git(self, root, *arguments):
        result = subprocess.run(["git", *arguments], cwd=root, text=True, capture_output=True, check=True)
        return result.stdout.strip()

    def repository(self, root):
        root.mkdir(parents=True, exist_ok=True)
        self.git(root, "init", "--initial-branch=main")
        self.git(root, "config", "user.name", "Preparation fixture")
        self.git(root, "config", "user.email", "preparation-fixture@hexalith.test")
        return root

    def commit(self, root, stage=True):
        message = "test: create native preparation Git fixture\n"
        builds_root = MODULE_PATH.parents[2]
        command = ["node", str(builds_root / "node_modules/@commitlint/cli/cli.js"), "--config", str(builds_root / "commitlint.config.mjs")]
        validation = subprocess.run(command, cwd=builds_root, input=message, text=True, capture_output=True, check=False)
        self.assertEqual(0, validation.returncode, validation.stdout + validation.stderr)
        # The test transcript retains exact successful validation evidence before
        # every fixture commit; callers can retain that transcript as a receipt.
        print(json.dumps(dict(command=command, candidate=message, exitCode=validation.returncode, stdout=validation.stdout, stderr=validation.stderr)), flush=True)
        if stage:
            self.git(root, "add", "--all")
        subprocess.run(["git", "commit", "--no-gpg-sign", "--file=-"], cwd=root, input=message, text=True, capture_output=True, check=True)
        return self.git(root, "rev-parse", "HEAD")

    def consumer(self, root):
        self.repository(root)
        (root / ".config").mkdir()
        (root / "module.json").write_text("{}")
        (root / ".config/dotnet-tools.json").write_text(json.dumps(dict(isRoot=True, tools={"hexalith.builds.module.cli": dict(version="9.8.7", rollForward=False, commands=["hexalith-module"])})))
        return root

    def prepare_with_native_git(self, root, export):
        original_run = helper.run
        dotnet_calls = []
        def run(command, cwd):
            if command[0] != "dotnet":
                return original_run(command, cwd)
            dotnet_calls.append(command)
            if command[1:3] == ["tool", "restore"]:
                return "restored"
            if command[-1] == "--version":
                return "9.8.7+source"
            self.assertIn("prepare-build", command)
            workspace = Path(command[command.index("--workspace") + 1])
            return json.dumps(self.result(workspace))
        with mock.patch.object(helper, "run", side_effect=run):
            helper.prepare("module.json", root, root, export)
        self.assertEqual(3, len(dotnet_calls))
        return dotnet_calls

    def result(self, workspace):
        mapping_dir = workspace / "source-mapping"
        mapping_dir.mkdir(parents=True)
        digest = "a" * 64
        environment = {key: str(mapping_dir / filename) for key, filename in helper.IMPORTS.items()}
        environment.update(Configuration="Release", HEXALITH_SOURCE_MAPPING_HASH=digest)
        for filename in helper.IMPORTS.values():
            (mapping_dir / filename).write_text("<Project />")
        (mapping_dir / "mapping.json").write_text(json.dumps(dict(ContentHash=digest, Mode=1, HasGit=True)))
        return dict(status="prepared", outcome=dict(exitCode="Success"), buildPreparation=dict(workspace=str(workspace), mode="package", mappingHash=digest, environment=environment))

    def test_verified_environment_and_unsafe_results(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            self.assertEqual(set(helper.verified_environment(result, workspace)), helper.KEYS)
            mutations = [
                lambda r: r.update(outcome=[]),
                lambda r: r.update(buildPreparation=[]),
                lambda r: r["buildPreparation"]["environment"].update(BASH_ENV="/tmp/attack"),
                lambda r: r["buildPreparation"]["environment"].update(Configuration="Debug"),
                lambda r: r["buildPreparation"].update(mode="source"),
                lambda r: r.update(runId="forbidden"),
                lambda r: r["buildPreparation"]["environment"].update(HEXALITH_SOURCE_MAPPING_HASH="b" * 64),
                lambda r: r["buildPreparation"]["environment"].update(CustomAfterMicrosoftCommonProps="/tmp/outside"),
                lambda r: r["buildPreparation"]["environment"].update(Configuration="Release\nEVIL=1"),
            ]
            for mutate in mutations:
                changed = copy.deepcopy(result)
                mutate(changed)
                with self.assertRaises(ValueError):
                    helper.verified_environment(changed, workspace)

    def test_exact_pin_package_command_and_export_without_shell_evaluation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".config").mkdir()
            platform = root / "references/Hexalith.Platform"
            platform.mkdir(parents=True)
            (platform / ".git").write_text("gitdir: unused-test-fixture")
            (root / "module.json").write_text("{}")
            (root / ".config/dotnet-tools.json").write_text(json.dumps(dict(isRoot=True, tools={"hexalith.builds.module.cli": dict(version="9.8.7", rollForward=False, commands=["hexalith-module"])})))
            export = root / "github-env"
            calls = []
            def run(command, cwd):
                calls.append(command)
                self.assertEqual(cwd, root)
                if command[0] == "git":
                    if "ls-tree" in command:
                        return ""
                    if "rev-parse" in command:
                        return str(platform)
                    self.assertEqual(["git", "-c", "submodule.recurse=false", "-C", str(platform), "fetch", "--no-recurse-submodules", "--no-tags", "origin", "refs/tags/*:refs/tags/*"], command)
                    return ""
                if command[1:3] == ["tool", "restore"]:
                    return "restored"
                if command[-1] == "--version":
                    return "9.8.7+source"
                self.assertIn("prepare-build", command)
                self.assertEqual(command[command.index("--mode") + 1], "package")
                workspace = Path(command[command.index("--workspace") + 1])
                return json.dumps(self.result(workspace))
            with mock.patch.object(helper, "run", side_effect=run):
                helper.prepare("module.json", root, root, export)
            self.assertEqual(6, len(calls))
            self.assertEqual(helper.KEYS, {line.split("=", 1)[0] for line in export.read_text().splitlines()})

    def test_platform_root_preserves_producer_owned_identity_without_a_direct_checkout(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.consumer(Path(temporary))
            (root / "hexalith-platform.json").write_text('{"schemaVersion":1}')
            unrelated = root / "references/Hexalith.Platform"
            unrelated.mkdir(parents=True)
            (unrelated / "unrelated.txt").write_text("ordinary directory")
            self.commit(root)
            (root / "hexalith-platform.json").unlink()
            (unrelated / ".git").mkdir()
            export = root / "github-env"
            self.prepare_with_native_git(root, export)
            self.assertEqual(helper.KEYS, {line.split("=", 1)[0] for line in export.read_text().splitlines()})

    def test_platform_tag_fetch_failure_preserves_environment_and_stops_preparation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".config").mkdir()
            (root / "module.json").write_text("{}")
            (root / ".config/dotnet-tools.json").write_text(json.dumps(dict(isRoot=True, tools={"hexalith.builds.module.cli": dict(version="9.8.7", rollForward=False, commands=["hexalith-module"])})))
            export = root / "github-env"
            export.write_text("EXISTING=value\n")
            # A declared but uninitialized dependency must fail before Git can
            # discover and fetch the enclosing consumer repository instead.
            platform = root / "references/Hexalith.Platform"
            platform.mkdir(parents=True)
            with mock.patch.object(helper, "run", return_value="") as run, self.assertRaises(OSError):
                helper.prepare("module.json", root, root, export)
            self.assertEqual(1, run.call_count)
            self.assertIn("ls-tree", run.call_args.args[0])
            (platform / ".git").write_text("gitdir: unused-test-fixture")
            def failed_fetch(command, cwd):
                if "ls-tree" in command:
                    return ""
                if "rev-parse" in command:
                    return str(platform)
                raise ValueError("Platform tags unavailable")
            with mock.patch.object(helper, "run", side_effect=failed_fetch) as run, self.assertRaisesRegex(ValueError, "Platform tags unavailable"):
                helper.prepare("module.json", root, root, export)
            self.assertEqual(3, run.call_count)
            self.assertIn("fetch", run.call_args.args[0])
            self.assertEqual("EXISTING=value\n", export.read_text())

    def test_failed_tool_or_unqualified_pin_never_exports(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / ".config").mkdir()
            platform = root / "references/Hexalith.Platform"
            platform.mkdir(parents=True)
            (platform / ".git").write_text("gitdir: unused-test-fixture")
            (root / "module.json").write_text("{}")
            manifest = root / ".config/dotnet-tools.json"
            export = root / "github-env"
            def failed_tool(command, cwd):
                if command[0] == "git":
                    if "rev-parse" in command:
                        return str(platform)
                    return ""
                raise ValueError("unpublished prepare-build")
            for version, roll_forward in [("[4.35.2,)", False), ("4.35.2", True), ("4.35.2", False)]:
                manifest.write_text(json.dumps(dict(isRoot=True, tools={"hexalith.builds.module.cli": dict(version=version, rollForward=roll_forward, commands=["hexalith-module"])})))
                with mock.patch.object(helper, "run", side_effect=failed_tool), self.assertRaises(ValueError):
                    helper.prepare("module.json", root, root, export)
                self.assertFalse(export.exists())

    def test_native_fetch_gets_annotated_tags_without_branches_or_nested_initialization(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            nested = self.repository(base / "nested")
            (nested / "dependency.txt").write_text("first nested revision")
            nested_first = self.commit(nested)
            seed = self.repository(base / "platform-seed")
            (seed / "hexalith-platform.json").write_text('{"schemaVersion":1}')
            (seed / ".gitmodules").write_text(f'[submodule "nested"]\n path = references/Nested\n url = {nested.as_posix()}\n')
            self.git(seed, "add", "--all")
            self.git(seed, "update-index", "--add", "--cacheinfo", f"160000,{nested_first},references/Nested")
            first = self.commit(seed, stage=False)
            self.assertTrue(self.git(seed, "ls-tree", "HEAD", "--", "references/Nested").startswith("160000 commit "))
            origin = base / "platform-origin.git"
            self.git(base, "clone", "--bare", str(seed), str(origin))
            root = self.consumer(base / "consumer")
            platform = root / "references/Hexalith.Platform"
            platform.parent.mkdir()
            self.git(root, "clone", "--no-tags", str(origin), str(platform))
            (root / ".gitmodules").write_text(f'[submodule "platform"]\n path = references/Hexalith.Platform\n url = {origin.as_posix()}\n')
            self.git(root, "update-index", "--add", "--cacheinfo", f"160000,{first},references/Hexalith.Platform")
            self.commit(root)
            self.assertEqual("", self.git(platform, "tag", "--list"))
            self.git(platform, "config", "submodule.recurse", "true")
            self.git(platform, "config", "fetch.recurseSubmodules", "true")
            self.git(platform, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*")
            (nested / "dependency.txt").write_text("second nested revision")
            nested_second = self.commit(nested)
            (seed / "release.txt").write_text("new branch and tag objects")
            self.git(seed, "add", "--all")
            self.git(seed, "update-index", "--add", "--cacheinfo", f"160000,{nested_second},references/Nested")
            second = self.commit(seed, stage=False)
            self.assertEqual(f"160000 commit {nested_second}\treferences/Nested", self.git(seed, "ls-tree", "HEAD", "--", "references/Nested"))
            self.git(seed, "tag", "--annotate", "v1.2.3", first, "--message", "Original Platform release")
            self.git(seed, "tag", "--annotate", "v1.2.4", second, "--message", "Later Platform release")
            self.git(seed, "push", str(origin), "HEAD:refs/heads/main", "HEAD:refs/heads/new-branch", "refs/tags/v1.2.3", "refs/tags/v1.2.4")
            # An untracked marker cannot override the committed direct gitlink.
            (root / "hexalith-platform.json").write_text('{"schemaVersion":1}')
            self.prepare_with_native_git(root, root / "github-env")
            self.assertEqual("tag", self.git(platform, "cat-file", "-t", "refs/tags/v1.2.3"))
            self.assertEqual(first, self.git(platform, "rev-parse", "refs/tags/v1.2.3^{}"))
            self.assertEqual(second, self.git(platform, "rev-parse", "refs/tags/v1.2.4^{}"))
            self.assertEqual(first, self.git(platform, "rev-parse", "refs/remotes/origin/main"))
            self.assertEqual("", self.git(platform, "for-each-ref", "--format=%(refname)", "refs/remotes/origin/new-branch"))
            self.assertFalse((platform / "references/Nested/.git").exists())
            self.assertFalse((platform / ".git/modules").exists())

    def test_native_empty_child_git_refuses_parent_discovery_without_fetching_consumer(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root = self.consumer(base / "consumer")
            self.commit(root)
            origin = base / "consumer-origin.git"
            self.git(base, "clone", "--bare", str(root), str(origin))
            self.git(root, "remote", "add", "origin", str(origin))
            commit = self.git(root, "rev-parse", "HEAD")
            self.git(origin, "update-ref", "refs/tags/consumer-only", commit)
            platform = root / "references/Hexalith.Platform"
            (platform / ".git").mkdir(parents=True)
            self.assertEqual(str(root), self.git(platform, "rev-parse", "--show-toplevel"))
            before = self.git(root, "for-each-ref", "--format=%(refname) %(objectname)")
            fetch_head = root / ".git/FETCH_HEAD"
            fetch_head.write_text("consumer fetch must remain unchanged\n")
            export = root / "github-env"
            export.write_text("EXISTING=value\n")
            with self.assertRaisesRegex(ValueError, "expected direct Platform checkout"):
                self.prepare_with_native_git(root, export)
            self.assertEqual(before, self.git(root, "for-each-ref", "--format=%(refname) %(objectname)"))
            self.assertEqual("consumer fetch must remain unchanged\n", fetch_head.read_text())
            self.assertEqual("EXISTING=value\n", export.read_text())

    def test_native_untracked_marker_cannot_select_self_platform(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = self.consumer(Path(temporary))
            self.commit(root)
            (root / "hexalith-platform.json").write_text('{"schemaVersion":1}')
            export = root / "github-env"
            with self.assertRaises(OSError):
                self.prepare_with_native_git(root, export)
            self.assertFalse(export.exists())

    def test_duplicate_keys_and_import_symlinks_refuse(self):
        with self.assertRaises(ValueError):
            helper.read_json('{"status":"prepared","status":"failed"}')
        if os.name == "nt":
            return
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            target = workspace / "source-mapping/SourceMapping.props"
            target.unlink()
            outside = Path(temporary) / "outside.props"
            outside.write_text("<Project />")
            target.symlink_to(outside)
            with self.assertRaises(ValueError):
                helper.verified_environment(result, workspace)

    def test_external_mapping_metadata_symlink_refuses(self):
        if os.name == "nt":
            return
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            metadata = workspace / "source-mapping/mapping.json"
            external = Path(temporary) / "external-mapping.json"
            metadata.replace(external)
            metadata.symlink_to(external)
            with self.assertRaisesRegex(ValueError, "symbolic-link traversal"):
                helper.verified_environment(result, workspace)

    def test_string_package_mode_is_accepted(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            metadata = workspace / "source-mapping/mapping.json"
            mapping = json.loads(metadata.read_text())
            mapping["Mode"] = "package"
            metadata.write_text(json.dumps(mapping))
            self.assertEqual(set(helper.verified_environment(result, workspace)), helper.KEYS)
            mapping["Mode"] = "source"
            metadata.write_text(json.dumps(mapping))
            with self.assertRaisesRegex(ValueError, "persisted mapping differs"):
                helper.verified_environment(result, workspace)

    def test_boolean_mapping_mode_refuses(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "prepared"
            result = self.result(workspace)
            metadata = workspace / "source-mapping/mapping.json"
            mapping = json.loads(metadata.read_text())
            mapping["Mode"] = True
            metadata.write_text(json.dumps(mapping))
            with self.assertRaisesRegex(ValueError, "persisted mapping differs"):
                helper.verified_environment(result, workspace)

    def test_normal_producer_validation_runs_behavioral_helper_tests(self):
        for name in ("ci.yml", "build-release.yml"):
            workflow = (MODULE_PATH.parents[2] / ".github/workflows" / name).read_text()
            self.assertIn("python3 -m unittest discover -s Github/prepare-module-build/tests -v", workflow)
            lane = workflow.split("\n  python-tests:\n", 1)[1] if name == "ci.yml" else workflow
            self.assertLess(lane.index("uses: actions/setup-node@"), lane.index("name: Test module build preparation helper"))
            self.assertLess(lane.index("run: npm ci --ignore-scripts"), lane.index("name: Test module build preparation helper"))
            if name == "ci.yml":
                self.assertEqual(1, lane.count("run: npm ci --ignore-scripts"))
            self.assertIn("Tools/test-domain-workflow-test-platforms.ps1\n", workflow)
            self.assertIn("Tools/test-domain-workflow-test-platforms.ps1 -Contract v2", workflow)

    def test_all_workflow_jobs_opt_in_before_restore(self):
        workflow = (MODULE_PATH.parents[2] / ".github/workflows/domain-ci.yml").read_text()
        self.assertIn("      module-manifest:", workflow)
        for name in ("build-and-test", "aspire-tests", "performance-tests"):
            job = workflow.split("\n  " + name + ":", 1)[1]
            if name != "performance-tests":
                next_name = "aspire-tests" if name == "build-and-test" else "performance-tests"
                job = job.split("\n  " + next_name + ":", 1)[0]
            self.assertIn("inputs.module-manifest != ''", job)
            self.assertIn("inputs.module-manifest != '' && !inputs.governed-ci", job)
            self.assertLess(job.index("name: Prepare module build"), job.index("name: Restore"))
            self.assertIn("./.hexalith/builds-execution/Github/prepare-module-build", job)
            self.assertIn("ref: ${{ job.workflow_sha }}", job)


    def test_owner_job_prepares_before_restore_and_retires_advisory_jobs(self):
        workflow = (MODULE_PATH.parents[2] / ".github/workflows/domain-ci-v2.yml").read_text()
        self.assertIn("      module-manifest:", workflow)
        job = workflow.split("\n  build-and-test:", 1)[1]
        self.assertIn("inputs.module-manifest != '' && !inputs.governed-ci", job)
        self.assertLess(job.index("name: Prepare module build"), job.index("name: Restore"))
        self.assertIn("./.hexalith/builds-execution/Github/prepare-module-build", job)
        self.assertIn("ref: ${{ job.workflow_sha }}", job)
        self.assertNotIn("\n  aspire-tests:", workflow)
        self.assertNotIn("\n  performance-tests:", workflow)


    def test_versioned_workflow_keeps_only_active_inputs_and_explicit_refusals(self):
        workflow = (MODULE_PATH.parents[2] / ".github/workflows/domain-ci-v2.yml").read_text()
        for name in (
            'dapr-version',
            'dapr-runtime-version',
            'aspire-non-performance-filter',
            'performance-filter',
            'aspire-mtp-excluded-trait',
            'performance-mtp-trait',
            'aspire-continue-on-error',
            'aspire-prepare-script',
            'aspire-runner-script',
            'aspire-prepare-timeout-minutes',
            'aspire-runner-timeout-minutes',
            'aspire-evidence-path',
            'aspire-browser-project',
            'aspire-fail-skips',
            'aspire-timeout-minutes',
            'performance-timeout-minutes',
        ):
            with self.subTest(input=name):
                self.assertNotIn("      " + name + ":", workflow)
        self.assertIn("LEGACY_INTEGRATION: ${{ inputs.integration-test-projects }}", workflow)
        self.assertIn("LEGACY_ASPIRE: ${{ inputs.aspire-test-project }}", workflow)
        legacy = (MODULE_PATH.parents[2] / ".github/workflows/domain-ci.yml").read_text()
        self.assertIn("      dapr-version:", legacy)
        self.assertIn("      aspire-prepare-script:", legacy)
        self.assertNotIn("registry-pull-password", legacy)


if __name__ == "__main__":
    unittest.main()
