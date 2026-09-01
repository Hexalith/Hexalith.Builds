# Hexalith.Builds Tools

Utility scripts for consuming repositories that use `Hexalith.Builds` as a
submodule.

## Available Tools

### G-4 tool package qualification

`build-g4-tool-packages.ps1` builds both authorized .NET tools in Release,
packs exactly `Hexalith.Builds.Module.Cli` and
`Hexalith.Builds.Evidence.Cli`, and writes a SHA-256 inventory. It does not
reuse the shared consumer `Github/scripts/build-packages.ps1` behavior.

```powershell
.\Tools\build-g4-tool-packages.ps1 -Version 0.0.0-ci.1
```

`test-g4-tool-package-contracts.ps1` performs clean source and package-mode
qualification. It copies the fixtures into an isolated temporary consumer
repository, generates a local-tool manifest pinned to the provided version,
restores only from the temporary local feed, and invokes the positive and
blocking-negative module/evidence controls, including the packaged `module
test` command path. Retained module evidence is checked against the exact tool
version, source revision, dirty marker, invocation, manifest hash, JSON types,
and null rule semantics. `-RetainPackageDirectory` preserves
the exact qualified artifacts for the semantic-release publisher.

Tool help is probed through the local-tool boundary with arguments after `--`,
so qualification verifies each tool's own root and subcommand help rather than
the `dotnet tool run` wrapper. The qualification output directory must be new
and empty, every retained evidence filename must be unique, and the final
inventory is written only after source validation and all controls pass. Failed
or reused runs expose no complete inventory.

```powershell
.\Tools\test-g4-tool-package-contracts.ps1 -Version 0.0.0-ci.1 -RequireControls
```

`publish-g4-tool-packages.ps1` verifies the Release inventory before
publication. A prerelease version (one containing `-`) targets
repository-configured GitHub Packages using `GITHUB_TOKEN`; a stable version
targets NuGet.org using `NUGET_API_KEY`. The script fails closed if the token,
package inventory, version, NuGet package, symbol package, or SHA-256 record
is missing. Publication additionally requires an official package build,
executed and passed source validation and controls, repository-tracked fixtures
whose current bytes match the complete fixture manifest, and a complete
hash-verified qualification-evidence inventory. Every package row binds its
exact version, and every `.nupkg` and `.snupkg` must contain exactly one nuspec
whose ID/version matches that row. Fixture package-build modes,
validation bypasses, external fixtures, and partial evidence are explicitly
non-release-eligible. Before any push, the publisher also rechecks a clean exact
source revision, proves every fixture byte against that revision, and parses
every retained qualification artifact for its exact typed outcome, invocation,
tool/source identity, and expected negative rule IDs. The script submits each
primary `.nupkg` once; `dotnet nuget push` discovers and publishes the adjacent
`.snupkg` automatically.

```powershell
.\Tools\publish-g4-tool-packages.ps1 -Version 4.20.0
```

The version shown above is illustrative. Semantic-release supplies the actual
version; never create a consumer tool manifest with an unpublished version.

The preserved P1R `3.90.0` candidate bundles live in the umbrella workspace at
`_bmad-output/implementation-artifacts/qualification-evidence/`. Its README
and the P1R revalidation record bind the preserved `.8`, `.9`, `.13`, and
loop-5 diagnostic/final results. The final loop-5 bundle truthfully remains
nonrelease while its frozen fixture additions are untracked. These retained
candidates are not release or owner acceptance evidence.

### validate-central-package-versions.ps1

Requires `Props/Directory.Packages.props` to begin with the exact UTF-8 BOM
bytes `EF BB BF`, decodes the remainder as strict UTF-8, and only then evaluates
it with MSBuild. It rejects BOM-free, truncated, or invalid UTF-8 input, blank
or duplicate IDs, blank, unresolved, tag-prefixed, or malformed versions,
failed evaluation, and source/effective-catalog mismatches before release.

```powershell
.\Tools\validate-central-package-versions.ps1
```

Run the focused fixture suite with:

```powershell
.\Tools\test-central-package-version-validator.ps1
```

`test-authoritative-package-catalog.ps1` protects the approved migration package
identities, shared package-family alignment, and
`CentralPackageVersionOverrideEnabled=false` through evaluated MSBuild output.
It does not pin dependency versions, so an intentional version bump remains a
valid release input.

### Central package freshness audit

`audit-central-package-versions.ps1` evaluates the complete catalog, discovers
every enabled NuGet V3 source, and records current-version listing state plus
the latest listed stable and prerelease candidates. It never changes the
catalog and never treats a missing, unlisted, or unresolved result as a reason
to downgrade. Run it only when deliberately refreshing the checked-in audit:

```powershell
.\Tools\audit-central-package-versions.ps1
```

With no family selector, generation is a complete refresh and queries every
catalog family. An incremental refresh requires a prior audit and an explicit,
canonical family list; only those families are queried:

```powershell
.\Tools\audit-central-package-versions.ps1 `
  -PriorAuditPath .\Tools\package-version-audit.json `
  -Family hexalith-eventstore,hexalith-frontcomposer
```

PowerShell callers may also pass an array to `-Family`. Unknown or duplicate
families, a missing prior audit, and catalog-selection drift in any unrequested
family fail before output. The v2 snapshot envelope records `complete` or
`incremental` mode and an exact, non-overlapping refreshed/preserved partition
covering every catalog family.

An incremental refresh requires a `schemaVersion` 2 prior audit, because its
preserved rows are copied verbatim into the emitted document and must first pass
the closed-shape prior contract. Migrate an older audit with a complete refresh.

Generation compares repository-owned catalog and consumer declarations with the
claimed revision before any feed request, including staged and unstaged changes
while respecting Git's configured EOL normalization. Git blob reads are both
time- and size-bounded, defaulting to 10 seconds and 1 MiB per blob; override
them with `-GitBlobReadTimeoutSeconds` and `-GitBlobReadMaxBytes` when a
repository legitimately exceeds either bound. The audit is serialized to a sibling temporary file and
atomically moved into place only after the complete document succeeds, so a
failed refresh leaves the prior output intact.

Review `Tools/package-version-audit.json` by rollback-safe family, apply only
accepted versions to `Props/Directory.Packages.props`, and record the selected
version and disposition for every row. Live discovery is intentionally not a
release gate because feed results vary over time. CI and release instead run
the deterministic validator and its fail-closed fixtures:

```powershell
.\Tools\validate-package-version-audit.ps1
.\Tools\test-package-version-audit-generator.ps1
.\Tools\test-package-version-audit-validator.ps1
```

The deterministic generator fixtures cover V3 resource discovery, paged
registrations, arbitrary-size prerelease ordering, unlisted and missing
versions, unresolved sources, complete/incremental partitioning, targeted
querying, history deduplication, hostile family selections, and output-path
safety without accessing a live feed. `generatedFromRevision` binds the exact
ancestor commit whose committed catalog blob and tracked consumer declarations
are claimed. `catalogSha256` remains the BOM+CRLF-normalized semantic hash;
`catalogRawSha256` separately binds the exact raw Git blob so BOM or EOL drift
cannot hide behind normalization. Consumer `declarationSha256` values likewise
bind committed blob bytes, and validation proves the worktree copy against the
audited revision the way Git compares tracked content, so the audit stays valid
on a checkout whose configured EOL normalization rewrites those files.

Each family owns an observation origin containing its revision/time plus
ordered family-selection, source-scope, package-metadata, and consumer-evidence
fingerprints. Package-metadata fingerprints include each source diagnostic as
well as its listing state and candidates. Incremental generation validates and
copies every preserved family decision and package row unchanged, while a
genuinely changed refreshed family alone gains one typed family snapshot and its
package snapshots. Repeating identical
evidence does not append duplicate history. Explicit test fixtures remain
labeled synthetic and bound to their exact bytes and semantic relations. The
validator independently recomputes every closed-shape invariant, exact catalog
coverage, snapshot partition, origin fingerprint, consumer relation, and typed
history record, while retaining coherent family dispositions and rollback
groups, retained-exception rationale/removal triggers, and selected versions
that exactly match the evaluated catalog.

External-package decisions remain exact: retained rows cannot change, while accepted versions cannot downgrade, must be an audited
latest stable or prerelease candidate, and cannot move an existing stable pin
onto a prerelease channel. Internal `Hexalith.*` rows require canonical NuGet
versions in the catalog and every audit/candidate field. Their checked-in
`selectedVersion` is the accepted monotonic floor and must exactly match the
actual catalog selection, so an advance requires an audit refresh and a later
regression cannot hide above an older `auditedVersion`. An accepted advance must
be family-aligned, remain stable when the accepted baseline is stable, and carry
listed configured-source candidate evidence for the actual selection. Numeric
overflow and malformed version evidence fail as controlled validation errors;
build-metadata hyphens do not turn a stable version into a prerelease. The Tenants
release-owner guard likewise compares the actual catalog selection to its accepted
audit selection. `Microsoft.OpenApi` remains on 2.x until its ASP.NET Core 10
runtime constraint is removed, and a missing source result can never advance or
downgrade an external pin.

### validate-consumer-package-authority.ps1

Source-scans a consumer repository's tracked MSBuild XML and evaluates every
tracked project. It requires a version-free root wrapper importing the shared
catalog, CPM and override protection on every project, exact effective catalog
values, and a catalog row for every non-implicit `PackageReference`. It rejects
consumer `PackageVersion` items, dependency-version property overrides,
`PackageReference`/`GlobalPackageReference` version metadata,
`VersionOverride`, and CPM opt-outs while allowing legal asset metadata and
SDK-implicit references.

```powershell
.\Tools\validate-consumer-package-authority.ps1 `
  -RepositoryRoot ..\MyModule `
  -CatalogPath .\Props\Directory.Packages.props
.\Tools\test-consumer-package-authority-validator.ps1
```

### validate-package-version-exceptions.ps1

Validates `package-version-exceptions.json` as the closed allowlist for the ten
AppHost project-SDK pins and five local tool-manifest pins. Schema-only mode is
appropriate in the Builds checkout. Supplying a ChatBot umbrella workspace also
compares every actual root-declared repository pin with the allowlist and keeps
`Aspire.AppHost.Sdk` exactly aligned with the shared `Aspire.Hosting` version.

```powershell
.\Tools\validate-package-version-exceptions.ps1 `
  -InventoryPath .\Tools\package-version-exceptions.json `
  -CatalogPath .\Props\Directory.Packages.props
.\Tools\test-package-version-exception-validator.ps1
```

New entries require an architecture decision; the inventory is not a general
escape hatch for local package-reference versions.

### test-domain-workflow-test-platforms.ps1

Checks that reusable domain CI/release workflows retain their backward-compatible
VSTest default and route Microsoft.Testing.Platform callers to MTP-native TRX and
trait-filter options without leaking VSTest-only arguments.

### test-commitlint-workflow.ps1

Checks that the reusable Commitlint workflow validates the prospective squash
title before the pull-request commit range, transfers the title through an
environment variable and stdin without shell evaluation, and documents caller
subscription to pull-request title edits.

### builds-submodule-init.ps1

Adds or initializes the `Hexalith.Builds` Git submodule in a parent repository
under `references/Hexalith.Builds`.

#### Purpose

The script automates:

1. Checking that PowerShell is running with administrator privileges.
2. Adding the `references/Hexalith.Builds` submodule when it is not already
   declared.
3. Initializing the existing `references/Hexalith.Builds` submodule when it is
   declared.
4. Updating the submodule.
5. Checking out the `main` branch in the initialized build submodule.

#### Requirements

- Administrator privileges on Windows.
- Git available in `PATH`.
- PowerShell 5.0 or later.

#### Usage

Run the script from the root directory of your repository:

```powershell
.\references\Hexalith.Builds\Tools\builds-submodule-init.ps1
```

If the submodule has not been added yet, you can download the script and run it:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/Hexalith/Hexalith.Builds/main/Tools/builds-submodule-init.ps1" -OutFile "builds-submodule-init.ps1"
.\builds-submodule-init.ps1
```

#### Notes

- The script does not use recursive or remote submodule updates.
- When adding a new submodule, it adds the repository root and
  `./references/Hexalith.Builds` to Git's global safe directory list.
- The script does not create symlinks. Use `editorconfig-symlink.ps1` for the
  shared `.editorconfig` link.

### editorconfig-symlink.ps1

Creates a `.editorconfig` symbolic link in the parent repository that points to
`references/Hexalith.Builds/.editorconfig`.

#### Purpose

The script lets a consuming repository reuse the shared editor and analyzer
style settings from the `Hexalith.Builds` submodule.

#### Requirements

- Administrator privileges on Windows.
- `references/Hexalith.Builds/.editorconfig` must exist.

#### Usage

Run the script from the `references/Hexalith.Builds` submodule:

```powershell
.\Tools\editorconfig-symlink.ps1
```

#### What the Script Does

1. Resolves the `references/Hexalith.Builds` directory and its parent
   repository.
2. Verifies that `references/Hexalith.Builds/.editorconfig` exists.
3. Removes any existing parent `.editorconfig` path.
4. Creates a symbolic link from the parent `.editorconfig` to
   `references\Hexalith.Builds\.editorconfig`.

## Troubleshooting

- `Error: This script requires administrator privileges`: Run PowerShell as
  Administrator and try again.
- Symbolic link creation fails: Confirm administrator privileges and Windows
  symlink support.
- Git submodule commands fail: Confirm Git is installed and that the repository
  root is the current directory.
