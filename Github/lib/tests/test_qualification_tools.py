"""Exercise isolated qualification installation with authenticated transport doubles."""

from __future__ import annotations

import copy
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import qualification_tools as tools


def digest(data):
    return hashlib.sha256(data).hexdigest()


def tar_bytes(entries):
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
        for name, content, kind in entries:
            entry = tarfile.TarInfo(name)
            entry.type = kind
            entry.size = len(content) if kind == tarfile.REGTYPE else 0
            if kind == tarfile.SYMTYPE:
                entry.linkname = "../outside"
            archive.addfile(entry, io.BytesIO(content) if kind == tarfile.REGTYPE else None)
    return buffer.getvalue()


class QualificationToolTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.parent = Path(self.scratch.name)
        self.root = self.parent / "module"
        self.root.mkdir()
        self.binary = b"verified executable"
        self.archive = tar_bytes([("daprd", self.binary, tarfile.REGTYPE)])
        self.row = {"name": "daprd", "url": "https://github.com/dapr/dapr/releases/download/v1.18.4/daprd_linux_amd64.tar.gz",
                    "sha256": digest(self.archive), "size": len(self.archive), "format": "tar.gz",
                    "member": "daprd", "binarySha256": digest(self.binary)}
        self.document = {"schema": "hexalith.qualification-tools.v1", "artifacts": [self.row]}
        self.write()

    def write(self):
        (self.root / "tools.json").write_text(json.dumps(self.document))

    def install(self, transport=None):
        self.write()
        return tools.stage(self.root, "tools.json", temporary=self.parent,
                           transport=transport or (lambda url, size: self.archive))

    def test_verified_executable_and_raw_archive_are_private_and_distinct(self):
        raw = {key: value for key, value in self.row.items() if key not in ("member", "binarySha256")}
        raw.update(name="original.tar.gz", format="file")
        self.document["artifacts"].append(raw)
        first = self.install()
        second = self.install()
        self.assertNotEqual(first["directory"], second["directory"])
        paths = json.loads(first["files"])
        binary, archive = (Path(paths[name]) for name in ("daprd", "original.tar.gz"))
        self.assertEqual(self.binary, binary.read_bytes())
        self.assertEqual(self.archive, archive.read_bytes())
        self.assertEqual(0o500, binary.stat().st_mode & 0o777)
        self.assertEqual(0o400, archive.stat().st_mode & 0o777)
        self.assertEqual(0o700, Path(first["directory"]).stat().st_mode & 0o777)
        receipt = json.loads(Path(first["receipt"]).read_text())
        self.assertEqual("not-established", receipt["qualification"])
        self.assertEqual(self.row["binarySha256"], receipt["artifacts"][0]["installedSha256"])
        self.assertEqual(["tools.json"], sorted(path.name for path in self.root.iterdir()))

    def test_bad_size_or_archive_or_executable_digest_leaves_no_partial_directory(self):
        for field, value in (("size", len(self.archive) + 1), ("sha256", "0" * 64), ("binarySha256", "0" * 64)):
            with self.subTest(field=field):
                original = copy.deepcopy(self.row)
                self.row[field] = value
                with self.assertRaises(tools.PipelineError):
                    self.install()
                self.assertEqual(["module"], sorted(path.name for path in self.parent.iterdir()))
                self.row.clear()
                self.row.update(original)

    def test_later_transport_failure_does_not_publish_first_tool(self):
        second = copy.deepcopy(self.row)
        second["name"] = "second"
        self.document["artifacts"].append(second)
        calls = []

        def transport(url, size):
            calls.append(url)
            if len(calls) == 2:
                raise tools.PipelineError("transport unavailable")
            return self.archive

        with self.assertRaisesRegex(tools.PipelineError, "transport unavailable"):
            self.install(transport)
        self.assertEqual(["module"], sorted(path.name for path in self.parent.iterdir()))

    def test_unsafe_and_ambiguous_archive_entries_never_escape(self):
        for entries in (
            [("daprd", b"", tarfile.SYMTYPE)],
            [("daprd", self.binary, tarfile.REGTYPE), ("../outside", b"bad", tarfile.REGTYPE)],
            [("daprd", self.binary, tarfile.REGTYPE)] * 2,
        ):
            with self.subTest(entries=entries):
                self.archive = tar_bytes(entries)
                self.row.update(sha256=digest(self.archive), size=len(self.archive))
                with self.assertRaisesRegex(tools.PipelineError, "unsafe or ambiguous"):
                    self.install()
                self.assertFalse((self.parent / "outside").exists())

    def test_invalid_manifest_fails_before_transport(self):
        for change in (
            {"url": "https://example.invalid/daprd"},
            {"url": "https://token@github.com/dapr/dapr/releases/download/v1.18.4/daprd.tar.gz"},
            {"url": self.row["url"] + "?token=credential"},
            {"url": "https://github.com/dapr/dapr/releases/download/latest/daprd.tar.gz"},
            {"name": "../escape"}, {"size": True}, {"sha256": "unknown"},
            {"unexpected": "configuration"},
        ):
            with self.subTest(change=change):
                original = copy.deepcopy(self.row)
                self.row.update(change)
                with self.assertRaises(tools.PipelineError):
                    self.install(lambda *_: self.fail("transport must not run"))
                self.row.clear()
                self.row.update(original)

    def test_duplicate_keys_names_and_symlink_manifest_are_rejected(self):
        self.document["artifacts"].append(copy.deepcopy(self.row))
        with self.assertRaisesRegex(tools.PipelineError, "duplicate"):
            self.install()
        path = self.root / "tools.json"
        path.write_text('{"schema":"hexalith.qualification-tools.v1","schema":"duplicate","artifacts":[]}')
        with self.assertRaises(tools.ConfigError):
            tools.read_manifest(self.root, "tools.json")
        path.unlink()
        source = self.parent / "outside.json"
        source.write_text(json.dumps(self.document))
        path.symlink_to(source)
        with self.assertRaises(tools.PipelineError):
            tools.read_manifest(self.root, "tools.json")

    def test_module_directory_cannot_be_used_for_tool_outputs(self):
        with self.assertRaisesRegex(tools.PipelineError, "outside the module"):
            tools.stage(self.root, "tools.json", temporary=self.root,
                        transport=lambda *_: self.fail("transport must not run"))
        self.assertEqual(["tools.json"], sorted(path.name for path in self.root.iterdir()))

    def test_cli_refusal_exits_nonzero_without_success_outputs(self):
        self.row["url"] = "https://example.invalid/daprd"
        self.write()
        output = self.parent / "outputs"
        result = subprocess.run([sys.executable, "-I", str(Path(tools.__file__)), "--workspace", str(self.root),
                                 "--manifest", "tools.json"], capture_output=True, text=True,
                                env=dict(os.environ, GITHUB_OUTPUT=str(output)))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("official HTTPS release URL", result.stderr)
        self.assertNotIn("Traceback", result.stderr)
        self.assertFalse(output.exists())

    def test_transport_has_a_total_timeout_size_limit_and_https_only_redirects(self):
        result = subprocess.CompletedProcess([], 0, stdout=self.archive, stderr=b"")
        with mock.patch.object(tools.subprocess, "run", return_value=result) as process:
            self.assertEqual(self.archive, tools.download(self.row["url"], len(self.archive)))
        arguments = process.call_args.args[0]
        for flag, value in (("--max-time", "90"), ("--max-filesize", str(len(self.archive))),
                            ("--proto", "=https"), ("--proto-redir", "=https")):
            self.assertEqual(value, arguments[arguments.index(flag) + 1])
        self.assertEqual(100, process.call_args.kwargs["timeout"])
        with mock.patch.object(tools.subprocess, "run", side_effect=subprocess.TimeoutExpired("curl", 100)):
            with self.assertRaisesRegex(tools.PipelineError, "download failed"):
                tools.download(self.row["url"], len(self.archive))

    def test_published_schema_accepts_the_manifest_and_rejects_the_same_unsafe_shapes(self):
        from jsonschema import Draft202012Validator
        schema = json.loads((Path(tools.__file__).parents[2] / "schemas/hexalith.qualification-tools.v1.json").read_text())
        validator = Draft202012Validator(schema)
        validator.validate(self.document)
        for change in ({"url": "https://example.invalid/tool"}, {"size": True},
                       {"member": "../escape"}, {"format": "unknown"}, {"extra": True}):
            invalid = copy.deepcopy(self.document)
            invalid["artifacts"][0].update(change)
            self.assertFalse(validator.is_valid(invalid), change)


if __name__ == "__main__":
    unittest.main()
