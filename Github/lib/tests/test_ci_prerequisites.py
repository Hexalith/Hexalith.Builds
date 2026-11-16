"""Shared prerequisite validation and side effects, without network or host mutation."""

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

import test_ci_environment as FIXTURE


ENV = FIXTURE.ENV
ROOT = FIXTURE.ROOT
SPEC = importlib.util.spec_from_file_location("_prerequisite_prepare", ROOT / "Github/prepare/prepare_linux.py")
PREPARE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREPARE)


class PrerequisiteTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ci-prerequisites-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "Fixture.slnx").touch()

    def test_closed_exact_pins_and_catalog_only_profiles(self):
        for value in (None, [], {"extra": True}, {"apt": "true"}, {"tlsTrust": 1},
                      {"nodeVersion": "24.11.0"}, {"nodeVersion": "24.x", "npmVersion": "11.6.0"},
                      {"nodeVersion": "24.11.0", "npmVersion": "latest"}, {"daprProfile": "unqualified"}):
            with self.subTest(value=value), self.assertRaises(ENV.PipelineError):
                ENV.validate_prerequisites(value)
        value = {"nodeVersion": "24.11.0", "npmVersion": "11.6.0", "apt": True,
                 "tlsTrust": True, "daprProfile": "catalog"}
        self.assertIs(value, ENV.validate_prerequisites(value))

    def test_exact_runtime_mismatch_fails_before_installing_tools(self):
        pins = {"nodeVersion": "24.11.0", "npmVersion": "11.6.0", "apt": True}
        for node, npm in (("v24.11.0", "11.7.0"), ("v24.12.0", "11.6.0")):
            commands = []

            def run(arguments, root, **kwargs):
                commands.append(arguments)
                return node if arguments[0] == "node" else npm

            with self.subTest(node=node, npm=npm), mock.patch.object(ENV, "run_command", side_effect=run), \
                    self.assertRaisesRegex(ENV.PipelineError, "exact shared prerequisite pins"):
                ENV.setup_environment(self.root, solution="Fixture.slnx", prerequisites=pins)
            self.assertEqual([["node", "--version"], ["npm", "--version"]], commands)

    def test_composite_installs_selected_node_and_checks_its_bundled_npm(self):
        fixture = FIXTURE.PreparationInputTests()
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        harness = FIXTURE.CompositeHarness(fixture.root, fixture.environment)
        result = harness.run_action(ROOT / "Github/ci-environment", {
            "solution": "Fixture.slnx", "npm-workspaces": '["tests/e2e"]',
            "prerequisites": json.dumps({"nodeVersion": "24.11.0", "npmVersion": "11.6.0"})})
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["24.11.0"], [inputs["node-version"] for reference, inputs, _ in harness.external_calls
                                      if reference.startswith("actions/setup-node@")])
        self.assertEqual(["npm", "npm"], [name for name, _, _ in fixture.effects()])

    def test_invalid_shared_inputs_fail_before_exact_node_installation(self):
        fixture = FIXTURE.PreparationInputTests()
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        (fixture.workspace / "package-lock.json").unlink()
        harness = FIXTURE.CompositeHarness(fixture.root, fixture.environment)
        result = harness.run_action(ROOT / "Github/ci-environment", {
            "solution": "Fixture.slnx", "npm-workspaces": '["tests/e2e"]',
            "prerequisites": json.dumps({"nodeVersion": "24.11.0", "npmVersion": "11.6.0"})})
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], harness.external_calls)
        self.assertEqual([], fixture.effects())

    def test_missing_registry_authority_prevents_selected_runtime_installation(self):
        environment = dict(os.environ)
        environment.pop("REGISTRY_PULL_USERNAME", None)
        environment.pop("REGISTRY_PULL_PASSWORD", None)
        harness = FIXTURE.CompositeHarness(self.root, environment)
        result = harness.run_action(ROOT / "Github/ci-environment", {
            "solution": "Fixture.slnx", "registry": "true",
            "prerequisites": json.dumps({"nodeVersion": "24.11.0", "npmVersion": "11.6.0"})})
        self.assertNotEqual(0, result.returncode)
        self.assertIn("pull-only username and password", result.stderr)
        self.assertEqual([], harness.external_calls)

    def test_apt_is_shared_and_precedes_tls_and_browser_effects(self):
        commands = []
        script = self.root / "built/playwright.ps1"
        with mock.patch.object(ENV, "run_command", side_effect=lambda args, root, **kw: commands.append(args) or ""), \
                mock.patch.object(ENV, "setup_tls_trust", side_effect=lambda root: commands.append(["tls-trust"])), \
                mock.patch.object(ENV, "playwright_tools", return_value=(script,)):
            ENV.setup_environment(self.root, solution="Fixture.slnx", browsers=True,
                                  prerequisites={"apt": True, "tlsTrust": True})
        self.assertEqual(["bash", str(ROOT / "Github/initialize-apt/initialize-apt.sh")], commands[0])
        self.assertEqual(["tls-trust"], commands[1])
        self.assertEqual(["pwsh", "-NoLogo", "-NoProfile", "-File", str(script), "install", "--with-deps"], commands[2])

    def test_https_archive_preparation_preserves_signed_suites_and_other_sources(self):
        source = self.root / "ubuntu.sources"
        source.write_text("Types: deb\nURIs: http://azure.archive.ubuntu.com/ubuntu\n"
                          "Suites: noble noble-updates noble-backports\nComponents: main universe restricted multiverse\n"
                          "Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n\n"
                          "URIs: https://security.ubuntu.com/ubuntu\nSuites: noble-security\n")
        original = source.read_text()
        mirrors = self.root / "apt-mirrors.txt"
        mirrors.write_text("http://azure.archive.ubuntu.com/ubuntu/\tpriority:1\n"
                           "http://archive.ubuntu.com/ubuntu/\tpriority:2\n"
                           "http://security.ubuntu.com/ubuntu/\tpriority:3\n")
        original_mirrors = mirrors.read_text()

        def run(arguments, root, **kwargs):
            self.assertEqual(["sudo", "sed", "-i"], arguments[:3])
            subprocess.run(arguments[1:], cwd=root, check=True)
            return ""

        with mock.patch.object(ENV, "run_command", side_effect=run):
            ENV.use_https_ubuntu_sources(self.root, (source, mirrors))
        self.assertEqual(original.replace("http://azure.archive.ubuntu.com/ubuntu",
                                          "https://archive.ubuntu.com/ubuntu"), source.read_text())
        self.assertEqual(original_mirrors.replace("http://azure.archive.ubuntu.com/ubuntu",
                                                  "https://archive.ubuntu.com/ubuntu"), mirrors.read_text())

    def test_shared_apt_preparation_includes_the_hosted_runner_mirror_list(self):
        with mock.patch.object(ENV, "run_command"), mock.patch.object(ENV, "use_https_ubuntu_sources") as prepare:
            ENV.setup_apt(self.root)
        self.assertIn(Path("/etc/apt/apt-mirrors.txt"), prepare.call_args.args[1])

    def test_missing_exported_certificate_is_failure_and_never_imports_empty_trust(self):
        commands = []
        with mock.patch.object(ENV.Path, "home", return_value=self.root), \
                mock.patch.object(ENV, "run_command", side_effect=lambda args, root, **kw: commands.append(args) or ""), \
                self.assertRaisesRegex(ENV.PipelineError, "certificate PEM"):
            ENV.setup_tls_trust(self.root)
        self.assertFalse(any(args[:2] == ["certutil", "-A"] for args in commands))
        self.assertFalse(any("--check" in args for args in commands))

    def test_certificate_trust_covers_both_nss_databases_and_checks_imports(self):
        commands = []
        trust = self.root / ".aspnet/dev-certs/trust"
        trust.mkdir(parents=True)
        certificate = trust / "certificate.pem"
        certificate.write_text("synthetic certificate bytes")
        environment_file = self.root / "github-env"
        with mock.patch.dict(os.environ, {"SSL_CERT_DIR": "/existing/certificates:/usr/lib/ssl/certs:/other/certificates",
                                          "GITHUB_ENV": str(environment_file)}), \
                mock.patch.object(ENV.Path, "home", return_value=self.root), \
                mock.patch.object(ENV, "run_command", side_effect=lambda args, root, **kw: commands.append(args) or ""):
            ENV.setup_tls_trust(self.root)
            expected = f"{trust}:/usr/lib/ssl/certs:/existing/certificates:/other/certificates"
            self.assertEqual(expected, os.environ["SSL_CERT_DIR"])
            observed = subprocess.check_output([sys.executable, "-c", "import os; print(os.environ['SSL_CERT_DIR'])"], text=True)
            self.assertEqual(expected, observed.strip())
            self.assertEqual(f"SSL_CERT_DIR={expected}\n", environment_file.read_text())
        imports = [args for args in commands if args[:2] == ["certutil", "-A"]]
        self.assertEqual(2, len(imports))
        self.assertEqual({"sql:" + str(self.root / relative) for relative in
                          (".pki/nssdb", ".local/share/pki/nssdb")}, {args[3] for args in imports})
        self.assertTrue(all(args[-1] == str(certificate) and args[-3] == "P,," for args in imports))
        self.assertEqual(2, len([args for args in commands if args[:2] == ["certutil", "-L"]]))
        self.assertEqual(["dotnet", "dev-certs", "https", "--check", "--trust"], commands[-1])

    def test_tls_import_failure_propagates_and_does_not_claim_trust(self):
        trust = self.root / ".aspnet/dev-certs/trust"
        trust.mkdir(parents=True)
        (trust / "certificate.pem").write_text("synthetic certificate bytes")
        commands = []

        def run(arguments, root, **kwargs):
            commands.append(arguments)
            if arguments[:2] == ["certutil", "-L"]:
                raise ENV.PipelineError("NSS import verification failed", 57)
            return ""

        environment_file = self.root / "github-env"
        with mock.patch.dict(os.environ, {"SSL_CERT_DIR": "/existing/certificates", "GITHUB_ENV": str(environment_file)}):
            with mock.patch.object(ENV.Path, "home", return_value=self.root), \
                    mock.patch.object(ENV, "run_command", side_effect=run), self.assertRaises(ENV.PipelineError) as failure:
                ENV.setup_tls_trust(self.root)
            self.assertEqual("/existing/certificates", os.environ["SSL_CERT_DIR"])
            self.assertFalse(environment_file.exists())
        self.assertEqual(57, failure.exception.returncode)
        self.assertFalse(any("--check" in args for args in commands))

    def test_tls_trust_rejects_inherited_certificate_path_control_characters_before_effects(self):
        environment_file = self.root / "github-env"
        for control in ("\n", "\r", "\t", "\x7f", "\x85", "\u2028", "\u2029"):
            value = "/existing/certificates" + control + "FORGED=value"
            with self.subTest(control=repr(control)), \
                    mock.patch.dict(os.environ, {"SSL_CERT_DIR": value, "GITHUB_ENV": str(environment_file)}), \
                    mock.patch.object(ENV, "run_command") as command:
                with self.assertRaisesRegex(ENV.PipelineError, "control characters"):
                    ENV.setup_tls_trust(self.root)
                command.assert_not_called()
                self.assertEqual(value, os.environ["SSL_CERT_DIR"])
                self.assertFalse(environment_file.exists())

    def test_job_setup_changes_environment_without_changing_classification_or_original_document(self):
        from ci_pipeline import PreparedConfig
        document = {"dapr": False, "browsers": False, "jobs": {"routes": {"runner": "ubuntu-latest",
                    "setup": {"dapr": True, "prerequisites": {"tlsTrust": True}}}},
                    "tests": {"integration": ["tests/Routes.csproj"]}}
        prepared = PreparedConfig(self.root, self.root, document, "Fixture.slnx", "global.json", ())
        configured = PREPARE.environment_config(prepared, "routes")
        self.assertIsNot(prepared, configured)
        self.assertFalse(prepared.document["dapr"])
        self.assertTrue(configured.document["dapr"])
        self.assertEqual(document["tests"], configured.document["tests"])
        self.assertEqual({"tlsTrust": True}, configured.document["prerequisites"])
        with self.assertRaisesRegex(ENV.PipelineError, "not enabled"):
            PREPARE.environment_config(prepared, "unknown")

    def test_custom_job_explicit_browser_false_suppresses_unrelated_e2e_inference(self):
        from ci_pipeline import PreparedConfig, TestProjects
        document = {"browsers": True, "jobs": {"properties": {"runner": "ubuntu-latest",
                    "setup": {"browsers": False}}}}
        prepared = PreparedConfig(self.root, self.root, document, "Fixture.slnx", "global.json", ())
        classified = TestProjects((), ("test/Fixture.E2E.Tests.csproj",), ())
        with mock.patch.dict(os.environ, {"CI_CUSTOM_JOB": "properties"}), \
                mock.patch.object(sys, "argv", ["prepare_linux.py", "--phase", "build"]), \
                mock.patch.object(PREPARE, "prepare_config", return_value=prepared), \
                mock.patch.object(PREPARE, "restore_and_build"), \
                mock.patch.object(PREPARE, "classified_projects", return_value=classified), \
                mock.patch.object(PREPARE, "write_stage_outputs") as outputs:
            self.assertEqual(0, PREPARE.main())
        self.assertFalse(outputs.call_args.args[0].document["browsers"])
        self.assertIs(classified, outputs.call_args.args[1])
        self.assertEqual({"infer_browsers": False}, outputs.call_args.kwargs)


if __name__ == "__main__":
    unittest.main()
