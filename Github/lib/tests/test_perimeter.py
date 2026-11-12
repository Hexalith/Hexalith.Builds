"""Observe perimeter repairs in real Git indexes, with untouched working trees."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("perimeter", ROOT / "Github/lib/perimeter.py")
P = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = P
SPEC.loader.exec_module(P)
CORPUS = json.loads((Path(__file__).parent / "fixtures/perimeter-contract.json").read_text())


class Repository:
    def __init__(self, path: Path):
        self.path = path
        path.mkdir()
        self.command("init", "-q", "-b", "main")
        self.command("config", "user.name", "Fixture")
        self.command("config", "user.email", "fixture@example.invalid")
        self.command("config", "commit.gpgsign", "false")
        self.command("config", "core.autocrlf", "false")
        self.git = P.Git(path)

    def command(self, *args, data=None, check=True, environment=None):
        env = os.environ.copy()
        for key in ("GIT_INDEX_FILE", "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_ATTR_SOURCE"):
            env.pop(key, None)
        if environment:
            env.update(environment)
        result = subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "-C", str(self.path), *args],
                                input=data, capture_output=True, env=env, check=False)
        if check and result.returncode:
            raise AssertionError((args, result.stderr))
        return result.stdout

    def write(self, path, data):
        target = self.path / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data.encode() if isinstance(data, str) else data)

    def stage(self):
        self.command("add", "--all")

    def commit(self):
        self.stage()
        self.command("commit", "-qm", "fixture", "--allow-empty")
        return self.command("rev-parse", "HEAD").decode().strip()

    def blob(self, path):
        return self.git.snapshot("index").read(path)

    def tree_bytes(self):
        # Test observers can inspect the worktree; the production library cannot.
        result = {}
        for path in self.path.rglob("*"):
            if ".git" in path.relative_to(self.path).parts:
                continue
            if path.is_symlink():
                result[path.relative_to(self.path).as_posix()] = ("link", os.readlink(path))
            elif path.is_file():
                result[path.relative_to(self.path).as_posix()] = ("file", path.read_bytes(), path.stat().st_mode)
        return result


class PerimeterTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="perimeter-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        self.module = Repository(root / "module")
        self.render = Repository(root / "render")
        self.builds = Repository(root / "builds")
        self.builds.write("source", "tagged commit\n")
        self.builds_sha = self.builds.commit()
        self.builds.command("tag", "-a", "v1.2.3", "-m", "annotated stable fixture")
        self.module.commit()
        self.render.commit()

    def document(self, *entries):
        return {"schema": "hexalith.perimeter.v1", "entries": list(entries)}

    def compare(self, document, *, ref="index", repository=None, selected=None, expected=None):
        repository = repository or self.module.git
        return P.compare(document, repository.snapshot(ref), expected=expected or self.render.git.snapshot(),
                         rendered_root="rendered/Fixture", builds=self.builds.git, selected=selected)

    def apply(self, document, *, repository=None, selected=None, expected=None):
        return P.apply(document, repository or self.module.git, expected=expected or self.render.git.snapshot(),
                       rendered_root="rendered/Fixture", builds=self.builds.git, selected=selected)

    def assert_roundtrip(self, document, *, repository=None, selected=None, expected=None):
        before = self.module.tree_bytes()
        drift = self.compare(document, repository=repository, selected=selected, expected=expected)
        self.assertEqual(sorted(drift), drift)
        self.assertEqual(drift, self.apply(document, repository=repository, selected=selected, expected=expected))
        self.assertEqual([], self.compare(document, repository=repository, selected=selected, expected=expected))
        index = (repository or self.module.git).index_path().read_bytes()
        self.assertEqual([], self.apply(document, repository=repository, selected=selected, expected=expected))
        self.assertEqual(index, (repository or self.module.git).index_path().read_bytes())
        self.assertEqual(before, self.module.tree_bytes())
        return drift

    def test_ad4_manifest_repairs_every_kind_preserving_foreign_content(self):
        document = copy.deepcopy(CORPUS[0]["document"])
        self.render.write("rendered/Fixture/.github/workflows/ci.yml", "name: expected\n")
        self.render.write("rendered/Fixture/.github/hexalith-ci.json", '{"schema":"expected"}\n')
        self.render.write("rendered/Fixture/.github/dependabot.yml", "version: 2\n")
        self.render.commit()
        self.module.write(".github/workflows/extra.yml", "unrendered\n")
        self.module.write(".github/hexalith-ci.json", "{ malformed whole owned file")
        self.module.write(".github/actions/module/action.yml", "module-owned\n")
        self.module.write(".gitmodules", '[submodule "existing name"]\n path = references/Hexalith.Builds\n url = old\n branch = main\n update = checkout\n custom = keep\n[submodule "other"]\n path = references/Other\n url = other\n')
        self.module.write(".claude/settings.json", json.dumps({"permissions": {"deny": ["module deny"], "allow": ["module allow"]}, "module": True}))
        self.module.write("AGENTS.md", "module instructions\n")
        self.module.write("package.json", json.dumps({"release": {}, "commitlint": {}, "name": "module", **{
            group: {"semantic-release": "x", "@semantic-release/github": "x", "@commitlint/cli": "x", "module-pin": "1"}
            for group in ("dependencies", "devDependencies", "optionalDependencies", "peerDependencies")}}))
        self.module.write(".husky/commit-msg", "#!/bin/sh\nnpx commitlint --edit\nmodule-command\n")
        for path in (".releaserc.json", "release.config.js", "commitlint.config.cjs", ".commitlintrc.yaml",
                     "Directory.Build.rsp", "src/deep/Directory.Build.rsp", ".agents/skills/bmad-testarch-ci/SKILL.md"):
            self.module.write(path, "forbidden\n")
        self.module.commit()
        self.module.write("unrelated", "staged unrelated\n")
        self.module.stage()
        staged = self.module.git.snapshot("index").items["unrelated"]
        self.module.write("unrelated", "dirty unrelated\n")
        self.module.write("untracked", "untracked\n")
        checkout = self.module.path / "references/Hexalith.Builds"
        checkout.mkdir(parents=True)
        (checkout / "checkout.txt").write_text("preserve submodule checkout")
        drift = self.assert_roundtrip(document)
        self.assertEqual(set(P.config.FIELDS), {record.kind for record in drift})
        self.assertIn(P.Drift(".github/workflows/extra.yml", "whole-dir", "unexpected"), drift)
        snapshot = self.module.git.snapshot("index")
        self.assertNotIn(".github/workflows/extra.yml", snapshot.items)
        self.assertEqual(P.Item("160000", self.builds_sha), snapshot.items["references/Hexalith.Builds"])
        self.assertEqual(staged, snapshot.items["unrelated"])
        settings = json.loads(snapshot.read(".claude/settings.json"))
        self.assertEqual(["module allow"], settings["permissions"]["allow"])
        self.assertEqual("module deny", settings["permissions"]["deny"][0])
        required = next(entry["values"] for entry in document["entries"] if entry["kind"] == "json-superset")
        self.assertTrue(set(required).issubset(settings["permissions"]["deny"]))
        self.assertTrue(settings["module"])
        package = json.loads(snapshot.read("package.json"))
        self.assertEqual("module", package.pop("name"))
        self.assertTrue(all(pins == {"module-pin": "1"} for pins in package.values()))
        modules = snapshot.read(".gitmodules").decode()
        self.assertIn('[submodule "existing name"]', modules)
        self.assertIn("custom = keep", modules)
        self.assertIn('[submodule "other"]', modules)
        self.assertNotIn("branch =", modules)
        self.assertNotIn("update =", modules)
        self.assertEqual(b"#!/bin/sh\nmodule-command\n", snapshot.read(".husky/commit-msg"))
        self.assertTrue(snapshot.read("AGENTS.md").startswith(b"module instructions\n"))
        self.assertEqual(b"module-owned\n", snapshot.read(".github/actions/module/action.yml"))

    def test_superset_adds_every_required_value_when_only_first_exists(self):
        self.module.write("settings", '{"deny":["first","foreign"]}')
        self.module.commit()
        document = self.document({"kind": "json-superset", "path": "settings", "pointer": "/deny",
                                  "values": ["first", "second", "third"]})
        self.assertEqual([P.Drift("settings", "json-superset", "partial")], self.compare(document))
        self.assert_roundtrip(document)
        self.assertEqual(["first", "foreign", "second", "third"], json.loads(self.module.blob("settings"))["deny"])

    def test_long_array_pointer_is_a_controlled_atomic_error(self):
        self.module.write("settings", '["foreign"]')
        self.module.commit()
        self.assert_atomic_error(self.document({"kind": "json-superset", "path": "settings",
                                                "pointer": "/" + "9" * 5000, "values": ["owned"]}),
                                 "pointer cannot repair")

    def test_compliant_deep_json_is_not_serialized(self):
        data = ('{"deny":["owned"],"foreign":' + '[' * 700 + '0' + ']' * 700 + '}').encode()
        self.module.write("settings", data)
        self.module.commit()
        document = self.document({"kind": "json-superset", "path": "settings", "pointer": "/deny", "values": ["owned"]})
        self.assertEqual([], self.assert_roundtrip(document))
        self.assertEqual(data, self.module.blob("settings"))

    def test_head_index_dirty_and_pinned_expected_tree_are_distinct(self):
        path = "literal $ # : (name).json"
        self.module.write(path, "head\n")
        self.module.commit()
        self.render.write("rendered/Fixture/" + path, "head\n")
        pinned = self.render.commit()
        self.module.write(path, "staged\n")
        self.module.stage()
        self.module.write(path, "dirty\n")
        self.render.write("rendered/Fixture/" + path, "new expected\n")
        self.render.commit()
        expected = self.render.git.snapshot(pinned)
        document = self.document({"kind": "whole", "path": path})
        self.assertEqual([], self.compare(document, ref="HEAD", expected=expected))
        self.assertEqual([P.Drift(path, "whole", "content")], self.compare(document, expected=expected))
        self.assert_roundtrip(document, expected=expected)
        self.assertEqual(b"head\n", self.module.blob(path))

    def test_passing_partial_crlf_blobs_and_regular_modes_remain_identical(self):
        rules = [
            {"kind": "json-superset", "path": "settings", "pointer": "/permissions/deny", "values": ["owned"]},
            {"kind": "marker-block", "path": "instructions", "text": "owned\n"},
            {"kind": "gitmodules-section", "path": ".gitmodules", "submodulePath": "refs/builds", "url": "expected"},
            {"kind": "absent-json", "path": "package", "selectors": [["forbidden"]]},
            {"kind": "absent-line", "path": "hook", "pattern": "commitlint"},
        ]
        data = {"settings": b'{ "permissions": {"deny":["extra", "owned"], "allow":["keep"]}}\r\n',
                "instructions": b"foreign\r\n<!-- hexalith-cicd:begin -->\r\nowned\r\n<!-- hexalith-cicd:end -->\r\ntrailer\r\n",
                ".gitmodules": b'[submodule "name"]\r\npath=refs/builds\r\nurl=expected\r\ncustom=keep\r\n',
                "package": b'{ "foreign" : true }\r\n', "hook": b"module-command\r\n"}
        for path, content in data.items():
            self.module.write(path, content)
        (self.module.path / "hook").chmod(0o755)
        self.module.commit()
        before = self.module.git.snapshot("index").items.copy()
        self.assertEqual([], self.assert_roundtrip(self.document(*rules)))
        self.assertEqual(before, self.module.git.snapshot("index").items)

    def test_absent_missing_targets_pass_and_malformed_partials_recreate_only_owned_content(self):
        absent = self.document({"kind": "absent-json", "path": "missing", "selectors": [["x"]]},
                               {"kind": "absent-line", "path": "missing-hook", "pattern": "."})
        self.assertEqual([], self.assert_roundtrip(absent))
        rules = [
            {"kind": "json-superset", "path": "settings", "pointer": "/permissions/deny", "values": ["owned"]},
            {"kind": "marker-block", "path": "guide", "text": "owned"},
            {"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"},
            {"kind": "absent-json", "path": "package", "selectors": [["x"]]},
            {"kind": "absent-line", "path": "hook", "pattern": "commitlint"},
        ]
        for path in ("settings", "modules", "package", "hook"):
            self.module.write(path, b"\xffbroken")
        self.module.write("guide", "foreign\n<!-- hexalith-cicd:begin -->\nbroken\n")
        self.module.commit()
        self.assert_roundtrip(self.document(*rules))
        self.assertEqual({"permissions": {"deny": ["owned"]}}, json.loads(self.module.blob("settings")))
        self.assertEqual(b"{}\n", self.module.blob("package"))
        self.assertEqual(b"", self.module.blob("hook"))
        self.assertNotIn(b"foreign", self.module.blob("guide"))
        self.assertNotIn(b"broken", self.module.blob("modules"))

    def test_missing_positive_partial_files_are_created_and_disjoint_json_entries_compose(self):
        document = self.document(
            {"kind": "json-superset", "path": "settings", "pointer": "/permissions/deny", "values": ["owned"]},
            {"kind": "json-superset", "path": "settings", "pointer": "/other", "values": ["second"]},
            {"kind": "marker-block", "path": "guide", "text": "owned"},
            {"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"})
        self.assert_roundtrip(document)
        self.assertEqual({"permissions": {"deny": ["owned"]}, "other": ["second"]}, json.loads(self.module.blob("settings")))
        self.assertEqual("100644", self.module.git.snapshot("index").items["settings"].mode)

    def test_json_pointer_escapes_and_selector_slashes_are_key_data(self):
        self.module.write("settings", '{"a/b":{"~":{"deny":["extra"]}},"permissions":{"allow":["keep"]}}')
        self.module.write("package", '{"dependencies":{"@scope/remove":"1","@scope/keep":"2","foreign":"3"}}')
        self.module.commit()
        document = self.document(
            {"kind": "json-superset", "path": "settings", "pointer": "/a~1b/~0/deny", "values": ["owned"]},
            {"kind": "absent-json", "path": "package", "selectors": [["dependencies", "@scope/rem*"]]})
        self.assert_roundtrip(document)
        self.assertEqual(["extra", "owned"], json.loads(self.module.blob("settings"))["a/b"]["~"]["deny"])
        self.assertEqual({"@scope/keep": "2", "foreign": "3"}, json.loads(self.module.blob("package"))["dependencies"])

    def test_json_pointer_array_positions_preserve_foreign_array_members(self):
        original = {"items": [{"deny": ["extra"], "foreign": True}, {"foreign": "sibling"}]}
        self.module.write("settings", json.dumps(original))
        self.module.commit()
        document = self.document({"kind": "json-superset", "path": "settings", "pointer": "/items/0/deny", "values": ["owned"]})
        self.assert_roundtrip(document)
        original["items"][0]["deny"].append("owned")
        self.assertEqual(original, json.loads(self.module.blob("settings")))

    def test_json_positive_and_absence_repairs_preserve_exact_foreign_number_tokens(self):
        numeric = b'"precise": 0.123456789012345678901234567890, "large": 1e999, "negativeZero": -0'
        original = b'{' + numeric + b', "deny": [], "remove": true}'
        for path in ("positive", "absence"):
            self.module.write(path, original)
        self.module.commit()
        self.assert_roundtrip(self.document(
            {"kind": "json-superset", "path": "positive", "pointer": "/deny", "values": ["owned"]},
            {"kind": "absent-json", "path": "absence", "selectors": [["remove"]]}))
        for path in ("positive", "absence"):
            repaired = self.module.blob(path)
            for token in (b"0.123456789012345678901234567890", b"1e999", b"-0"):
                self.assertIn(token, repaired)
            self.assertNotIn(b"Infinity", repaired)
        self.assertEqual(["owned"], json.loads(self.module.blob("positive"))["deny"])
        self.assertNotIn("remove", json.loads(self.module.blob("absence")))

    def test_valid_marker_replaces_only_block_and_duplicate_markers_recreate(self):
        before = "module prefix\n<!-- hexalith-cicd:begin -->\nold\n<!-- hexalith-cicd:end -->\nmodule suffix\n"
        self.module.write("guide", before)
        self.module.commit()
        document = self.document({"kind": "marker-block", "path": "guide", "text": "new"})
        self.assert_roundtrip(document)
        self.assertEqual(before.replace("\nold\n", "\nnew\n").encode(), self.module.blob("guide"))
        self.module.write("guide", before + before)
        self.module.stage()
        self.assert_roundtrip(document)
        self.assertNotIn(b"module prefix", self.module.blob("guide"))

    def test_schema_valid_marker_body_with_nul_converges(self):
        document = self.document({"kind": "marker-block", "path": "guide", "text": "owned\0body"})
        self.assert_roundtrip(document)
        self.assertIn(b"owned\0body", self.module.blob("guide"))

    def test_partial_repairs_preserve_foreign_crlf_bytes_with_text_conversion_disabled(self):
        self.module.write(".gitattributes", "* -text\n")
        prefix, suffix = b"foreign prefix\r\n", b"\r\nforeign suffix\r\n"
        self.module.write("guide", prefix + b"<!-- hexalith-cicd:begin -->\r\nold\r\n<!-- hexalith-cicd:end -->" + suffix)
        self.module.write("hook", b"#!/bin/sh\r\nnpx commitlint --edit\r\nmodule-command\r\n")
        foreign = b'[submodule "foreign"]\r\npath=refs/other\r\nurl=keep\r\ncustom=unchanged\r\n'
        self.module.write("modules", foreign + b'[submodule "owned"]\r\npath=refs/builds\r\nurl=old\r\ncustom=retained\r\n')
        self.module.commit()
        self.assert_roundtrip(self.document(
            {"kind": "marker-block", "path": "guide", "text": "owned"},
            {"kind": "absent-line", "path": "hook", "pattern": "commitlint"},
            {"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"}))
        self.assertTrue(self.module.blob("guide").startswith(prefix))
        self.assertTrue(self.module.blob("guide").endswith(suffix))
        self.assertEqual(b"#!/bin/sh\r\nmodule-command\r\n", self.module.blob("hook"))
        self.assertIn(foreign, self.module.blob("modules"))
        self.assertIn(b"custom=retained\r\n", self.module.blob("modules"))

    def test_gitmodules_git_syntax_duplicate_sections_and_foreign_keys_survive(self):
        self.module.write("modules", '# prefix\n[submodule "first \\"name\\""]\n path = "refs/builds" # owned\n url = old\n update = checkout\n branch = main\n custom = "kept \\\nvalue"\n[submodule "duplicate"]\n path = refs/builds\n url = bad\n[submodule.other]\n path=refs/other\n url=foreign\n')
        self.module.commit()
        document = self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected#quoted"})
        self.assert_roundtrip(document)
        repaired = self.module.blob("modules").decode()
        self.assertIn('first \\"name\\"', repaired)
        self.assertIn('custom = "kept \\\nvalue"', repaired)
        self.assertIn("[submodule.other]", repaired)
        self.assertNotIn("duplicate", repaired)
        self.assertNotIn("branch =", repaired)
        self.assertNotIn("update =", repaired)

    def test_gitmodules_url_only_drift_is_observable_through_git(self):
        self.module.write("modules", '[submodule "owned"]\npath=refs/builds\nurl=wrong\ncustom=keep\n')
        self.module.commit()
        document = self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"})
        self.assertEqual([P.Drift("modules", "gitmodules-section", "partial")], self.compare(document))
        self.assert_roundtrip(document)
        self.assertEqual(b"expected\n", self.module.command("config", "--file", "-", "--get", "submodule.owned.url", data=self.module.blob("modules")))

    def test_gitmodules_bom_and_cr_headers_cannot_hide_owned_sections(self):
        for prefix in ("\ufeff", "\r", "\ufeff\r"):
            with self.subTest(prefix=prefix):
                self.module.write("modules", prefix + '[submodule "owned"]\npath=refs/builds\nurl=wrong\nbranch=main\ncustom=keep\n')
                self.module.stage()
                document = self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"})
                self.assertEqual([P.Drift("modules", "gitmodules-section", "partial")], self.compare(document))
                self.assert_roundtrip(document)
                data = self.module.blob("modules")
                self.assertTrue(data.startswith(prefix.encode()))
                self.assertEqual(b"refs/builds\n", self.module.command("config", "--file", "-", "--get-all", "submodule.owned.path", data=data))
                self.assertEqual(b"expected\n", self.module.command("config", "--file", "-", "--get-all", "submodule.owned.url", data=data))
                self.assertNotIn(b"branch=", data)
                self.assertIn(b"custom=keep", data)

    def test_gitmodules_and_hooks_use_only_physical_lf_lines(self):
        for separator in ("\u2028", "\u2029", "\u0085", "\v", "\f", "\r"):
            with self.subTest(separator=separator):
                foreign = 'custom=keep' + separator + 'url=foreign\n'
                self.module.write("modules", '[submodule "owned"]\npath=refs/builds\nurl=wrong\n' + foreign)
                self.module.write("hook", 'keep\necho keep' + separator + 'commitlint xx\nlast\n')
                self.module.stage()
                original = self.module.command("config", "--file", "-", "--get", "submodule.owned.custom", data=self.module.blob("modules"))
                self.assert_roundtrip(self.document(
                    {"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"},
                    {"kind": "absent-line", "path": "hook", "pattern": "commitlint"}))
                self.assertEqual(original, self.module.command("config", "--file", "-", "--get", "submodule.owned.custom", data=self.module.blob("modules")))
                self.assertEqual(b"keep\nlast\n", self.module.blob("hook"))

    def test_gitmodules_default_name_collision_fails_before_any_index_change(self):
        self.module.write("modules", '[submodule "refs/builds"]\n path=refs/other\n url=keep\n')
        self.module.commit()
        document = self.document({"kind": "marker-block", "path": "guide", "text": "owned"},
                                 {"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"})
        self.assert_atomic_error(document, "default submodule section name")

    def test_gitmodules_default_name_without_path_is_completed_preserving_foreign_keys(self):
        self.module.write("modules", '[submodule "refs/builds"]\nurl=old\ncustom=keep\n')
        self.module.commit()
        self.assert_roundtrip(self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"}))
        self.assertIn(b"custom=keep\n", self.module.blob("modules"))
        self.assertIn(b'path = "refs/builds"', self.module.blob("modules"))

    def test_gitmodules_final_foreign_continuations_keep_their_parsed_values(self):
        self.module.write("new-section", '[submodule "foreign"]\npath=refs/other\nurl=keep\ncustom=tail \\\n')
        self.module.write("existing-section", '[submodule "owned"]\npath=refs/builds\nurl=old\ncustom=tail \\\n')
        self.module.commit()
        originals = {path: self.module.command("config", "--file", "-", "--get", key, data=self.module.blob(path))
                     for path, key in (("new-section", "submodule.foreign.custom"),
                                       ("existing-section", "submodule.owned.custom"))}
        self.assert_roundtrip(self.document(*[
            {"kind": "gitmodules-section", "path": path, "submodulePath": "refs/builds", "url": "expected"}
            for path in originals]))
        for path, key in (("new-section", "submodule.foreign.custom"), ("existing-section", "submodule.owned.custom")):
            self.assertEqual(originals[path], self.module.command("config", "--file", "-", "--get", key, data=self.module.blob(path)))

    def test_gitmodules_comment_backslash_cannot_hide_or_modify_foreign_sections(self):
        foreign = '[submodule "foreign"]\npath=refs/other\nurl=keep\ncustom=foreign-key\n'
        self.module.write("modules", '[submodule "owned"]\npath=refs/builds\nurl=old\n# comment \\\n' + foreign)
        self.module.commit()
        self.assert_roundtrip(self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"}))
        self.assertIn(foreign.encode(), self.module.blob("modules"))

    def test_gitmodules_comments_only_file_retains_its_comments_when_section_is_added(self):
        self.module.write("modules", "# module comment\n; another comment\n")
        self.module.commit()
        self.assert_roundtrip(self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"}))
        self.assertTrue(self.module.blob("modules").startswith(b"# module comment\n; another comment\n"))

    def test_gitmodules_repeated_header_fragments_cannot_override_owned_fields(self):
        self.module.write("modules", '[submodule "owned"]\npath=refs/builds\nurl=expected\n'
                          '[submodule "foreign"]\npath=refs/other\nurl=keep\n'
                          '[submodule "owned"]\nurl=overridden\nbranch=main\ncustom=preserved\n')
        self.module.commit()
        document = self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"})
        self.assert_roundtrip(document)
        repaired = self.module.blob("modules")
        self.assertNotIn(b"overridden", repaired)
        self.assertNotIn(b"branch=", repaired)
        self.assertIn(b"custom=preserved", repaired)
        self.assertIn(b"path=refs/other\nurl=keep", repaired)

    def test_gitmodules_same_section_name_with_foreign_path_is_an_atomic_conflict(self):
        self.module.write("modules", '[submodule "same"]\npath=refs/builds\nurl=expected\n'
                          '[submodule "same"]\npath=refs/other\nurl=keep\n')
        self.module.commit()
        self.assert_atomic_error(self.document({"kind": "gitmodules-section", "path": "modules", "submodulePath": "refs/builds", "url": "expected"}),
                                 "matching submodule section name belongs")

    def test_whole_restores_expected_mode_and_replaces_symlink_and_gitlink(self):
        self.render.write("rendered/Fixture/script", "expected\n")
        (self.render.path / "rendered/Fixture/script").chmod(0o755)
        self.render.commit()
        external = self.module.path.parent / "external"
        external.write_text("preserve external")
        (self.module.path / "script").symlink_to(external)
        self.module.commit()
        document = self.document({"kind": "whole", "path": "script"})
        self.assert_roundtrip(document)
        self.assertEqual("100755", self.module.git.snapshot("index").items["script"].mode)
        self.module.command("update-index", "--add", "--cacheinfo", "160000", self.builds_sha, "script")
        self.assert_roundtrip(document)
        self.assertEqual("preserve external", external.read_text())

    def test_whole_symlink_target_bypasses_regular_file_clean_filters(self):
        target = self.render.path / "rendered/Fixture/link"
        target.parent.mkdir(parents=True)
        target.symlink_to("raw lower target")
        self.render.commit()
        self.module.write(".gitattributes", "link filter=uppercase\n")
        self.module.commit()
        self.module.command("config", "filter.uppercase.clean", "tr a-z A-Z")
        self.assert_roundtrip(self.document({"kind": "whole", "path": "link"}))
        self.assertEqual("120000", self.module.git.snapshot("index").items["link"].mode)
        self.assertEqual(b"raw lower target", self.module.blob("link"))

    def test_whole_directory_replaces_an_owned_non_directory_without_touching_it(self):
        self.module.write("owned", "tracked file in directory position\n")
        self.module.commit()
        self.render.write("rendered/Fixture/owned/expected", "expected\n")
        self.render.commit()
        self.assert_roundtrip(self.document({"kind": "whole-dir", "path": "owned"}))
        self.assertNotIn("owned", self.module.git.snapshot("index").items)
        self.assertEqual(b"expected\n", self.module.blob("owned/expected"))

    def test_nonregular_partial_target_never_follows_links_and_uses_regular_mode(self):
        external = self.module.path.parent / "external"
        external.write_text('{"foreign":"outside"}')
        (self.module.path / "settings").symlink_to(external)
        self.module.commit()
        self.assert_roundtrip(self.document({"kind": "json-superset", "path": "settings", "pointer": "/deny", "values": ["owned"]}))
        self.assertEqual("100644", self.module.git.snapshot("index").items["settings"].mode)
        self.assertEqual({"deny": ["owned"]}, json.loads(self.module.blob("settings")))
        self.assertEqual('{"foreign":"outside"}', external.read_text())

    def test_nonregular_absence_only_json_recreates_an_empty_object(self):
        external = self.module.path.parent / "external"
        external.write_text('{"foreign":"outside"}')
        (self.module.path / "package").symlink_to(external)
        self.module.commit()
        document = self.document({"kind": "absent-json", "path": "package", "selectors": [["release"]]})
        self.assert_roundtrip(document)
        self.assertEqual(b"{}\n", self.module.blob("package"))
        self.assertEqual("100644", self.module.git.snapshot("index").items["package"].mode)
        self.module.command("update-index", "--add", "--cacheinfo", "160000", self.builds_sha, "package")
        self.assert_roundtrip(document)
        self.assertEqual(b"{}\n", self.module.blob("package"))
        self.assertEqual('{"foreign":"outside"}', external.read_text())

    def test_regular_partial_repair_preserves_executable_mode(self):
        self.module.write("hook", "commitlint\nkeep\n")
        (self.module.path / "hook").chmod(0o755)
        self.module.commit()
        self.assert_roundtrip(self.document({"kind": "absent-line", "path": "hook", "pattern": "commitlint"}))
        self.assertEqual("100755", self.module.git.snapshot("index").items["hook"].mode)

    def test_git_wildmatch_classes_question_and_zero_multiple_directory_double_star(self):
        paths = ["Directory.Build.rsp", "a/Directory.Build.rsp", "a/b/Directory.Build.rsp",
                 "x/bmad-testarch-ci/SKILL.md", "bmad-testarch-ci/deep/file", "src/keep.rsp",
                 ".releaserc", ".releaserc.json", "nested/.releaserc.json", "x1a", "xza", "a/x2a"]
        for path in paths:
            self.module.write(path, "tracked\n")
        self.module.commit()
        for pattern in ("**/Directory.Build.rsp", "**/bmad-testarch-ci/**", ".releaserc*", "x[0-9]?", "**/x[0-9]?",
                        "x[[:digit:]]?", "x[^a]?", "x[!a]?", "x[[:punct:]]?"):
            with self.subTest(pattern=pattern):
                oracle = self.module.command("ls-files", "-z", "--", ":(glob)" + pattern).split(b"\0")
                self.assertEqual({os.fsdecode(path) for path in oracle if path}, {path for path in paths if P.wildmatch(pattern, path)})
        self.assert_roundtrip(self.document(*[{"kind": "absent-glob", "pattern": pattern} for pattern in
                                             ("**/Directory.Build.rsp", "**/bmad-testarch-ci/**", ".releaserc*", "x[0-9]?")]))
        remaining = self.module.git.snapshot("index").items
        self.assertIn("nested/.releaserc.json", remaining)
        self.assertIn("src/keep.rsp", remaining)
        self.assertIn("a/x2a", remaining)

    def test_git_wildmatch_question_and_classes_count_utf8_path_bytes(self):
        paths = ["xé", "xéa", "xza", "x☃", "x☃a"]
        for path in paths:
            self.module.write(path, "tracked\n")
        self.module.commit()
        for pattern in ("x?", "x??", "x???", "x[é]", "x[!é]", "xé?", "x☃*", "x*"):
            with self.subTest(pattern=pattern):
                oracle = self.module.command("ls-files", "-z", "--", ":(glob)" + pattern).split(b"\0")
                self.assertEqual({os.fsdecode(path) for path in oracle if path},
                                 {path for path in paths if P.wildmatch(pattern, path)})
        self.assert_roundtrip(self.document({"kind": "absent-glob", "pattern": "x??"}))
        self.assertEqual({"xéa", "x☃", "x☃a"}, set(self.module.git.snapshot("index").items))

    def test_git_wildmatch_descending_ranges_keep_the_starting_endpoint(self):
        paths = ("xa", "xb", "xz", "xc", "x-")
        for path in paths:
            self.module.write(path, "tracked\n")
        self.module.commit()
        for pattern, expected in (("x[b-a]", {"xb"}), ("x[z-a]", {"xz"}), ("x[a--]", {"xa"})):
            with self.subTest(pattern=pattern):
                oracle = self.module.command("ls-files", "-z", "--", ":(glob)" + pattern).split(b"\0")
                self.assertEqual(expected, {os.fsdecode(path) for path in oracle if path})
                self.assertEqual(expected, {path for path in paths if P.wildmatch(pattern, path)})

    def test_git_wildmatch_posix_opener_consumed_as_range_endpoint_is_literal(self):
        paths = {"xa", "xz", "x0", "x1", "xd", "xg", "xi", "xt", "x:", "x[", "x]", "keep",
                 "xa]", "xz]", "xd]", "x0]", "xZ]", "x[]", "x]]", "x-]", "x:]"}
        for path in paths:
            self.module.write(path, "tracked\n")
        self.module.commit()
        patterns = ("x[a-[:digit:]]", "x[z-[:digit:]]", "x[0-[:digit:]]", "x[--[:digit:]]",
                    "x[]-[:digit:]]")
        removed = set()
        for pattern in patterns:
            with self.subTest(pattern=pattern):
                oracle = self.module.command("ls-files", "-z", "--", ":(glob)" + pattern).split(b"\0")
                expected = {os.fsdecode(path) for path in oracle if path}
                self.assertTrue(expected)
                self.assertTrue(all(len(path) == 3 and path.endswith("]") for path in expected))
                if pattern in ("x[0-[:digit:]]", "x[--[:digit:]]"):
                    self.assertIn("x[]", expected)
                self.assertEqual(expected, {path for path in paths if P.wildmatch(pattern, path)})
                removed.update(expected)
        self.assert_roundtrip(self.document(*[{"kind": "absent-glob", "pattern": pattern} for pattern in patterns]))
        self.assertEqual(paths - removed, set(self.module.git.snapshot("index").items))

    def test_git_wildmatch_malformed_classes_never_remove_unmatched_paths(self):
        paths = {"xa[]", "xa[", "x[", "x]", "x[:]", "x::]", "x!", "x:", "xb", "x[]", "x^]", "xa]", "keep"}
        for path in paths:
            self.module.write(path, "foreign\n")
        self.module.commit()
        patterns = ("x[a][]", "x[a][", "x[", "x[]", "x[[:]:]", "x[]:b!:[:]", "x[[b![:]",
                    "x[:[:]", "x[[:]", "x[a[^[:]]")
        removed = set()
        for pattern in patterns:
            with self.subTest(pattern=pattern):
                # Glob pathspecs may short-circuit malformed patterns as exact
                # literal paths. Git's ignore matcher uses raw wildmatch.
                (self.module.path / ".git/info/exclude").write_text(pattern + "\n")
                oracle = self.module.command("check-ignore", "--no-index", "--stdin", "-z",
                                             data=b"\0".join(os.fsencode(path) for path in paths) + b"\0",
                                             check=False).split(b"\0")
                expected = {os.fsdecode(path) for path in oracle if path}
                if pattern == "x[[:]:]":
                    self.assertEqual({"x[:]", "x::]"}, expected)
                elif pattern in patterns[:4]:
                    self.assertEqual(set(), expected)
                self.assertEqual(expected, {path for path in paths if P.wildmatch(pattern, path)})
                removed.update(expected)
        self.assert_roundtrip(self.document(*[{"kind": "absent-glob", "pattern": pattern} for pattern in patterns]))
        self.assertEqual(paths - removed, set(self.module.git.snapshot("index").items))

    def test_git_wildmatch_bounds_star_work_and_handles_deep_git_paths(self):
        script = ("import sys; sys.path.insert(0, sys.argv[1]); import perimeter; "
                  "assert not perimeter.wildmatch('*a' * 5 + 'b', 'a' * 200); "
                  "assert perimeter.wildmatch('**/x', '/'.join(['a'] * 1200 + ['x'])); "
                  "assert not perimeter.wildmatch('**/y', '/'.join(['a'] * 1200 + ['x']))")
        subprocess.run([sys.executable, "-c", script, str(ROOT / "Github/lib")], check=True, timeout=5, capture_output=True)

    def test_sparse_outside_cone_tracked_content_is_read_and_deleted_without_materializing(self):
        self.module.write("keep/file", "visible\n")
        self.module.write("outside/Directory.Build.rsp", "forbidden\n")
        self.module.write("outside/guide", "module-owned text\n")
        self.module.commit()
        self.module.command("sparse-checkout", "set", "--cone", "--sparse-index", "keep")
        self.assertFalse((self.module.path / "outside/guide").exists())
        self.assert_roundtrip(self.document({"kind": "absent-glob", "pattern": "**/Directory.Build.rsp"},
                                            {"kind": "marker-block", "path": "outside/guide", "text": "owned"}))
        self.assertNotIn("outside/Directory.Build.rsp", self.module.git.snapshot("index").items)
        self.assertTrue(self.module.blob("outside/guide").startswith(b"module-owned text\n"))

    def test_attributes_come_from_selected_index_not_head_or_dirty_worktree(self):
        self.module.write(".gitattributes", "*.txt -text\n")
        self.module.write("guide.txt", "foreign\r\n")
        self.module.commit()
        self.module.write(".gitattributes", "*.txt text eol=lf\n")
        self.module.stage()
        staged_attributes = self.module.git.snapshot("index").items[".gitattributes"]
        self.module.write(".gitattributes", "*.txt filter=poison -text\n")
        self.module.command("config", "filter.poison.clean", "false")
        self.module.command("config", "filter.poison.required", "true")
        self.assert_roundtrip(self.document({"kind": "marker-block", "path": "guide.txt", "text": "owned\r\n"}))
        self.assertNotIn(b"\r", self.module.blob("guide.txt"))
        self.assertEqual(staged_attributes, self.module.git.snapshot("index").items[".gitattributes"])

    def test_selected_index_and_entries_preserve_default_index_and_other_staging(self):
        self.module.write("keep", "original\n")
        self.module.commit()
        default = self.module.git.index_path().read_bytes()
        alternate = self.module.path.parent / "selected-index"
        alternate.write_bytes(default)
        repository = P.Git(self.module.path, index_file=alternate)
        document = self.document({"kind": "marker-block", "path": "first", "text": "first"},
                                 {"kind": "marker-block", "path": "second", "text": "second"})
        self.assert_roundtrip(document, repository=repository, selected={1})
        self.assertEqual(default, self.module.git.index_path().read_bytes())
        self.assertNotIn("first", repository.snapshot("index").items)
        self.assertIn("second", repository.snapshot("index").items)
        with mock.patch.dict(os.environ, {"GIT_INDEX_FILE": str(alternate)}):
            inherited = P.Git(self.module.path)
            self.assertEqual(alternate, inherited.index_path())
            self.assertEqual(repository.snapshot("index").items, inherited.snapshot("index").items)

    def assert_atomic_error(self, document, expression, *, selected=None):
        index = self.module.git.index_path()
        before = index.read_bytes()
        worktree = self.module.tree_bytes()
        with self.assertRaisesRegex((P.PerimeterError, P.config.ConfigError), expression):
            self.apply(document, selected=selected)
        self.assertEqual(before, index.read_bytes())
        self.assertEqual(worktree, self.module.tree_bytes())

    def test_all_source_and_tag_and_selector_failures_preflight_before_mutation(self):
        first = {"kind": "marker-block", "path": "first", "text": "owned"}
        self.assert_atomic_error(self.document(first, {"kind": "whole", "path": "missing"}), "missing expected source")
        self.assert_atomic_error(self.document(first, {"kind": "whole-dir", "path": "missing"}), "missing expected source")
        self.assert_atomic_error(self.document(first, {"kind": "gitlink", "path": "refs/builds", "buildsTag": "v9.9.9"}), "git rev-parse")
        blob = self.builds.command("hash-object", "-w", "--stdin", data=b"not a commit").decode().strip()
        self.builds.command("tag", "v2.0.0", blob)
        self.assert_atomic_error(self.document(first, {"kind": "gitlink", "path": "refs/builds", "buildsTag": "v2.0.0"}), "resolve to a commit")
        for selection in ({3}, {True}, {-1}, [0, 0], ["0"]):
            self.assert_atomic_error(self.document(first), "selected entry|duplicate selected", selected=selection)
        self.assert_atomic_error({"schema": "future", "entries": [first]}, "unsupported schema")

    def test_unmerged_index_rejected_without_any_changes(self):
        oid = self.module.command("hash-object", "-w", "--stdin", data=b"conflict").decode().strip()
        self.module.command("update-index", "--index-info", data=f"100644 {oid} 1\tconflict\n100644 {oid} 2\tconflict\n".encode())
        self.assert_atomic_error(self.document({"kind": "marker-block", "path": "guide", "text": "owned"}), "unmerged index")

    def test_intent_to_add_empty_blob_cannot_report_compliance_or_be_applied(self):
        self.module.write("empty", b"")
        self.module.command("add", "--intent-to-add", "--", "empty")
        self.render.write("rendered/Fixture/empty", b"")
        self.render.commit()
        tree = self.module.command("write-tree").decode().strip()
        self.assertNotIn(b"empty", self.module.command("ls-tree", "--name-only", tree).splitlines())
        document = self.document({"kind": "whole", "path": "empty"})
        before = self.module.git.index_path().read_bytes()
        with self.assertRaisesRegex(P.PerimeterError, "intent-to-add"):
            self.compare(document)
        self.assertEqual(before, self.module.git.index_path().read_bytes())
        self.assert_atomic_error(document, "intent-to-add")

    def test_stale_alternate_index_intent_to_add_rejects_modified_or_deleted_same_path(self):
        self.module.write("empty", b"")
        self.module.command("add", "--intent-to-add", "--", "empty")
        alternate = self.module.path.parent / "intent-index"
        alternate.write_bytes(self.module.git.index_path().read_bytes())
        self.module.write("empty", "committed nonempty content\n")
        self.module.commit()
        repository = P.Git(self.module.path, index_file=alternate)
        tree = repository.run("write-tree").decode().strip()
        self.assertNotIn(b"empty", self.module.command("ls-tree", "--name-only", tree).splitlines())
        self.render.write("rendered/Fixture/empty", b"")
        self.render.commit()
        document = self.document({"kind": "whole", "path": "empty"})
        default_before = self.module.git.index_path().read_bytes()
        alternate_before = alternate.read_bytes()
        worktree_before = self.module.tree_bytes()
        for operation in (self.compare, self.apply):
            with self.subTest(operation=operation.__name__):
                with self.assertRaisesRegex(P.PerimeterError, "intent-to-add"):
                    operation(document, repository=repository)
                self.assertEqual(default_before, self.module.git.index_path().read_bytes())
                self.assertEqual(alternate_before, alternate.read_bytes())
                self.assertEqual(worktree_before, self.module.tree_bytes())

    def test_concurrent_staging_after_preflight_is_rejected_and_preserved(self):
        document = self.document({"kind": "marker-block", "path": "guide", "text": "owned"})
        original_plan = P._Plan
        interleaving = {}

        def plan_then_stage(*arguments, **keywords):
            plan = original_plan(*arguments, **keywords)
            self.module.write("concurrent", "new staging\n")
            self.module.command("add", "--", "concurrent")
            interleaving["index"] = self.module.git.index_path().read_bytes()
            interleaving["item"] = self.module.git.snapshot("index").items["concurrent"]
            return plan

        with mock.patch.object(P, "_Plan", side_effect=plan_then_stage):
            with self.assertRaisesRegex(P.PerimeterError, "index changed during perimeter preflight"):
                self.apply(document)
        self.assertEqual(interleaving["index"], self.module.git.index_path().read_bytes())
        snapshot = self.module.git.snapshot("index")
        self.assertEqual(interleaving["item"], snapshot.items["concurrent"])
        self.assertNotIn("guide", snapshot.items)

    def test_conflicting_ownership_and_file_directory_repairs_fail_atomically(self):
        self.assert_atomic_error(self.document({"kind": "whole-dir", "path": "owned"},
                                               {"kind": "whole", "path": "owned/child"}), "ownership overlap")
        self.assert_atomic_error(self.document({"kind": "marker-block", "path": "guide", "text": "owned"},
                                               {"kind": "absent-glob", "pattern": "gui*"}), "conflicting ownership")
        self.assert_atomic_error(self.document({"kind": "json-superset", "path": "settings", "pointer": "/permissions/deny", "values": ["owned"]},
                                               {"kind": "absent-json", "path": "settings", "selectors": [["permissions", "*"]]}), "conflicting ownership")
        self.module.write("guide/foreign", "keep\n")
        self.module.commit()
        self.assert_atomic_error(self.document({"kind": "marker-block", "path": "guide", "text": "owned"}), "unselected path")
        self.render.write("rendered/Fixture/guide/expected", "owned\n")
        self.render.commit()
        self.module.write("ancestor", "foreign ancestor\n")
        self.module.stage()
        self.assert_atomic_error(self.document({"kind": "marker-block", "path": "ancestor/child", "text": "owned"}), "unselected path")
        self.assert_roundtrip(self.document({"kind": "whole-dir", "path": "guide"}))

    def test_absent_glob_cannot_delete_a_compliant_whole_directory_member(self):
        self.module.write("owned/file.txt", "expected\n")
        self.module.commit()
        self.render.write("rendered/Fixture/owned/file.txt", "expected\n")
        self.render.commit()
        document = self.document({"kind": "whole-dir", "path": "owned"},
                                 {"kind": "absent-glob", "pattern": "owned/*.txt"})
        self.assert_atomic_error(document, "conflicting ownership")
        self.assert_atomic_error(document, "conflicting ownership", selected={1})

    def test_literal_special_paths_use_nul_delimited_deletions(self):
        names = [".releaserc $ # (x)", ".releaserc:literal", ".releaserc\u2028line"]
        for name in names:
            self.module.write(name, "forbidden\n")
        self.module.commit()
        self.assert_roundtrip(self.document({"kind": "absent-glob", "pattern": ".releaserc*"}))
        self.assertFalse(any(name in self.module.git.snapshot("index").items for name in names))

    def test_hash_filter_failure_and_index_lock_preserve_the_original_index(self):
        self.module.write(".gitattributes", "second filter=fail\n")
        self.module.command("config", "filter.fail.clean", "false")
        self.module.command("config", "filter.fail.required", "true")
        self.module.commit()
        document = self.document({"kind": "marker-block", "path": "first", "text": "first"},
                                 {"kind": "marker-block", "path": "second", "text": "second"})
        self.assert_atomic_error(document, "git hash-object")
        lock = Path(str(self.module.git.index_path()) + ".lock")
        lock.write_text("other writer")
        self.addCleanup(lambda: lock.unlink(missing_ok=True))
        before = self.module.git.index_path().read_bytes()
        with self.assertRaises(FileExistsError):
            self.apply(document)
        self.assertEqual("other writer", lock.read_text())
        self.assertEqual(before, self.module.git.index_path().read_bytes())

    def test_successful_filter_that_corrupts_owned_content_is_an_atomic_failure(self):
        self.module.write(".gitattributes", "guide filter=uppercase\n")
        self.module.command("config", "filter.uppercase.clean", "tr a-z A-Z")
        self.module.commit()
        self.assert_atomic_error(self.document({"kind": "marker-block", "path": "guide", "text": "owned"}),
                                 "attributes prevent a compliant")

    def test_successful_filter_changing_only_foreign_marker_or_json_data_is_refused(self):
        self.module.write(".gitattributes", "* -text filter=rewrite\n")
        self.module.write("guide", "foreign-before\n")
        self.module.write("settings", '{"foreign":"foreign-before","deny":[]}')
        self.module.commit()
        self.module.command("config", "filter.rewrite.clean", "sed s/foreign-before/foreign-after/g")
        for entry in ({"kind": "marker-block", "path": "guide", "text": "owned"},
                      {"kind": "json-superset", "path": "settings", "pointer": "/deny", "values": ["owned"]}):
            with self.subTest(kind=entry["kind"]):
                self.assert_atomic_error(self.document(entry), "without changing proposed content")


if __name__ == "__main__":
    unittest.main()
