"""Exercise the package runner's process, isolation and fail-fast boundaries."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[1] / "product_validation.py"
SPEC = importlib.util.spec_from_file_location("product_validation", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)

FAKE_OPENSSL = r'''
import json, os, pathlib, sys
root = pathlib.Path(os.environ["PRODUCT_TEST_ROOT"])
data = json.loads((root / "sdk.json").read_text())
args = sys.argv[1:]
with (root / "commands.jsonl").open("a") as log:
    log.write(json.dumps({"args":["openssl",*args],"cwd":str(pathlib.Path.cwd()),
                          "temps":{key:os.environ.get(key) for key in ("TMPDIR","TMP","TEMP")}})+"\n")
private_temp=pathlib.Path(os.environ["TMPDIR"])
assert all(os.environ.get(key)==str(private_temp) for key in ("TMP","TEMP"))
assert private_temp.name=="signing-temp" and private_temp.stat().st_mode & 0o777 == 0o700
if data.get("identity_failure"):
    sys.exit(37)
if args[0] == "verify":
    sys.exit(43 if data.get("expired_candidate") else 0)
output = pathlib.Path(args[args.index("-out")+1])
if args[0] == "req":
    pathlib.Path(args[args.index("-keyout")+1]).write_bytes(b"private test key")
    output.write_bytes(os.urandom(32))
else:
    output.write_bytes(pathlib.Path(args[args.index("-in")+1]).read_bytes())
'''

FAKE_DOTNET = r'''
import hashlib, json, os, pathlib, shutil, signal, subprocess, sys, tempfile, time, xml.etree.ElementTree as ET, zipfile
root = pathlib.Path(os.environ["PRODUCT_TEST_ROOT"])
data = json.loads((root / "sdk.json").read_text())
args = sys.argv[1:]
cwd = pathlib.Path.cwd()
record = {"args":args, "cwd":str(cwd), "cache":os.environ.get("NUGET_PACKAGES"), "pid":os.getpid(),
          "temps":{key:os.environ.get(key) for key in ("TMPDIR","TMP","TEMP")}}
with (root / "commands.jsonl").open("a") as log:
    log.write(json.dumps(record)+"\n")
version = data.get("version", "2.3.4-preview.7")
if args[0] == "nuget":
    private_temp=pathlib.Path(tempfile.gettempdir())
    assert all(os.environ.get(key)==str(private_temp) for key in ("TMPDIR","TMP","TEMP"))
    assert private_temp.name=="signing-temp" and private_temp.stat().st_mode & 0o777 == 0o700
    with tempfile.NamedTemporaryFile(delete=False) as intermediate:
        intermediate.write(b"intermediate signing archive")
if args[0] == "msbuild":
    package = pathlib.Path(args[1]).stem
    target = root / pathlib.Path(args[1]).parent / "bin/Release/net10.0" / (package+".dll")
    values={"PackageId":package,"PackageVersion":version,"IsPackable":"true","TargetFramework":"net10.0",
            "TargetFrameworks":"","TargetPath":str(target),"AssemblyName":package}
    values.update(data.get("metadata",{}).get(package,{}))
    print(json.dumps({"Properties":values}))
elif args[0] == "pack":
    package = pathlib.Path(args[1]).stem
    if package == data.get("pack_failure"):
        sys.exit(17)
    feed = pathlib.Path(args[args.index("--output")+1])
    for suffix in ("nupkg","snupkg"):
        if suffix == "snupkg" and package == data.get("missing_symbols"):
            continue
        with zipfile.ZipFile(feed / (package+"."+version+"."+suffix), "w") as archive:
            actual = "9.9.9" if package == data.get("wrong_archive_version") else version
            archive.writestr(package+".nuspec", "<package><metadata><id>"+package+"</id><version>"+actual+"</version></metadata></package>")
            archive.writestr("lib/net10.0/"+package+(".dll" if suffix == "nupkg" else ".pdb"), b"built product")
    if package == data.get("extra_archive"):
        (feed / "Unexpected.1.0.0.nupkg").write_bytes(b"extra")
    if package == data.get("rewrite_output"):
        (root / pathlib.Path(args[1]).parent / "bin/Release/net10.0" / (package+".dll")).write_bytes(b"rebuilt")
elif args[0] == "restore":
    project=ET.parse(cwd / "Consumer.csproj")
    package=project.find(".//PackageReference").get("Include")
    if package == data.get("restore_failure"):
        sys.exit(19)
    config=ET.parse(args[args.index("--configfile")+1])
    assert config.find("./config/add[@key='signatureValidationMode']").get("value")=="require"
    feed=pathlib.Path(config.find("./packageSources/add[@key='candidate']").get("value"))
    assert not (feed.parent / "signer").exists(), "keys must be removed before consumers"
    author=config.find("./trustedSigners/author[@name='hexalith-private-validation-job']/certificate")
    assert author is not None and author.get("allowUntrustedRoot")=="true"
    assert author.get("hashAlgorithm")=="SHA256"
    for archive in feed.glob("*.nupkg"):
        with zipfile.ZipFile(archive) as package_archive:
            assert package_archive.read(".signature.p7s").decode()==author.get("fingerprint")
    ledger="".join(hashlib.sha256(path.read_bytes()).hexdigest()+"  "+path.name+"\n" for path in sorted(feed.iterdir()) if path.suffix in (".nupkg",".snupkg"))
    assert (feed / "release-artifacts.sha256").read_text()==ledger
    assert sorted(n.get("pattern") for n in config.findall("./packageSourceMapping/packageSource[@key='candidate']/package")) == sorted(data["ids"])
    assert "-p:NuGetAudit=true" in args and "-p:NuGetAuditMode=all" in args
    candidates=[package] + ([data["ids"][0]] if package != data["ids"][0] else [])
    libraries={}
    for item in candidates:
        actual="0.0.1" if item == data.get("wrong_restored_version") else version
        directory=pathlib.Path(os.environ["NUGET_PACKAGES"]) / item.lower() / actual.lower()
        directory.mkdir(parents=True)
        archive=directory / (item.lower()+"."+actual.lower()+".nupkg")
        shutil.copyfile(feed / (item+"."+version+".nupkg"),archive)
        if item == data.get("wrong_restored_bytes"):
            archive.write_bytes(b"different archive")
        source="https://example.invalid/feed" if item == data.get("wrong_restored_source") else str(feed)
        (directory / ".nupkg.metadata").write_text(json.dumps({"source":source}))
        libraries[item+"/"+actual]={"type":"project" if item == data.get("source_reference") else "package"}
    (cwd / "obj").mkdir()
    (cwd / "obj/project.assets.json").write_text(json.dumps({"libraries":libraries}))
    if data.get("mutate_feed"):
        next(feed.glob("*.nupkg")).write_bytes(b"changed after restore")
    if data.get("mutate_feed_directory"):
        (feed / "unexpected-directory").mkdir()
    if data.get("mutate_feed_symlink"):
        archive=next(feed.glob("*.nupkg"))
        copy=feed.parent / archive.name
        shutil.copyfile(archive,copy)
        archive.unlink()
        archive.symlink_to(copy)
elif args[:2] == ["nuget", "sign"]:
    assert (root / "archive-checked").exists(), "product validator must precede signing"
    if data.get("signing_failure"):
        sys.exit(41)
    assert args[args.index("--timestamper")+1] == "http://timestamp.digicert.com"
    assert args[args.index("--timestamp-hash-algorithm")+1] == "SHA256"
    if data.get("timestamp_failure"):
        sys.exit(47)
    pfx=pathlib.Path(args[args.index("--certificate-path")+1])
    assert pfx.stat().st_mode & 0o777 == 0o600
    assert pfx.parent.stat().st_mode & 0o777 == 0o700
    if data.get("terminate_signing"):
        worker = r"""
import json, os, pathlib, signal, sys
root, pfx = map(pathlib.Path, sys.argv[1:])
def terminating(_signum, _frame):
    (root / "forwarded-child-termination.json").write_text(json.dumps({
        "key_exists":pfx.is_file(), "scratch_exists":pfx.parent.parent.is_dir(),
        "temp_exists":pathlib.Path(os.environ["TMPDIR"]).is_dir()}))
signal.signal(signal.SIGTERM, terminating)
signal.alarm(10)
(root / "forwarded-child.json").write_text(json.dumps({"pid":os.getpid(),"group":os.getpgrp()}))
while True:
    signal.pause()
"""
        subprocess.Popen([sys.executable, "-c", worker, str(root), str(pfx)])
        deadline=time.monotonic()+5
        while not (root / "forwarded-child.json").exists():
            assert time.monotonic()<deadline, "forwarded child did not become ready"
            time.sleep(0.01)
        os.kill(os.getppid(), signal.SIGTERM)
        signal.pause()
    if not data.get("unsigned_candidate"):
        fingerprint=hashlib.sha256(pfx.read_bytes()).hexdigest().upper()
        with zipfile.ZipFile(args[2], "a") as archive:
            archive.writestr(".signature.p7s", "wrong" if data.get("wrong_signer") else fingerprint)
            if data.get("rewrite_payload"):
                archive.writestr("unexpected.txt", b"changed payload")
        if data.get("rewrite_existing_payload"):
            with zipfile.ZipFile(args[2]) as archive:
                entries=[(entry,archive.read(entry)) for entry in archive.infolist()]
            existing=next(entry.filename for entry,payload in entries if entry.filename.endswith(".dll"))
            replacement=private_temp / "rewritten.nupkg"
            with zipfile.ZipFile(replacement, "w") as archive:
                for entry,payload in entries:
                    archive.writestr(entry, b"changed existing payload" if entry.filename==existing else payload)
            os.replace(replacement, args[2])
            with zipfile.ZipFile(args[2]) as archive:
                (root / "rewritten-payload.json").write_text(json.dumps({
                    "before":[entry.filename for entry,payload in entries],"after":archive.namelist(),"changed":existing}))
    if data.get("rewrite_symbols"):
        next(pathlib.Path(args[2]).parent.glob("*.snupkg")).write_bytes(b"changed symbols")
    if data.get("rewrite_output_during_signing"):
        next(root.glob("src/*/bin/Release/net10.0/*.dll")).write_bytes(b"changed original assembly")
elif args[:2] == ["nuget", "verify"]:
    try:
        with zipfile.ZipFile(args[2]) as archive:
            fingerprint=archive.read(".signature.p7s").decode()
    except KeyError:
        sys.exit(43)
    if fingerprint != args[args.index("--certificate-fingerprint")+1]:
        sys.exit(43)
elif args[0] == "build":
    assert cwd != root, "main module must never be rebuilt"
    package=ET.parse(cwd / "Consumer.csproj").find(".//PackageReference").get("Include")
    assert "-p:NuGetAudit=true" in args
    assert (cwd / "global.json").read_bytes()==(root / "global.json").read_bytes()
    assert "product surface assertion "+package in (cwd / "Program.cs").read_text()
    assert "AssemblyInformationalVersionAttribute" in (cwd / "Program.cs").read_text()
    assert '!= "'+version+'"' in (cwd / "Program.cs").read_text()
    if package == data.get("consumer_build_failure"):
        sys.exit(23)
    output=cwd / "bin/Release/net10.0/Consumer.dll"
    output.parent.mkdir(parents=True)
    output.write_text(package)
else:
    package=pathlib.Path(args[0]).read_text()
    if package == data.get("consumer_execution_failure"):
        sys.exit(29)
'''


class ProductValidationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="product-validation-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.scratch = Path(self.temporary.name)
        self.root = self.scratch / "module"
        self.root.mkdir()
        self.ids = ["Example." + name for name in ("Contracts", "Client", "ReadModels", "Server", "Projections", "Testing")]
        self.data = {"ids": self.ids}
        rows, consumers = [], {}
        for identity in self.ids:
            relative = f"src/{identity}/{identity}.csproj"
            project = self.root / relative
            project.parent.mkdir(parents=True)
            project.write_text("<Project />")
            output = project.parent / "bin/Release/net10.0" / (identity + ".dll")
            output.parent.mkdir(parents=True)
            output.write_bytes(b"existing compiled assembly")
            output.with_suffix(".pdb").write_bytes(b"existing compiled symbols")
            rows.append({"id": identity, "project": relative})
            consumer = self.root / "consumers" / (identity + ".cs")
            consumer.parent.mkdir(exist_ok=True)
            consumer.write_text("// product surface assertion " + identity + "\n")
            consumers[identity] = consumer.relative_to(self.root).as_posix()
        self.manifest = {"packages": rows}
        self.contract = {"schema": "hexalith.product-validation.v1", "manifest": "packages.json",
                         "archiveValidator": "validate.py", "targetFramework": "net10.0", "consumers": consumers}
        (self.root / "global.json").write_text('{"sdk":{"version":"11.0.100"}}')
        (self.root / "validate.py").write_text('''import json, os, pathlib, sys, zipfile
root=pathlib.Path(os.environ["PRODUCT_TEST_ROOT"])
data=json.loads((root / "sdk.json").read_text())
(root / "archive-checked").write_text("product assertions executed")
assert len(list(pathlib.Path(sys.argv[1]).glob("*.nupkg")))==6
for path in pathlib.Path(sys.argv[1]).glob("*.nupkg"):
    with zipfile.ZipFile(path) as archive:
        assert ".signature.p7s" not in archive.namelist()
sys.exit(31 if data.get("validator_failure") else 0)
''')
        self.bin = self.scratch / "bin"
        self.bin.mkdir()
        executable = self.bin / "dotnet"
        executable.write_text("#!" + sys.executable + "\n" + FAKE_DOTNET)
        executable.chmod(0o755)
        openssl = self.bin / "openssl"
        openssl.write_text("#!" + sys.executable + "\n" + FAKE_OPENSSL)
        openssl.chmod(0o755)
        self.environment = os.environ.copy()
        self.environment.update(PATH=str(self.bin) + os.pathsep + self.environment["PATH"],
                                PRODUCT_TEST_ROOT=str(self.root), TMPDIR=str(self.scratch))
        self.environment.pop("GITHUB_WORKSPACE", None)

    def invoke(self):
        (self.root / "contract.json").write_text(json.dumps(self.contract))
        (self.root / "packages.json").write_text(json.dumps(self.manifest))
        (self.root / "sdk.json").write_text(json.dumps(self.data))
        return subprocess.run([sys.executable, "-I", "-S", str(SCRIPT), "--workspace", str(self.root),
                               "--contract", "contract.json"], env=self.environment,
                              capture_output=True, text=True, check=False, timeout=60)

    def commands(self):
        path = self.root / "commands.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def assert_refused(self, marker):
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(marker, result.stderr)
        self.assertFalse(list(self.scratch.glob("hexalith-product-validation-*")), "scratch leaked on refusal")

    def test_six_independent_consumers_reuse_one_built_version_and_clean_every_workspace(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        commands = self.commands()
        packs = [item for item in commands if item["args"][0] == "pack"]
        restores = [item for item in commands if item["args"][0] == "restore"]
        builds = [item for item in commands if item["args"][0] == "build"]
        executions = [item for item in commands if item["args"][0].endswith("Consumer.dll")]
        self.assertEqual((len(packs), len(restores), len(builds), len(executions)), (6, 6, 6, 6))
        self.assertTrue(all("--no-build" in item["args"] and "--no-restore" in item["args"] for item in packs))
        self.assertTrue(all("-p:PackageVersion=2.3.4-preview.7" in item["args"] for item in packs))
        sdk = [item for item in commands if item["args"][0] in {"msbuild", "pack", "restore", "build"}]
        self.assertTrue(all("-p:UseSharedCompilation=false" in item["args"] and "-warnaserror" in item["args"]
                            for item in sdk))
        self.assertEqual(len({item["cache"] for item in restores}), 6)
        self.assertTrue(all(item["cwd"] != str(self.root) for item in builds))
        self.assertTrue((self.root / "archive-checked").is_file())
        self.assertFalse(list(self.scratch.glob("hexalith-product-validation-*")))
        signs = [item for item in commands if item["args"][:2] == ["nuget", "sign"]]
        verifies = [item for item in commands if item["args"][:2] == ["nuget", "verify"]]
        self.assertEqual((len(signs), len(verifies)), (6, 6))
        self.assertTrue(all(item["args"][2].endswith(".nupkg") for item in signs))
        self.assertTrue(all("--certificate-password" not in item["args"] for item in commands))
        signing = [item for item in commands if item["args"][0] == "openssl" or item["args"][0] == "nuget"]
        self.assertEqual(len({item["temps"]["TMPDIR"] for item in signing}), 1)
        for item in signing:
            self.assertEqual(len(set(item["temps"].values())), 1)
            directory = Path(item["temps"]["TMPDIR"])
            self.assertEqual(directory.name, "signing-temp")
            self.assertTrue(directory.parent.name.startswith("hexalith-product-validation-"))
            self.assertFalse(directory.exists(), "signing intermediate files leaked")

    def test_unsupported_platforms_refuse_before_any_sdk_command_or_pack(self):
        for platform in ("win32", "darwin"):
            with self.subTest(platform=platform), patch.object(RUNNER.sys, "platform", platform):
                with self.assertRaisesRegex(RUNNER.ValidationError, "Linux only; refusing before packing"):
                    RUNNER.run(self.root, "contract.json")
        self.assertEqual(self.commands(), [])

    def test_identity_creation_failure_stops_before_signing_or_consumers_and_cleans_keys(self):
        self.data["identity_failure"] = True
        self.assert_refused("failed (37)")
        self.assertFalse(any(item["args"][:2] == ["nuget", "sign"] for item in self.commands()))

    def test_first_signing_failure_stops_before_any_consumer(self):
        self.data["signing_failure"] = True
        self.assert_refused("failed (41)")
        self.assertFalse(any(item["args"][0] == "restore" for item in self.commands()))

    def test_timestamp_refusal_stops_before_verification_or_consumers_and_cleans_keys(self):
        self.data["timestamp_failure"] = True
        self.assert_refused("failed (47)")
        self.assertFalse(any(item["args"][:2] == ["nuget", "verify"] or item["args"][0] == "restore"
                             for item in self.commands()))

    def test_unauthenticated_candidates_stop_before_every_consumer(self):
        for failure in ("unsigned_candidate", "wrong_signer", "expired_candidate"):
            with self.subTest(failure=failure):
                self.data = {"ids": self.ids, failure: True}
                self.assert_refused("failed (43)")
                self.assertFalse(any(item["args"][0] == "restore" for item in self.commands()))

    def test_signing_cannot_change_validated_payload(self):
        self.data["rewrite_payload"] = True
        self.assert_refused("signing changed candidate ZIP payload")

    def test_authenticated_signature_cannot_hide_changed_existing_entry_bytes(self):
        self.data["rewrite_existing_payload"] = True
        self.assert_refused("signing changed candidate ZIP payload")
        inventory = json.loads((self.root / "rewritten-payload.json").read_text())
        self.assertEqual(inventory["before"], inventory["after"])
        self.assertTrue(inventory["changed"].endswith(".dll"))
        self.assertEqual(len([item for item in self.commands() if item["args"][:2] == ["nuget", "verify"]]), 1)
        self.assertFalse(any(item["args"][0] == "restore" for item in self.commands()))

    def test_signing_cannot_change_symbols(self):
        self.data["rewrite_symbols"] = True
        self.assert_refused("signing changed unselected feed bytes")

    def test_signing_cannot_change_original_build_outputs(self):
        self.data["rewrite_output_during_signing"] = True
        self.assert_refused("existing Release assembly/symbol outputs changed")

    def test_termination_during_signing_reaps_forwarded_child_before_removing_keys(self):
        # Adopt/reap a leaked fixture child on test failure too; a regression
        # must not leave an orphan behind in the test runner's environment.
        libc = RUNNER.ctypes.CDLL(None)
        previous = RUNNER.ctypes.c_int()
        self.assertEqual(libc.prctl(37, RUNNER.ctypes.byref(previous), 0, 0, 0), 0)
        self.assertEqual(libc.prctl(36, 1, 0, 0, 0), 0)
        self.addCleanup(libc.prctl, 36, previous.value, 0, 0, 0)

        def reap_fixture():
            path = self.root / "forwarded-child.json"
            if path.exists():
                child = json.loads(path.read_text())["pid"]
                try:
                    os.kill(child, RUNNER.signal.SIGKILL)
                except ProcessLookupError:
                    pass
                try:
                    os.waitpid(child, 0)
                except ChildProcessError:
                    pass

        self.addCleanup(reap_fixture)
        self.data["terminate_signing"] = True
        result = self.invoke()
        self.assertEqual(result.returncode, 143, result.stderr)
        self.assertFalse(list(self.scratch.glob("hexalith-product-validation-*")))
        child = next(item["pid"] for item in self.commands() if item["args"][:2] == ["nuget", "sign"])
        with self.assertRaises(ProcessLookupError):
            os.kill(child, 0)
        forwarded = json.loads((self.root / "forwarded-child.json").read_text())
        self.assertEqual(forwarded["group"], child)
        with self.assertRaises(ProcessLookupError):
            os.kill(forwarded["pid"], 0)
        self.assertEqual(json.loads((self.root / "forwarded-child-termination.json").read_text()),
                         {"key_exists": True, "scratch_exists": True, "temp_exists": True})

    def test_missing_consumer_refuses_before_any_sdk_command(self):
        self.contract["consumers"].pop(self.ids[0])
        self.assert_refused("consumers must cover exactly")
        self.assertEqual(self.commands(), [])

    def test_escaping_product_source_refuses_before_any_sdk_command(self):
        self.contract["archiveValidator"] = "../outside.py"
        self.assert_refused("invalid module source path")
        self.assertEqual(self.commands(), [])

    def test_symlinked_product_source_refuses(self):
        path = self.root / self.contract["consumers"][self.ids[0]]
        path.unlink()
        path.symlink_to(self.root / "validate.py")
        self.assert_refused("symlink")

    def test_duplicate_manifest_identity_refuses(self):
        self.manifest["packages"][-1] = self.manifest["packages"][0]
        self.assert_refused("duplicate package")

    def test_missing_existing_release_output_refuses_without_pack(self):
        next(self.root.glob("src/*/bin/Release/net10.0/*.dll")).unlink()
        self.assert_refused("existing Release assembly/symbol output is missing")
        self.assertFalse(any(item["args"][0] == "pack" for item in self.commands()))

    def test_different_built_versions_refuse_without_pack(self):
        self.data["metadata"] = {self.ids[-1]: {"PackageVersion": "1.0.0"}}
        self.assert_refused("built package versions differ")

    def test_wrong_framework_refuses_without_pack(self):
        self.data["metadata"] = {self.ids[0]: {"TargetFramework": "net11.0"}}
        self.assert_refused("invalid evaluated Release package")

    def test_first_pack_failure_stops_later_packages_and_consumers(self):
        self.data["pack_failure"] = self.ids[0]
        self.assert_refused("failed (17)")
        self.assertEqual(len([item for item in self.commands() if item["args"][0] == "pack"]), 1)
        self.assertFalse((self.root / "archive-checked").exists())

    def test_missing_symbols_refuses_before_consumer_restore(self):
        self.data["missing_symbols"] = self.ids[0]
        self.assert_refused("package/symbol output differs")

    def test_extra_package_refuses_before_consumer_restore(self):
        self.data["extra_archive"] = self.ids[0]
        self.assert_refused("package/symbol output differs")

    def test_wrong_archive_version_refuses(self):
        self.data["wrong_archive_version"] = self.ids[0]
        self.assert_refused("package identity/version mismatch")

    def test_packing_may_not_rewrite_the_original_build(self):
        self.data["rewrite_output"] = self.ids[0]
        self.assert_refused("packing changed the existing Release")

    def test_product_archive_assertion_failure_stops_every_consumer(self):
        self.data["validator_failure"] = True
        self.assert_refused("failed (31)")
        self.assertFalse(any(item["args"][0] == "restore" for item in self.commands()))

    def test_wrong_restored_candidate_versions_refuse(self):
        self.data["wrong_restored_version"] = self.ids[0]
        self.assert_refused("exact candidate")

    def test_wrong_transitive_candidate_version_refuses(self):
        self.data["wrong_restored_version"] = self.ids[0]
        self.manifest["packages"] = self.manifest["packages"][1:] + self.manifest["packages"][:1]
        self.assert_refused("wrong restored candidate version")

    def test_wrong_restored_archive_bytes_refuse(self):
        self.data["wrong_restored_bytes"] = self.ids[0]
        self.assert_refused("restored candidate bytes differ")

    def test_candidate_feed_mutation_refuses_even_after_a_successful_restore(self):
        self.data["mutate_feed"] = True
        self.assert_refused("candidate feed changed during validation")

    def test_candidate_feed_directory_addition_refuses_after_restore(self):
        self.data["mutate_feed_directory"] = True
        self.assert_refused("nonregular or symlink entry")

    def test_byte_identical_feed_symlink_replacement_refuses_after_restore(self):
        self.data["mutate_feed_symlink"] = True
        self.assert_refused("nonregular or symlink entry")

    def test_same_bytes_from_another_feed_refuse(self):
        self.data["wrong_restored_source"] = self.ids[0]
        self.assert_refused("not from the isolated feed")

    def test_source_dependency_in_an_isolated_consumer_refuses(self):
        self.data["source_reference"] = self.ids[0]
        self.assert_refused("restore only packages")

    def test_first_restore_failure_stops_all_consumer_builds(self):
        self.data["restore_failure"] = self.ids[0]
        self.assert_refused("failed (19)")
        self.assertFalse(any(item["args"][0] == "build" for item in self.commands()))

    def test_first_consumer_build_failure_stops_later_restores(self):
        self.data["consumer_build_failure"] = self.ids[0]
        self.assert_refused("failed (23)")
        self.assertEqual(len([item for item in self.commands() if item["args"][0] == "restore"]), 1)

    def test_first_consumer_execution_failure_stops_later_restores(self):
        self.data["consumer_execution_failure"] = self.ids[0]
        self.assert_refused("failed (29)")
        self.assertEqual(len([item for item in self.commands() if item["args"][0] == "restore"]), 1)

    def test_shared_signature_and_trusted_signer_policy_survives_local_feed_configuration(self):
        destination = self.scratch / "NuGet.Config"
        RUNNER.consumer_config(destination, self.scratch / "feed", self.manifest["packages"], RUNNER.NUGET_CONFIG, "A" * 64)
        baseline = ET.parse(RUNNER.NUGET_CONFIG).getroot()
        actual = ET.parse(destination).getroot()
        author = actual.find("./trustedSigners/author")
        self.assertEqual(author.attrib, {"name": RUNNER.JOB_AUTHOR})
        self.assertEqual(author.find("certificate").attrib,
                         {"fingerprint": "A" * 64, "hashAlgorithm": "SHA256", "allowUntrustedRoot": "true"})
        actual.find("trustedSigners").remove(author)
        for name in ("config", "trustedSigners", "disabledPackageSources", "fallbackPackageFolders"):
            self.assertEqual(ET.tostring(actual.find(name)), ET.tostring(baseline.find(name)))

    def test_unsigned_acceptance_configuration_cannot_replace_shared_signature_policy(self):
        config = self.scratch / "weakened.config"
        config.write_text('<configuration><packageSources/><packageSourceMapping/></configuration>')
        with self.assertRaisesRegex(RUNNER.ValidationError, "strict NuGet signature"):
            RUNNER.consumer_config(self.scratch / "candidate.config", self.scratch / "feed", self.manifest["packages"], config, "A" * 64)

    def test_private_trust_refuses_invalid_fingerprint_and_duplicate_job_author(self):
        config = self.scratch / "base.config"
        document = ET.parse(RUNNER.NUGET_CONFIG)
        ET.SubElement(document.getroot().find("trustedSigners"), "author", name=RUNNER.JOB_AUTHOR)
        document.write(config)
        for base, fingerprint in ((RUNNER.NUGET_CONFIG, "*"), (config, "A" * 64)):
            with self.subTest(base=base, fingerprint=fingerprint):
                with self.assertRaisesRegex(RUNNER.ValidationError, "strict NuGet signature"):
                    RUNNER.consumer_config(self.scratch / "candidate.config", self.scratch / "feed",
                                           self.manifest["packages"], base, fingerprint)


if __name__ == "__main__":
    unittest.main()
