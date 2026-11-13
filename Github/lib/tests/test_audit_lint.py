"""Audit lint over real committed module Git snapshots, without evaluation."""

from __future__ import annotations

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import audit_lint
import expected_state as state
from test_expected_state import Repository


class AuditLintTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repository = Repository(Path(temporary.name) / "module")

    def lint(self):
        self.repository.commit()
        audit_lint.lint(state.tree_snapshot(state.Git(self.repository.root)))

    def reject(self, path, data, rule):
        self.repository.write(path, data)
        with self.assertRaises(state.ExpectedStateError) as raised:
            self.lint()
        self.assertIn(state.escaped(path), str(raised.exception))
        self.assertIn(rule, str(raised.exception))
        self.assertIn(state.OWNERSHIP_MESSAGE, str(raised.exception))

    def test_extensionless_project_package_metadata_and_attributes_reject_blocking_codes(self):
        for number, code in enumerate(["NU1900", "nu1903", "Nu1904", "NU1905"]):
            with self.subTest(code=code):
                self.reject("custom-" + str(number), "<Project><ItemGroup><PackageReference Include='Example'>"
                            "<NoWarn>NU1901, NU1902; " + code + "</NoWarn></PackageReference></ItemGroup></Project>", code.upper())
        self.reject("attribute", "<Project><ItemGroup><PackageReference NoWarn='NU1901 NU1903'/></ItemGroup></Project>", "NoWarn")

    def test_namespace_local_and_case_insensitive_elements_and_attributes(self):
        self.reject("namespaced", "<m:PrOjEcT xmlns:m='urn:msbuild' xmlns:p='urn:metadata'>"
                    "<m:ItemGroup><m:PackageReference p:nOwArN='nu1904'/></m:ItemGroup></m:PrOjEcT>", "NU1904")

    def test_every_prohibited_declaration_under_any_parent_and_as_an_attribute(self):
        names = ["NuGetAuditSuppress", "MSBuildWarningsAsMessages", "auditSources",
                 "TestingPlatformCommandLineArguments", "VSTestTestCaseFilter"]
        for number, name in enumerate(names):
            for form in [f"<ItemGroup><PackageReference><{name}/></PackageReference></ItemGroup>",
                         f"<Target><Any {name.lower()}=''/></Target>"]:
                with self.subTest(name=name, form=form):
                    self.reject("forbidden-" + str(number), "<Project>" + form + "</Project>", name)

    def test_treat_as_local_property_uses_exact_list_tokens_and_nuget_audit_prefix(self):
        for number, value in enumerate(["NuGetAudit", "Other;NuGetAuditMode", "Other,nugetauditLevel",
                                        "Other\tNuGetAuditFutureExtension"]):
            with self.subTest(value=value):
                self.reject("local-" + str(number), "<Project TreatAsLocalProperty='" + value + "'/>", "TreatAsLocalProperty")
        self.reject("local-element", "<Project><Target><TreatAsLocalProperty>Other NuGetAuditMode</TreatAsLocalProperty></Target></Project>",
                    "TreatAsLocalProperty")

    def test_benign_warnings_audit_properties_local_properties_and_unrelated_sources_pass(self):
        self.repository.write("project", "<Project TreatAsLocalProperty='Other;NotNuGetAudit'>"
                              "<PropertyGroup><NoWarn>NU1901;nu1902 NU19030,CS1591</NoWarn>"
                              "<NuGetAudit>false</NuGetAudit><NuGetAuditMode>direct</NuGetAuditMode>"
                              "</PropertyGroup></Project>")
        self.repository.write("file.cs", 'var x = "<Project><NuGetAuditSuppress/></Project>";')
        self.repository.write("ordinary.xml", "<Root><NoWarn>NU1903</NoWarn><auditSources/></Root>")
        self.repository.write("ordinary.bin", b"\x00\xff\x80\x00")
        self.repository.write("ordinary.txt", "NoWarn NU1903 and NuGetAuditSuppress are examples.")
        self.lint()

    def test_project_and_config_xml_comments_do_not_create_findings(self):
        self.repository.write("project", "<Project><!-- <NoWarn>NU1903</NoWarn> <NuGetAuditSuppress/> -->"
                              "<PropertyGroup><NoWarn>NU1901<!-- NU1903 -->;NU1902</NoWarn></PropertyGroup></Project>")
        self.repository.write("NuGet.Config", "<configuration><!-- <auditSources/> --><packageSources><clear/></packageSources></configuration>")
        self.lint()

    def test_canonical_case_insensitive_and_structurally_identified_nuget_configs(self):
        self.reject("nested/nUgEt.CoNfIg", "<configuration><auditSources><clear/></auditSources></configuration>", "auditSources")
        self.reject("extensionless-custom-name", "<configuration><packageSources><clear/></packageSources>"
                    "<auditSources><clear/></auditSources></configuration>", "auditSources")
        self.reject("other.anything", "<c:configuration xmlns:c='urn:nuget'><c:trustedSigners/>"
                    "<c:section NoWarn='NU1905'/></c:configuration>", "NU1905")

    def test_only_selected_malformed_xml_fails_without_blanket_xml_rejection(self):
        self.reject("malformed-project", "<?xml version='1.0'?><Project><PropertyGroup>", "malformed")
        self.reject("malformed-attribute", '<Project NoWarn="NU1903>', "malformed")
        self.reject("malformed-utf32", '<Project NoWarn="NU1903>'.encode("utf-32"), "malformed")
        self.reject("malformed-namespaced", '<!-- root probe --><m:Project xmlns:m="urn:msbuild" NoWarn="', "malformed")
        self.reject("invalid-declared-encoding", b'<?xml version="1.0" encoding="unknown"?><Project/>', "malformed")
        self.reject("custom-config-start-tag", '<configuration unmatched="<auditSources>', "malformed")
        self.reject("NuGet.Config", "not XML at all", "malformed")
        self.reject("malformed-custom-config", "<configuration><packageSources><clear/>", "malformed")
        self.repository.write("malformed-project", "ordinary content")
        self.repository.write("malformed-attribute", "ordinary content")
        self.repository.write("malformed-utf32", "ordinary content")
        self.repository.write("malformed-namespaced", "ordinary content")
        self.repository.write("invalid-declared-encoding", "ordinary content")
        self.repository.write("custom-config-start-tag", "ordinary content")
        self.repository.write("NuGet.Config", "<configuration/>")
        self.repository.write("malformed-custom-config", "ordinary content")
        self.repository.write("unrelated.xml", "<Other><unfinished")
        self.lint()

    def test_xml_bytes_use_bom_and_declared_encodings(self):
        for number, data in enumerate(["<Project><NoWarn>NU1903</NoWarn></Project>".encode("utf-16"),
                                      "<Project><NoWarn>NU1903</NoWarn></Project>".encode("utf-32"),
                                      "\ufeff<Project><NoWarn>NU1903</NoWarn></Project>".encode("utf-8"),
                                      "<?xml version='1.0' encoding='iso-8859-1'?><Project Name='é'><NoWarn>NU1903</NoWarn></Project>".encode("latin-1")]):
            with self.subTest(encoding=number):
                self.reject("encoded-" + str(number), data, "NU1903")

    def test_bomless_utf32_signatures_select_valid_and_malformed_project_roots(self):
        for encoding in ("utf-32-le", "utf-32-be"):
            with self.subTest(encoding=encoding):
                declaration = '<?xml version="1.0" encoding="UTF-32"?>'
                self.reject("valid-" + encoding, (declaration + '<Project NoWarn="NU1903"/>').encode(encoding), "NU1903")
                self.reject("malformed-" + encoding, (declaration + '<Project NoWarn="NU1903>').encode(encoding), "malformed")

    def test_malformed_project_root_is_recognized_after_doctype_without_loading_entities(self):
        for doctype in ('<!DOCTYPE Project>', '<!DOCTYPE Project SYSTEM "file:///unavailable-external-dtd">',
                        '<!DOCTYPE Project [<!ENTITY unused "literal > text">]>',
                        '<!DOCTYPE Project [<!-- first --><!-- second -->]>'):
            with self.subTest(doctype=doctype):
                self.reject("doctype-project", '<?xml version="1.0"?>' + doctype + '<Project NoWarn="NU1903>', "malformed")

    def test_unterminated_doctype_subset_with_repeated_comments_fails_promptly(self):
        script = """import sys
sys.path.insert(0, sys.argv[1])
import audit_lint
findings = audit_lint._xml(sys.stdin.buffer.read(), canonical=True)
assert findings and 'malformed' in findings[0], findings
"""
        for comments in (20, 25, 1000):
            payload = ('<!-- lead --><!DOCTYPE Project [' + '<!--x-->' * comments + '<Project NoWarn="NU1903>').encode()
            with self.subTest(comments=comments):
                result = subprocess.run([sys.executable, "-I", "-c", script,
                    str(Path(audit_lint.__file__).parent)], input=payload, capture_output=True, timeout=2, check=False)
                self.assertEqual(0, result.returncode, result.stderr.decode("utf-8", "replace"))

    def test_large_prolog_and_deep_project_keep_audit_findings_bounded(self):
        script = """import sys
sys.path.insert(0, sys.argv[1])
import audit_lint
findings = audit_lint._xml(sys.stdin.buffer.read(), canonical=False)
assert findings and any('malformed' in f or 'NU1903' in f for f in findings), findings
"""
        payloads = [('<!--x-->' * 500_000 + '<Project NoWarn="NU1903>').encode(),
                    ('<Project>' + '<Group>' * 100_000 + '<NoWarn>NU1903</NoWarn>'
                     + '</Group>' * 100_000 + '</Project>').encode()]
        for payload in payloads:
            with self.subTest(size=len(payload)):
                result = subprocess.run([sys.executable, "-I", "-c", script,
                    str(Path(audit_lint.__file__).parent)], input=payload, capture_output=True, timeout=3, check=False)
                self.assertEqual(0, result.returncode, result.stderr.decode("utf-8", "replace"))

    def test_response_files_match_warning_and_property_lists_with_common_quotes(self):
        values = ['/p:NoWarn="NU1901;NU1903"', '"/p:NoWarn=NU1901, NU1904"',
                  "'-p:NoWarn=NU1902 NU1905'", "/p:NoWarn=NU1900", "-p:TreatAsLocalProperty='Other;NuGetAuditMode'",
                  '"/p:TreatAsLocalProperty=Other, nugetauditLevel"', '/p:NoWarn="NU1901;\nNU1903"',
                  "/p:NoWarn=NU1901 /p:NoWarn=NU1903", '/p:TreatAsLocalProperty="Other;\nNuGetAudit"',
                  '/p:"NoWarn"=NU1903', "/p:'NoWarn'=NU1904", '/p:"TreatAsLocalProperty"=NuGetAuditMode',
                  '/p:NoWarn="NU1901;<unrelated>;NU1903"', '-nowarn:NU1903', '-NoWarn:NU1904',
                  '-NoWarn:"NU1901;NU1905"', '-TreatAsLocalProperty:NuGetAuditMode']
        for number, data in enumerate(values):
            with self.subTest(data=data):
                self.reject("flags-" + str(number) + ".RSP", data, "audit-lint")
        for number, name in enumerate(["NuGetAuditSuppress", "MSBuildWarningsAsMessages", "auditSources",
                                       "TestingPlatformCommandLineArguments", "VSTestTestCaseFilter"]):
            self.reject("forbidden-rsp-" + str(number) + ".rsp", "/p:" + name + "=value", name)

    def test_benign_response_values_and_comments_are_conservative(self):
        self.repository.write("benign.rsp", "/p:NoWarn=NU1901;NU1902;NU19030\n/p:TreatAsLocalProperty=Other;NotNuGetAudit\n"
                              "/p:NoWarn=NU1901 /p:Other=NU1903\n/p:\"NoWarn\"=\"NU1901;<unrelated>;NU1902\"\n")
        self.lint()
        self.reject("comment.rsp", "# /p:NoWarn=NU1903", "NoWarn")

    def test_response_encoding_failures_fail_closed_and_utf16_is_supported(self):
        self.reject("invalid.rsp", b"\xff", "valid text encoding")
        self.reject("utf16.rsp", "/p:NoWarn=NU1903".encode("utf-16"), "NU1903")

    def test_selected_symlinks_and_gitlinks_are_rejected_without_following(self):
        target = self.repository.root.parent / "external"
        target.write_text("<Project/>")
        for path in ["sample.csproj", "flags.rsp", "NuGet.Config"]:
            source = self.repository.root / path
            source.symlink_to(target)
        with self.assertRaisesRegex(state.ExpectedStateError, "regular committed") as raised:
            self.lint()
        self.assertEqual(3, str(raised.exception).count("regular committed"))
        sha = self.repository.command("rev-parse", "HEAD")
        self.repository.command("update-index", "--add", "--cacheinfo", "160000", sha, "linked.rsp")
        self.repository.command("commit", "-qm", "test: response gitlink")
        with self.assertRaisesRegex(state.ExpectedStateError, "linked.rsp"):
            audit_lint.lint(state.tree_snapshot(state.Git(self.repository.root)))
        self.assertEqual("<Project/>", target.read_text())

    def test_pinned_committed_input_ignores_dirty_staged_deleted_and_sparse_files(self):
        self.repository.write("custom-project", "<Project><NoWarn>NU1901</NoWarn></Project>")
        self.repository.commit()
        snapshot = state.tree_snapshot(state.Git(self.repository.root))
        self.repository.write("custom-project", "<Project><NoWarn>NU1903</NoWarn></Project>")
        self.repository.command("add", "--all")
        (self.repository.root / "custom-project").unlink()
        index = (self.repository.root / ".git/index").read_bytes()
        audit_lint.lint(snapshot)
        self.assertEqual(index, (self.repository.root / ".git/index").read_bytes())
        self.assertFalse((self.repository.root / "custom-project").exists())
        self.repository.command("read-tree", "HEAD")
        self.repository.command("sparse-checkout", "init", "--cone")
        self.repository.command("sparse-checkout", "set", "outside")
        audit_lint.lint(state.tree_snapshot(state.Git(self.repository.root)))

    def test_malicious_filename_diagnostics_are_escaped(self):
        self.reject("bad\n::error::\x1b.rsp", "/p:NoWarn=NU1903", "NU1903")


if __name__ == "__main__":
    unittest.main()
