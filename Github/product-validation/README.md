# Product package validation

This composite packs an existing Release/package build with `--no-build
--no-restore`, runs the module's package payload/dependency validator, and
restores, builds and executes one isolated consumer per package. Product IDs,
assertions and consumer source files stay in the module. The runner evaluates
the build's package version, requires its assembly and symbols to exist, and
asserts that each restored assembly's informational version matches that version.
It never compiles the module again.

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
signers and required signature validation are preserved. **Unsigned candidate
packages therefore fail restore under this policy.** A production caller must
qualify a supported signed-candidate path; this runner does not supply a signing
identity, accept unsigned packages or change the signature policy. Audits stay
enabled for consumer restores and builds.

The runner verifies local-feed provenance, restored candidate bytes and versions
(including transitive candidates), and rejects source dependencies. It checks
that the feed and existing Release assembly/symbol outputs remain unchanged
throughout validation. The first failed pack, archive check, restore, consumer
build or execution stops subsequent work. All temporary files are cleaned on
success and failure. Set `TMPDIR` outside the module repository; `run-with-temp`
does this for local checks.

The local equivalent is:

```bash
run-with-temp python3 -I /path/to/qualified/Builds/Github/lib/product_validation.py \
  --workspace /path/to/module --contract tools/product-validation.json
```

Release and qualify this interface, its consumer signature path and complete
CiCd action discovery before enabling a production hook. Local implementation
and fake-SDK behavioral tests do not establish package/catalog compatibility,
real signature acceptance, complete discovery or live runtime acceptance.
