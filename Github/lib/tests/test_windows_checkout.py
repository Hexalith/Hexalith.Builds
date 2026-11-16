"""Exercise protected checkout against real local Git objects, without remotes."""

from __future__ import annotations

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
SPEC = importlib.util.spec_from_file_location("prepare_windows_checkout", ROOT / "Github/prepare/prepare_windows_checkout.py")
CHECKOUT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = CHECKOUT
SPEC.loader.exec_module(CHECKOUT)


class ProtectedCheckoutTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="protected-checkout-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.source.mkdir()
        self.git("init", "-q")
        self.git("config", "user.email", "qualification@example.invalid")
        self.git("config", "user.name", "Checkout qualification")

    def git(self, *args, root=None, data=None):
        result = subprocess.run(["git", "-C", str(root or self.source), *args],
                                input=data, capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace"))
        return result.stdout

    def write(self, path, contents="source\n"):
        target = self.source / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(contents, encoding="utf-8")

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "qualification input")
        return self.git("rev-parse", "HEAD").decode().strip()

    def clone(self):
        destination = self.root / "checkout"
        self.git("clone", "--no-checkout", "--no-hardlinks", str(self.source), str(destination))
        return CHECKOUT.Git(destination)

    def configuration(self, *paths):
        return {"exclude": [{"path": path, "tree": self.git("rev-parse", "HEAD:" + path).decode().strip()}
                            for path in paths]}

    def test_exact_source_fetch_retains_real_ancestor_history(self):
        self.write("ancestor.txt", "authentic ancestor bytes\n")
        ancestor = self.commit()
        self.write("source.txt")
        revision = self.commit()
        destination = self.root / "fresh"
        destination.mkdir()
        self.git("init", "-q", root=destination)
        self.git("remote", "add", "origin", str(self.source), root=destination)
        owner = self

        class LocalTransport(CHECKOUT.Git):
            def run(self, *arguments, **kwargs):
                if arguments[0] == "fetch":
                    owner.assertFalse(any(value.startswith("--depth") for value in arguments))
                    return owner.git(*arguments, root=self.root)
                return super().run(*arguments, **kwargs)

        git = LocalTransport(destination)
        CHECKOUT.fetch_source(git, revision)
        self.assertEqual(b"false\n", git.run("rev-parse", "--is-shallow-repository"))
        self.assertEqual(b"2\n", git.run("rev-list", "--count", revision))
        self.assertEqual(b"authentic ancestor bytes\n", git.run("show", ancestor + ":ancestor.txt"))
        git.run("merge-base", "--is-ancestor", ancestor, revision)
        with mock.patch.object(git, "run", side_effect=[b"", b"true\n"]), self.assertRaisesRegex(CHECKOUT.ConfigError, "complete ancestor"):
            CHECKOUT.fetch_source(git, revision)

    def test_real_sparse_checkout_preserves_ancestor_files_and_multiple_archives(self):
        for path in ("root.txt", "src/code.txt", "history/parent.txt", "history/a/cache/bad:name",
                     "history/a/keep.txt", "history/b/cache/other:bytes", "safe[dir]/code.txt"):
            self.write(path)
        revision = self.commit()
        config = self.configuration("history/a/cache", "history/b/cache")
        git = self.clone()
        leaves = CHECKOUT.materialize(git, revision, config, self.root)
        self.assertEqual(7, len(leaves))
        for path in ("root.txt", "src/code.txt", "history/parent.txt", "history/a/keep.txt", "safe[dir]/code.txt"):
            self.assertEqual("source\n", (git.root / path).read_text())
        self.assertFalse((git.root / "history/a/cache").exists())
        self.assertFalse((git.root / "history/b/cache").exists())
        self.assertEqual(b"", git.run("status", "--porcelain"))
        self.assertEqual(git.run("rev-parse", "HEAD^{tree}"), git.run("write-tree"))
        self.assertFalse((self.root / "protected-index").exists())

    def test_root_only_and_split_ancestor_only_cones_need_no_sentinel(self):
        for paths, exclusion in ((("root.txt", "archive/bad:name"), "archive"),
                                  (("root.txt", "only/file.txt", "only/archive/bad:name"), "only/archive")):
            with self.subTest(exclusion=exclusion), tempfile.TemporaryDirectory(dir=self.root) as temporary:
                original = self.source
                self.source = Path(temporary) / "source"
                self.source.mkdir()
                self.git("init", "-q")
                self.git("config", "user.name", "Qualification")
                self.git("config", "user.email", "qualification@example.invalid")
                for path in paths:
                    self.write(path)
                revision = self.commit()
                config = self.configuration(exclusion)
                destination = Path(temporary) / "checkout"
                self.git("clone", "--no-checkout", str(self.source), str(destination))
                git = CHECKOUT.Git(destination)
                CHECKOUT.materialize(git, revision, config, Path(temporary))
                self.assertEqual(set(paths) - {paths[-1]},
                                 {path.relative_to(destination).as_posix() for path in destination.rglob("*")
                                  if path.is_file() and ".git" not in path.relative_to(destination).parts})
                self.source = original

    def test_changed_tree_unavailable_pin_and_protected_paths_fail_before_checkout(self):
        self.write("source/code.txt")
        self.write("archive/bad:name")
        revision = self.commit()
        git = self.clone()
        config = self.configuration("archive")
        for pin, candidate, protected in (("0" * 40, config, ()),
                                           (revision, {"exclude": [{"path": "archive", "tree": "0" * 40}]}, ()),
                                           (revision, config, ("archive/product.csproj",))):
            with self.subTest(pin=pin, candidate=candidate):
                with self.assertRaises(CHECKOUT.ConfigError):
                    CHECKOUT.materialize(git, pin, candidate, self.root, protected=protected)
                self.assertFalse((git.root / "source/code.txt").exists())

    def test_archive_symlinks_and_gitlinks_are_rejected(self):
        self.write("source.txt")
        self.write("archive/safe.txt")
        (self.source / "archive/link").symlink_to("safe.txt")
        revision = self.commit()
        git = self.clone()
        with self.assertRaisesRegex(CHECKOUT.ConfigError, "symbolic link or gitlink"):
            CHECKOUT.materialize(git, revision, self.configuration("archive"), self.root)
        (self.source / "archive/link").unlink()
        self.git("add", "-u")
        self.git("update-index", "--add", "--cacheinfo", "160000," + revision + ",archive/dependency")
        self.git("commit", "-qm", "qualification gitlink")
        config = self.configuration("archive")
        # Qualification uses a second independent never-checked-out repository.
        destination = self.root / "second"
        self.git("clone", "--no-checkout", str(self.source), str(destination))
        revision = self.git("rev-parse", "HEAD").decode().strip()
        with self.assertRaisesRegex(CHECKOUT.ConfigError, "symbolic link or gitlink"):
            CHECKOUT.materialize(CHECKOUT.Git(destination), revision, config, self.root)

    def test_unsafe_materialized_source_case_and_prefix_aliases_fail(self):
        for paths in (("unsafe:name",), ("Dir/a", "dir/b"), ("Dir", "dir/b"), ("NUL.txt",), ("trailing./file",)):
            with self.subTest(paths=paths):
                entries = tuple(CHECKOUT.Entry(path, "100644", "1" * 40) for path in paths)
                with self.assertRaises(CHECKOUT.ConfigError):
                    CHECKOUT.validate_complement(entries)

    def test_closed_json_paths_overlap_and_aliases(self):
        valid = {"exclude": [{"path": "history/cache", "tree": "1" * 40}]}
        CHECKOUT.validate_windows_checkout(valid)
        invalid = ([], {}, {"exclude": []}, dict(valid, extra=True),
                   {"exclude": [{"path": "../cache", "tree": "1" * 40}]},
                   {"exclude": [{"path": "cache", "tree": "A" * 40}]},
                   {"exclude": [{"path": "cache", "tree": "1" * 40, "extra": True}]},
                   {"exclude": valid["exclude"] + [{"path": "History/CACHE", "tree": "1" * 40}]},
                   {"exclude": valid["exclude"] + [{"path": "history", "tree": "1" * 40}]})
        for candidate in invalid:
            with self.subTest(candidate=candidate), self.assertRaises(CHECKOUT.ConfigError):
                CHECKOUT.validate_windows_checkout(candidate)
        for path in ("C:/cache", "cache\\dir", "cache/", "cache\n", "cache/${{ inputs.x }}", "COM1", ".git"):
            with self.subTest(path=path), self.assertRaises(CHECKOUT.ConfigError):
                CHECKOUT.validate_windows_checkout({"exclude": [{"path": path, "tree": "1" * 40}]})

    def test_native_production_entry_refuses_non_windows_before_any_git_or_sdk(self):
        if os.name == "nt":
            return  # The native path is qualified by its actual checkout tests.
        with mock.patch.object(CHECKOUT.subprocess, "run") as child:
            self.assertEqual(1, CHECKOUT.main())
        child.assert_not_called()

    def test_malformed_tree_and_index_metadata_is_never_guessed(self):
        for payload, index in ((b"100644 blob " + b"1" * 40 + b"\tfile", False),
                               (b"100644 " + b"1" * 40 + b" 1\tfile\0", True),
                               (b"100644 blob " + b"1" * 40 + b"\t../file\0", False),
                               (b"100644 blob " + b"1" * 40 + b"\tbad\xff\0", False)):
            with self.subTest(payload=payload), self.assertRaises(CHECKOUT.ConfigError):
                CHECKOUT.records(payload, index=index)

    def test_git_always_overrides_ambient_protection_and_does_not_persist_credentials(self):
        git = CHECKOUT.Git(self.source)
        with mock.patch.object(CHECKOUT.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, b"", b"")) as run:
            git.run("status")
        args = run.call_args.args[0]
        self.assertIn("core.protectNTFS=true", args)
        self.assertNotIn("core.protectNTFS=false", args)
        self.assertIn("credential.helper=", args)
        self.assertIn("submodule.recurse=false", args)
        self.assertNotIn("GITHUB_TOKEN", run.call_args.kwargs["env"])


if __name__ == "__main__":
    unittest.main()
