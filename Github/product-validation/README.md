# Product package validation

This composite packs an existing Release/package build with `--no-build
--no-restore`, runs the module's package payload/dependency validator, and
restores, builds and executes one isolated consumer per package. Product IDs,
assertions and consumer source files stay in the module. The runner evaluates
the build's package version, requires its assembly and symbols to exist, and
asserts that each restored assembly's informational version matches that version.
It never compiles the module again.

The qualified signing path is Linux with OpenSSL 3 and .NET SDK `11.0.100`,
selected by the module's `global.json` (including its allowed SDK patch roll
forward). Install those tools and allow access to nuget.org and the timestamp
authority below. Other operating systems fail before any SDK command or packing:
PFX key storage containment is not qualified there. Linux process-group and
child-subreaper support must be available; inability to establish containment
also fails closed. The runner does not change machine or user certificate stores.

The module supplies a JSON contract through the `contract` input:

```json
{
  "schema": "hexalith.product-validation.v1",
  "manifest": "tools/release-packages.json",
  "archiveValidator": "scripts/validate-product-packages.py",
  "targetFramework": "net10.0",
  "consumers": {
    "Example.Contracts": "tools/package-consumers/Contracts.cs"
  }
}
```

The manifest contains only a nonempty `packages` array, with `id` and `project`
for each package. Every project must be packable, use the selected single
framework and have the same evaluated version. `consumers` must cover those
exact IDs. Every source path must be a literal repository-relative file without
symlinks. The archive validator is invoked with the temporary feed directory as
its only argument; a checksum ledger named `release-artifacts.sha256` is provided.
The module validator retains its product-specific exact inventory, framework,
payload and restore-backed dependency assertions. Consumer files contain
top-level C# product assertions; the runner appends the version assertion.

Each consumer receives the module's `global.json`, an exact candidate package
reference, private package/HTTP/plugin caches and CLI home, and the shared
NuGet configuration with exact candidate-ID source mappings. Shared trusted
signers and `signatureValidationMode=require` are preserved. Audits stay enabled
for consumer restores and builds.

After the module validates the unsigned archive inventory, payloads, dependencies
and ledger, the runner creates one self-signed RSA/SHA-256 code-signing identity
for this private validation job. Its certificate lasts one day; its private key,
certificate and empty-password PFX stay in an owner-only scratch directory, with
owner-only files. No private material or password is put in logs, environment
variables or command arguments. OpenSSL and the selected .NET SDK supply the
tools; no machine or user certificate store is changed.
Signing children receive an owner-only job directory for `TMPDIR`, `TMP` and
`TEMP`, containing NuGet's intermediate signing archives. That directory and
the explicit signing files are cleaned with the job.

The runner signs only the exact manifest-selected regular `.nupkg` files at the
evaluated built version. The SDK requests SHA-256 RFC3161 timestamps from
`http://timestamp.digicert.com`, the documented endpoint of
[DigiCert's existing TSA](https://knowledge.digicert.com/general-information/rfc3161-compliant-time-stamp-authority-server),
following [NuGet signing guidance](https://learn.microsoft.com/en-us/nuget/create-packages/sign-a-package).
Timestamp refusal stops the job; no timestamp trust exception or local TSA is
introduced. This network dependency avoids missing-timestamp warning `NU3027`
under the retained warnings-as-errors policy. Symbol packages remain
byte-identical. The runner authenticates each package with
`dotnet nuget verify --all`, pinned to the job certificate's
SHA-256 fingerprint. OpenSSL separately verifies the job certificate's validity
and self-signature: a valid timestamp can extend a signature beyond its
certificate's lifetime, and untimestamped expiration can be warning `NU3037`.
Job admission requires a currently valid job certificate. Unsigned, altered,
expired and wrong-signer candidates fail before consumer execution. Every
original ZIP entry must retain its payload;
only authenticated `.signature.p7s` metadata may be added. Product archive
validators therefore need no signature exception.

Each private consumer configuration adds one author trust entry with that exact
fingerprint and `allowUntrustedRoot=true`. Public repository signers and their
root requirements are copied unchanged. Candidate source mappings remain exact
package IDs. Signing uses the SDK's `--allow-untrusted-root`; this scoped trust
never applies to shared production configuration or a public signer. After all
packages are authenticated, the signed-byte ledger is regenerated, signing
material is deleted, and the feed is frozen. Consumers then restore those exact
signed archives.

The runner verifies local-feed provenance, restored candidate bytes and versions
(including transitive candidates), and rejects source dependencies. It checks
that the feed and existing Release assembly/symbol outputs remain unchanged
throughout validation. The first failed pack, archive check, restore, consumer
build or execution stops subsequent work. All temporary files are cleaned on
success, failure, Ctrl-C and ordinary termination. Every command runs in a
private process group; termination stops that group, escalates to SIGKILL when
necessary, and observes/reaps forwarded descendants before scratch or keys are
removed.
Set `TMPDIR` outside the module repository; `run-with-temp` does this for local
checks. Forced process kills and host loss rely on the enclosing disposable job's
scratch cleanup.

The local equivalent is:

```bash
run-with-temp python3 -I /path/to/qualified/Builds/Github/lib/product_validation.py \
  --workspace /path/to/module --contract tools/product-validation.json
```

Job signing exists only for isolated product checks. These bytes and this
temporary identity are never release or publication inputs, and do not authorize
adoption or enable a production hook. Release and qualify the immutable interface
and complete CiCd action discovery before enabling one. Behavioral tests alone
do not establish package/catalog compatibility, real signature acceptance,
complete discovery or live runtime acceptance.
