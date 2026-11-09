"""Exercise download integrity, exact finding baselines and linter process failures."""

from __future__ import annotations

import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "lint_workflows.py"
SPEC = importlib.util.spec_from_file_location("quality_lint_workflows", SCRIPT)
LINT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LINT)


class LintTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="builds-lint-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.cache = self.root / "cache"

    def archive(self, members):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w:gz") as archive:
            for name, data in members:
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                archive.addfile(entry, io.BytesIO(data))
        return stream.getvalue()

    def pin(self, data):
        return {"url": "https://tools.example.invalid/tool.tar.gz", "version": "1.0.0",
                "sha256": hashlib.sha256(data).hexdigest()}

    def install_cached(self, data, pin=None):
        self.cache.mkdir(exist_ok=True)
        (self.cache / "fixture-1.0.0.tar.gz").write_bytes(data)
        return LINT.install_tool("fixture", pin or self.pin(data), self.cache)

    def test_verified_archive_installs_and_replaces_tampered_binary(self):
        payload = b"#!/bin/sh\nprintf verified\\n\n"
        data = self.archive([("nested/fixture", payload)])
        binary = self.install_cached(data)
        self.assertEqual(payload, binary.read_bytes())
        self.assertTrue(binary.stat().st_mode & 0o111)
        binary.write_bytes(b"tampered executable")
        binary = self.install_cached(data)
        self.assertEqual(payload, binary.read_bytes())

    def test_reinstall_replaces_running_binary_without_interrupting_it(self):
        # A real executable holds its inode open: writing directly to it fails
        # with ETXTBSY on Linux, unlike a shell script read by another interpreter.
        payload = Path(sys.executable).resolve().read_bytes()
        data = self.archive([("fixture", payload)])
        binary = self.install_cached(data)
        environment = dict(os.environ, PYTHONHOME=sys.base_prefix)
        process = subprocess.Popen([
            str(binary), "-c",
            "import sys; print('ready', flush=True); sys.stdin.readline(); print('still running')",
        ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            env=environment)
        try:
            self.assertEqual("ready\n", process.stdout.readline())
            original_inode = binary.stat().st_ino
            self.install_cached(data)
            self.assertNotEqual(original_inode, binary.stat().st_ino)
            self.assertEqual(payload, binary.read_bytes())
            self.assertIsNone(process.poll())
            output, errors = process.communicate("continue\n", timeout=10)
            self.assertEqual(0, process.returncode, errors)
            self.assertEqual("still running\n", output)
            replacement = subprocess.run([str(binary), "-c", "print('replacement executes')"],
                                         capture_output=True, text=True, check=True, timeout=10,
                                         env=environment)
            self.assertEqual("replacement executes\n", replacement.stdout)
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()

    def test_tampered_cached_archive_fails_integrity_before_extraction(self):
        original = self.archive([("fixture", b"verified")])
        with self.assertRaisesRegex(ValueError, "SHA256 integrity check failed"):
            self.install_cached(original + b"tamper", self.pin(original))
        self.assertFalse((self.cache / "fixture").exists())

    def test_fresh_download_requires_integrity_and_https_redirect(self):
        data = self.archive([("fixture", b"verified")])

        class Response(io.BytesIO):
            url = "https://cdn.example.invalid/tool.tar.gz"

        with patch.object(LINT.urllib.request, "urlopen", return_value=Response(data)) as download:
            binary = LINT.install_tool("fixture", self.pin(data), self.cache)
        self.assertEqual(b"verified", binary.read_bytes())
        download.assert_called_once_with("https://tools.example.invalid/tool.tar.gz", timeout=120)
        self.cache.joinpath("fixture-1.0.0.tar.gz").unlink()
        with patch.object(LINT.urllib.request, "urlopen", return_value=Response(data + b"tamper")):
            with self.assertRaisesRegex(ValueError, "integrity"):
                LINT.install_tool("fixture", self.pin(data), self.cache)
        response = Response(data)
        response.url = "http://untrusted.example.invalid/tool.tar.gz"
        with patch.object(LINT.urllib.request, "urlopen", return_value=response):
            with self.assertRaisesRegex(ValueError, "non-HTTPS"):
                LINT.install_tool("fixture", self.pin(data), self.cache)

    def test_insecure_url_and_missing_or_duplicate_executable_fail(self):
        data = self.archive([("fixture", b"verified")])
        with self.assertRaisesRegex(ValueError, "must use HTTPS"):
            self.install_cached(data, self.pin(data) | {"url": "http://example.invalid/tool"})
        for members in ([('other', b"missing")], [('one/fixture', b"one"), ('two/fixture', b"two")]):
            with self.subTest(members=members):
                with self.assertRaisesRegex(ValueError, "exactly one executable"):
                    self.install_cached(self.archive(members))

    def finding(self, path="Github/frozen/action.yml", rule="unpinned-uses", row=7):
        return {"ident": rule, "desc": "synthetic unpinned action", "ignored": False,
                "locations": [{"symbolic": {"key": {"Local": {"verbatim_path": str(self.root / path)}}},
                               "concrete": {"location": {"start_point": {"row": row, "column": 3},
                                                            "end_point": {"row": row, "column": 45}},
                                            "feature": "uses: owner/action@v1"}}]}

    def baseline(self, finding):
        return {"version": 1, "findings": [LINT.finding_key(finding, self.root) | {
            "rationale": "Frozen runtime bytes cannot change before the release rewrite"}]}

    def test_only_exact_rule_path_location_and_feature_are_baselined(self):
        original = self.finding()
        baseline = self.baseline(original)
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, LINT.check_findings([original], baseline, self.root))
            for change in (self.finding(path="Github/new/action.yml"), self.finding(rule="template-injection"),
                           self.finding(row=8)):
                self.assertEqual(1, LINT.check_findings([change], baseline, self.root))
            changed = copy.deepcopy(original)
            changed["locations"][0]["concrete"]["feature"] = "uses: attacker/action@main"
            self.assertEqual(1, LINT.check_findings([changed], baseline, self.root))

    def test_baseline_cannot_hide_workflows_or_omit_rationale(self):
        for finding, error in ((self.finding(path=".github/workflows/frozen.yml"), "Only frozen composite"),
                               (self.finding(path="Github/frozen/action.yml"), "rationale")):
            baseline = self.baseline(finding)
            if error == "rationale":
                baseline["findings"][0].pop("rationale")
            with self.assertRaisesRegex(ValueError, error):
                LINT.check_findings([finding], baseline, self.root)

    def setup_linters(self, *, actionlint_code=0, zizmor_code=0, findings=None, output=None):
        workflows = self.root / ".github/workflows"
        workflows.mkdir(parents=True, exist_ok=True)
        (workflows / "one.yml").write_text("name: one\non: push\njobs: {}\n")
        (workflows / "two.yaml").write_text("name: two\non: push\njobs: {}\n")
        action = self.root / "Github/frozen/action.yml"
        action.parent.mkdir(parents=True, exist_ok=True)
        action.write_text("name: fixture\nruns: {using: composite, steps: []}\n")
        log = self.root / "tool-arguments.jsonl"
        tools = {}
        for name, code in (("actionlint", actionlint_code), ("zizmor", zizmor_code)):
            path = self.root / name
            body = ("#!/usr/bin/env python3\nimport json,sys\n"
                    f"with open({str(log)!r}, 'a') as log: log.write(json.dumps(sys.argv) + '\\n')\n")
            if name == "zizmor":
                body += f"print({(output if output is not None else json.dumps(findings or []))!r})\n"
            body += f"raise SystemExit({code})\n"
            path.write_text(body)
            path.chmod(0o755)
            tools[name] = path
        return tools, log

    def scan(self, tools, baseline=None):
        with patch.dict(os.environ, {"GH_TOKEN": "synthetic-test-token"}, clear=True), \
             contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return LINT.run_linters(self.root, tools, baseline or {"version": 1, "findings": []})

    def test_scan_passes_all_required_surfaces_and_keeps_online_audits_enabled(self):
        tools, log = self.setup_linters()
        actions = [self.root / directory / f"sample-{suffix}" / f"action.{suffix}"
                   for directory in ("Github", ".github/actions") for suffix in ("yml", "yaml")]
        for action in actions:
            action.parent.mkdir(parents=True)
            action.write_text("name: fixture\nruns: {using: composite, steps: []}\n")
        self.scan(tools)
        invocations = [json.loads(line) for line in log.read_text().splitlines()]
        self.assertEqual(2, len(invocations))
        for path in (".github/workflows/one.yml", ".github/workflows/two.yaml"):
            self.assertIn(str(self.root / path), invocations[0])
            self.assertIn(str(self.root / path), invocations[1])
        self.assertIn(str(self.root / "Github/frozen/action.yml"), invocations[1])
        for action in actions:
            self.assertIn(str(action), invocations[1])
        self.assertIn("--strict-collection", invocations[1])
        self.assertNotIn("--offline", invocations[1])
        self.assertNotIn("--no-online-audits", invocations[1])

    def test_actionlint_process_failure_stops_scan(self):
        tools, log = self.setup_linters(actionlint_code=4)
        with self.assertRaises(subprocess.CalledProcessError):
            self.scan(tools)
        self.assertEqual(1, len(log.read_text().splitlines()))

    def test_zizmor_process_failure_and_invalid_json_are_not_swallowed(self):
        for code, output, exception in ((1, "[]", RuntimeError), (14, "bad JSON", ValueError)):
            with self.subTest(code=code, output=output):
                tools, _ = self.setup_linters(zizmor_code=code, output=output)
                with self.assertRaises(exception):
                    self.scan(tools)

    def test_new_finding_fails_and_exact_frozen_finding_passes_severity_exit(self):
        finding = self.finding()
        tools, _ = self.setup_linters(zizmor_code=14, findings=[finding])
        with self.assertRaisesRegex(RuntimeError, "New zizmor findings"):
            self.scan(tools)
        self.scan(tools, self.baseline(finding))

    def test_missing_token_and_offline_environment_refuse_scan(self):
        tools, _ = self.setup_linters()
        for environment, diagnostic in (({}, "require GH_TOKEN"),
                                        ({"GH_TOKEN": "test", "ZIZMOR_OFFLINE": "true"}, "Online zizmor audits are required"),
                                        ({"GH_TOKEN": "test", "ZIZMOR_NO_ONLINE_AUDITS": "true"}, "Online zizmor audits are required")):
            with self.subTest(environment=environment), patch.dict(os.environ, environment, clear=True):
                with self.assertRaisesRegex(ValueError, diagnostic):
                    LINT.run_linters(self.root, tools, {"version": 1, "findings": []})


class RealPolicyTests(unittest.TestCase):
    """Exercise production policies with the already installed, verified tools."""

    @classmethod
    def setUpClass(cls):
        pins = json.loads((LINT.HERE / "tools.json").read_text())
        cache = Path(tempfile.gettempdir()) / "hexalith-builds-ci-tools"
        cls.binaries = {}
        for name in ("actionlint", "zizmor"):
            pin = pins[name]
            binary = cache / name
            archive_path = cache / f"{name}-{pin['version']}.tar.gz"
            if not binary.is_file() or not archive_path.is_file():
                raise AssertionError("Run python3 Tools/ci/lint_workflows.py --install-only before policy tests")
            data = archive_path.read_bytes()
            if hashlib.sha256(data).hexdigest() != pin["sha256"]:
                raise AssertionError(f"{name}: installed archive does not match its committed SHA256")
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
                members = [item for item in archive.getmembers() if item.isfile() and Path(item.name).name == name]
                if len(members) != 1 or archive.extractfile(members[0]).read() != binary.read_bytes():
                    raise AssertionError(f"{name}: executable differs from the verified pinned archive")
            cls.binaries[name] = binary

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="builds-real-policy-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def test_real_zizmor_enforces_committed_third_party_pinning_policy_online(self):
        self.assertTrue(any(os.environ.get(key) for key in ("GH_TOKEN", "GITHUB_TOKEN", "ZIZMOR_GITHUB_TOKEN")),
                        "Real online policy tests require a read-only GitHub token")
        self.assertFalse(any(os.environ.get(key) for key in ("ZIZMOR_OFFLINE", "ZIZMOR_NO_ONLINE_AUDITS")),
                         "Real policy tests require online audits")
        workflow = self.root / "production-policy.yml"
        template = ("name: Synthetic production policy\non: push\npermissions: {contents: read}\n"
                    "jobs:\n  check:\n    runs-on: ubuntu-latest\n    steps:\n"
                    "      - uses: actions/checkout@REFERENCE\n"
                    "        with: {persist-credentials: false}\n")
        # This official checkout v7.0.1 commit is checked by zizmor's online audits.
        for reference, unpinned in (("v7.0.1", True), ("3d3c42e5aac5ba805825da76410c181273ba90b1", False)):
            with self.subTest(reference=reference):
                workflow.write_text(template.replace("REFERENCE", reference))
                result = subprocess.run([
                    str(self.binaries["zizmor"]), "--strict-collection", "--no-progress", "--format=json",
                    "--config", str(LINT.ROOT / ".github/zizmor.yml"), str(workflow),
                ], capture_output=True, text=True, check=False, timeout=120)
                self.assertIn(result.returncode, (0, 11, 12, 13, 14), result.stderr)
                findings = [finding for finding in json.loads(result.stdout) if not finding["ignored"]]
                self.assertEqual(unpinned, any(finding["ident"] == "unpinned-uses" for finding in findings),
                                 result.stdout + result.stderr)
                if not unpinned:
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertEqual([], findings)

    def test_real_actionlint_allows_native_cache_mode_and_rejects_unknown_action_input(self):
        template = ("name: Synthetic future workflow\non: push\ncache-mode: none\n"
                    "jobs:\n  check:\n    runs-on: ubuntu-latest\n    steps:\n"
                    "      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1\n"
                    "        env:\n          IDENTITY: ${{ job.workflow_sha }}\n")
        command = [str(self.binaries["actionlint"]), "-shellcheck=", "-pyflakes=",
                   "-config-file", str(LINT.ROOT / ".github/actionlint.yaml"),
                   "-stdin-filename", ".github/workflows/future-quality.yaml", "-"]
        accepted = subprocess.run(command, cwd=LINT.ROOT, input=template,
                                  capture_output=True, text=True, check=False, timeout=30)
        self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)
        unsupported_input = template.replace("cache-mode: none\n", "").replace(
            "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v4")
        rejected = subprocess.run(command, cwd=LINT.ROOT, input=unsupported_input + "        with:\n          cache-mode: none\n",
                                  capture_output=True, text=True, check=False, timeout=30)
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn('input "cache-mode" is not defined in action', rejected.stdout + rejected.stderr)


if __name__ == "__main__":
    unittest.main()
