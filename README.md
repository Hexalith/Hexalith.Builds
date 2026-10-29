# Hexalith.Builds

[![Version](https://img.shields.io/github/v/tag/Hexalith/Hexalith.Builds?filter=v*)](https://github.com/Hexalith/Hexalith.Builds/tags)

Common build, release, and repository automation assets for Hexalith
applications, modules, and libraries.

## Overview

This repository centralizes the shared build configuration used across the
Hexalith ecosystem. It provides MSBuild properties, analyzer configuration,
central package versions, GitHub composite actions, reusable workflows, release
configuration, and repository maintenance tools.

This repository:

- Centralizes package version management for consuming repositories.
- Provides common MSBuild settings for .NET projects and NuGet packages.
- Enforces shared analyzer, code style, nullable, and documentation settings.
- Provides reusable GitHub Actions for verification, release, Dapr bootstrap,
  and container deployment.
- Provides semantic-release configuration for Conventional Commit based
  releases.
- Keeps AI assistant instructions and repository conventions discoverable.

## Repository Structure

### Build Configuration

- `Hexalith.Build.props`: Common MSBuild properties, analyzers, source link,
  nullable, documentation, and package version settings.
- `Hexalith.Package.props`: NuGet package metadata and package build settings
  for library projects.
- `Props/Environment.Build.props`: CI and IDE build detection.
- `Props/Directory.Packages.props`: Central package versions for Hexalith
  projects.
- `Samples/Module.Directory.Build.props`: Sample module root
  `Directory.Build.props`.
- `Samples/Module.Directory.Packages.props`: Sample module
  `Directory.Packages.props`.

### Release Configuration

- `package.json`: semantic-release configuration for this repository.
- `package-lock.json`: Locked npm dependency graph for reproducible release
  jobs.
- `Github/package-release/release.config.json`: Shared semantic-release config
  for package-producing repositories (legacy — domain modules use
  `domain-release.yml` with a module `.releaserc.json`).
- `Github/scripts/build-packages.ps1`: Builds and packs library projects during
  semantic-release.
- `Github/scripts/publish-packages.ps1`: Publishes each primary NuGet package
  once during semantic-release; `dotnet` publishes its adjacent symbols package.

### Code Style and Analysis

- `.editorconfig`: Shared editor and analyzer style settings.
- `Hexalith.globalconfig`: Global C# analyzer configuration.
- `stylecop.json`: StyleCop configuration.

### AI Assistant Rules

- `AGENTS.md`, `CLAUDE.md`, and `.github/copilot-instructions.md`: the shared,
  location-independent baseline for Codex, Claude, and GitHub Copilot.
- [`DEVELOPMENT.md`](DEVELOPMENT.md): repository-specific Build, release, and
  C# development guidance.

### Tools

- [`Tools/`](Tools/README.md): Repository utility scripts.
  - `builds-submodule-init.ps1`: Adds or initializes the `Hexalith.Builds`
    submodule under `references/` and checks it out on `main`.
  - `editorconfig-symlink.ps1`: Creates a parent repository `.editorconfig`
    symlink pointing to `references/Hexalith.Builds/.editorconfig`.

### GitHub Composite Actions

- [`Github/create-release/`](Github/create-release/README.md): Run
  semantic-release without hard-coding package behavior; repositories may
  configure their own verified package lifecycle (legacy — modules use
  `domain-release.yml`).
- [`Github/dapr-init/`](Github/dapr-init/README.md): Install the Dapr CLI and
  run `dapr init` with retry.
- [`Github/governed-provenance/`](Github/governed-provenance/README.md):
  Validate the running job's reusable-workflow identity and emit the bounded
  static `uses:`-closure provenance the governed BUILD-REL-1 paths in
  `domain-ci.yml` and `domain-release.yml` hand to their callers.
- [`Github/initialize-build/`](Github/initialize-build/README.md): Initialize
  root-declared submodules without recursive or remote updates.
- [`Github/initialize-dotnet/`](Github/initialize-dotnet/README.md): Install
  the .NET SDK from `global.json` or an explicit version, with optional Aspire
  workload installation.
- [`Github/package-release/`](Github/package-release/README.md): Build and
  release package projects with semantic-release (legacy — modules use
  `domain-release.yml`).
- [`Github/publish-azure-container-app/`](Github/publish-azure-container-app/README.md):
  Update Azure Container Apps to a published image version (legacy
  HexalithApp-era).
- [`Github/publish-container-to-registry/`](Github/publish-container-to-registry/README.md):
  Build and publish Web/API containers to a registry (legacy — modules use
  `domain-release.yml` container publishing).
- [`Github/publish-containers/`](Github/publish-containers/README.md): Install
  the semantic-release container publish helper used by `domain-release.yml`.
- [`Github/unit-tests/`](Github/unit-tests/README.md): Run and clean a standard
  Hexalith test project (legacy — modules use `domain-ci.yml`).
- [`Github/verify/`](Github/verify/README.md): CI gate that checks out,
  initializes, builds through tests, and does not publish (legacy — modules use
  `domain-ci.yml`).

### Reusable Workflows

- `.github/workflows/build-release.yml`: Releases this repository with
  `Github/create-release`.
- `.github/workflows/domain-ci.yml`: Reusable domain module CI pipeline. See
  [domain-ci.md](.github/workflows/domain-ci.md).
- `.github/workflows/domain-release.yml`: Reusable domain module release
  pipeline. See [domain-release.md](.github/workflows/domain-release.md).

## Usage

### Add Hexalith.Builds as a Submodule

From a consuming repository root, add or initialize this repository as a
root-declared submodule under `references/`:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/Hexalith/Hexalith.Builds/main/Tools/builds-submodule-init.ps1" -OutFile "builds-submodule-init.ps1"
.\builds-submodule-init.ps1
```

After the submodule exists, the same script is available from the build
submodule:

```powershell
.\references\Hexalith.Builds\Tools\builds-submodule-init.ps1
```

The script requires administrator privileges on Windows because related tooling
may create symbolic links. It initializes only the
`references/Hexalith.Builds` submodule and checks out `main`.

### Import Build Properties

Use the sample files in `Samples/` as the starting point for consuming
repositories. A typical module root `Directory.Build.props` imports the shared
build props and then sets repository-specific package metadata:

```xml
<Project>
  <Import Project="references/Hexalith.Builds/Hexalith.Build.props"
          Condition="Exists('references/Hexalith.Builds/Hexalith.Build.props')" />

  <PropertyGroup>
    <Product>Hexalith.MyModule</Product>
    <RepositoryUrl>https://github.com/Hexalith/Hexalith.MyModule.git</RepositoryUrl>
    <PackageProjectUrl>https://github.com/Hexalith/Hexalith.MyModule</PackageProjectUrl>
    <PackageTags>hexalith;my module;</PackageTags>
    <Description>Hexalith MyModule Module</Description>
  </PropertyGroup>
</Project>
```

For library projects that should produce NuGet packages, import
`Hexalith.Package.props` from the source-level `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <ParentDirectoryBuildProps>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</ParentDirectoryBuildProps>
  </PropertyGroup>

  <Import Project="$(ParentDirectoryBuildProps)"
          Condition="Exists('$(ParentDirectoryBuildProps)')" />

  <Import Project="../references/Hexalith.Builds/Hexalith.Package.props"
          Condition="Exists('../references/Hexalith.Builds/Hexalith.Package.props')" />
</Project>
```

Ordinary builds do not create `.nupkg` or `.snupkg` archives. This prevents
timestamped local packages from accumulating under `bin` on every build.
Create a local package explicitly with
`dotnet pack <project.csproj> --configuration Debug`; release scripts explicitly
pack in Release configuration. Projects remain packable, and
`-p:GeneratePackageOnBuild=true` can opt a build into automatic packaging.

### Import Central Package Versions

`Props/Directory.Packages.props` is the sole NuGet package-reference version
authority for Hexalith repositories. A consumer-root `Directory.Packages.props`
is a version-free wrapper: it imports the shared catalog and may retain only
non-version CPM settings such as its existing transitive-pinning posture.

```xml
<Project>
  <Import Project="references/Hexalith.Builds/Props/Directory.Packages.props"
          Condition="Exists('references/Hexalith.Builds/Props/Directory.Packages.props')" />
</Project>
```

Do not add consumer `PackageVersion` items, dependency-version properties,
`PackageReference Version` or `VersionOverride` metadata, or a CPM opt-out.
Add a missing dependency to the shared catalog through a reviewed Builds
change, then keep the consuming `PackageReference` version-free. MSBuild project
SDK pins and local tool-manifest pins cannot use CPM; they must be recorded in
`Tools/package-version-exceptions.json` and pass its alignment validator.

Run the catalog, consumer, and exception validators documented in
[`Tools/README.md`](Tools/README.md) before integrating a catalog or wrapper
change.

## Test environment contract consumption

The offline validator also exposes the same strict descriptor reader directly:

```sh
hexalith-module validate --environment /absolute/private/test-environment.json --output json
```

Use either `--environment` or `--manifest`; combining them, or combining an
environment descriptor with `--adoption`, refuses. This route validates schema,
document-local ownership and timing consistency without provisioning services,
fetching schemas, accepting qualification evidence or granting execution authority.
Platform's full-campaign environment uses this handoff contract.


The Tooling `Hexalith.Builds.Tooling.TestEnvironment` namespace provides
`TestEnvironmentContractReader.ReadDescriptor(path)` and `ReadResult(path)`.
They validate strict UTF-8 JSON against the embedded
[`hexalith.test-environment.v1`](schemas/hexalith.test-environment.v1.json) and
[`hexalith.test-result.v1`](schemas/hexalith.test-result.v1.json) schemas, returning
a detached `JsonElement` or atomic metadata-only diagnostics (`HXT101`–`HXT107`).
Both require policy `hexalith.platform.candidate-and-attachment-policy.v1`.
Unsupported versions name the supported version for the requested document kind.
Reads are offline, bounded to 1 MiB and 64 levels, reject duplicate keys and unknown
properties, preserve omitted versus explicit holds, and verify effective timing
and attached outcomes against their own hold.
The CLI package includes both schemas under `tools/net10.0/any/schemas/`.
Tooling is not published as a standalone package: the reader runs inside the
installed `hexalith-module` tool, and module fixtures that validate descriptors
themselves use the published schemas.

Platform owns the canonical contract schemas and lifecycle semantics; these
mirrors support independent consumption. The
[descriptor and result samples](test/fixtures/module/test-environment-v1/) are
fictional contract examples. They illustrate synthetic composition/catalog,
isolation, readiness, startup, deadline and attachment data; unavailable
tool, candidate and artifact producers remain `not-yet-populated`. These samples
grant no runtime qualification. Populated tool,
candidate, gate, loaded-artifact and completion fields remain claims to verify
under the policy. Successful reads never provision resources, authenticate
evidence, admit attachments or migrate the existing native handoff/internal run
records. Attachments are admitted only by `hexalith-module attach` (see G-4 Local Tools). Lifecycle and candidate adoption belong to downstream work.

## G-4 Local Tools

This repository owns two repository-scoped .NET tools. They are the only public
runner and readiness-validator contracts for the G-4 workflow:

| Package ID | Tool command | Purpose |
| --- | --- | --- |
| `Hexalith.Builds.Module.Cli` | `hexalith-module` | Validates a module manifest and owns supported runner lifecycle. |
| `Hexalith.Builds.Evidence.Cli` | `hexalith-evidence` | Validates `hexalith.readiness-evidence.v1` matrices. |

After an approved version is published, a consumer pins both exact versions in
its checked-in `.config/dotnet-tools.json`, then restores and invokes them from
the consumer checkout:

```powershell
dotnet tool restore
dotnet tool run hexalith-module run --manifest module/hexalith-projects.module.json
dotnet tool run hexalith-module down --manifest module/hexalith-projects.module.json
dotnet tool run hexalith-module test --manifest module/hexalith-projects.module.json --profile full
dotnet tool run hexalith-evidence validate _bmad-output/planning-artifacts/implementation-readiness-traceability-matrix.yaml
```

`prepare-build --manifest module/hexalith.module-manifest.v2.json --mode source|package
--workspace <fresh-directory> --output human|json` validates the v2 declaration,
resolves the same workspace and materializes verified MSBuild imports without
allocating a run identity or starting resources. Its output parent must exist;
the destination must be absent and have no symbolic-link ancestors. Source mode
selects Debug; package mode selects Release and retains every Platform identity,
release tag and pinned catalog check. JSON returns `buildPreparation` containing
`workspace`, `mode`, `mappingHash` and the explicit `environment`. Pass those
imports to each restore/build process without evaluating shell text. Existing
files are never overwritten. The CI composite in `Github/prepare-module-build`
restores the exact local CLI pin and exports only validated mapping imports.
This candidate command is absent from accepted CLI 4.35.2: consumer pins and
root Builds revisions require separate producer qualification/publication before
CI can succeed; this change does not select a new accepted tool version.

`run` and `test` accept `--mode source|package` and default to `source`. CI jobs
that invoke this tool should pass `--mode package` explicitly. The tool resolves
the outermost Git superproject containing
the manifest once, selects the active module and every direct
`references/*` entry in that root's `.gitmodules`, and writes the resulting
mapping to the run workspace (`source-mapping/mapping.json`) and `plan.json`.
A standalone root with one validated v2 owner uses that declaration's canonical
repository identity, even when its checkout directory is renamed. Its source path
remains the actual active checkout. Legacy and multi-module declarations retain
the checkout directory identity; no arbitrary module is selected. Invalid v2
owners and identity collisions refuse before reference initialization. The resolved
identity determines generated mapping property names and the source/package
duplicate identity prefix.
Both modes require each declared direct reference to have exactly one staged
Git gitlink. Package mode checks out the active module if its manifest is in a
direct reference, while other package-origin references stay uninitialized.
A missing or mismatched declaration fails before any build or launch.
Native tests and module projects launched by the AppHost receive the same
mapping hash and generated MSBuild imports. Builds verify the persisted mapping
against that hash before choosing dependencies. A workspace outside Git maps
only its active module.

In source mode, the active module and every root-declared direct reference build
in Debug. An uninitialized direct reference is checked out at its recorded
gitlink without `--recursive` or `--remote`. An initialized direct reference
whose HEAD differs from the staged gitlink fails with `HXW004` without moving
its checkout, except Platform and Builds identity mismatches, which warn in
source mode. Stage that checkout commit with
`git -C <root> add -- ':(literal)<reference>'`, or move it to the staged gitlink
with `git -C <root> submodule update --checkout -- ':(literal)<reference>'`.
The literal pathspec keeps a reference name with `[`, `*` or `?` from matching a
sibling reference. Deinitialize a broken checkout before retrying. An initialized nested submodule
stops resolution before any initialization; deinitialize it manually before
retrying. Package mode leaves dependencies as packages and builds the active
module in Release. Package versions continue to come from the workspace's
central package management. For packages defined by the Platform-pinned Builds
catalog, a generated guard compares the effective version, including
`VersionOverride` and restored versions, with the isolated pinned catalog.
The catalog is evaluated for the consumer project name, so conditional pins
apply to that project. Equivalent exact NuGet ranges are accepted before
restore; the guard checks the resolved package version after restore.

McpCli is a short-lived tool, not a server, so its v2 declaration lists no
server. When the active v2 module is `mcpcli`, source-mode `run` and `debug`
build `src/Hexalith.McpCli/Hexalith.McpCli.csproj` under the mapped
`HexalithMcpCliRoot` before the Platform model and before any resource starts.
`test` needs `--profile` naming a declared test; the McpCli declaration
declares none, so `test` refuses it (`HXC002`) and builds nothing.
The build uses Debug, the run's pinned catalog with transitive pinning and
`--artifacts-path <run workspace>/tools/mcpcli`. It never packs or publishes,
and it writes no `bin` or `obj` into the checkout. A ready run reports
`HXI010` with field `mcpcli`, naming
`tools/mcpcli/bin/Hexalith.McpCli/debug/Hexalith.McpCli.dll` under the run
workspace and the mapped project it was built from, and `down` deletes it with
that workspace. A checkout that is unmapped, uninitialized or has no tool
project physically inside it fails with `HXW004`, and a failed build
with `HXR020`. Both name McpCli and the expected project path. A sibling or
nested copy is never used. Package mode does not build McpCli.

The Platform identity is the committed `references/Hexalith.Platform` gitlink
in a module workspace, or HEAD when `hexalith-platform.json` is tracked at
HEAD in the Platform repository. The record has `schemaVersion: 1` and an
optional NuGet `toolVersionRange`. Platform pins Builds through its own
`references/Hexalith.Builds` gitlink. The mapping and run plan record the
identity, composition checkout, release tag, catalog pin, and tool range;
the identity also contributes to the mapping hash. Resolution uses local Git
objects and refs only. Package mode initializes direct Platform and Builds
references at their recorded gitlinks, leaving other package-origin references
uninitialized. Existing checkouts are never moved.

Package mode requires an aligned, clean Builds checkout, an aligned Platform
checkout with a local `v<SemVer>` tag and no changes, and a running tool within
the Platform range. Inside the Platform repository the identity is HEAD, so the
tag and clean-tree requirements do not apply. Source mode reports all identity
conditions as warnings and continues. A Git workspace missing the direct
Platform reference fails in both modes, while its other identity conditions
stay warnings in source mode; a standalone workspace warns in source mode and
refuses package mode. The `toolVersionRange` in Platform's
`hexalith-platform.json` is the record of which tool versions Platform accepts.

Mapping values override optional `Directory.Build.props` source probes and
reconcile references selected by consumer targets before restore. Builds fail
rather than using a sibling, ancestor, or nested source outside the map.
Source-tree Builds AppHost, EventStoreHost and UiHost projects participate in
source-mode reconciliation: EventStore.Aspire, EventStore.Gateway and
FrontComposer.Shell resolve to Debug projects under their mapped roots.
EventStore.Aspire keeps `IsAspireProjectResource="false"` when its package
reference becomes a project reference. In package mode, the source-tree
EventStoreHost and UiHost remain outside consumer reconciliation, preserving
their isolated central package selections. Generated projects under a run
workspace's `hosts/` directory stay outside reconciliation in both modes;
their existing catalog validation still applies.

Retained source references may forward `Hexalith<Identity>Root` and
`Hexalith<Identity>FromSource` values only when they agree with the verified
mapping. Equivalent physical roots, including relative segments and trailing
separators, are cloned with the mapping's canonical spelling before child
project evaluation. A catalog-matching `Version` may accompany these values.
Unknown, malformed, duplicate, conflicting or unmapped assignments and all
configuration assignments fail with `HXW006` before dependent restore or
compilation. Existing package conversion refusals and child guards still apply;
forwarding cannot select another root or enable a package fallback.

Restoring and building this source graph is a separate prerequisite before use. `CompositionEngine`
launches an already-built Builds AppHost; resolving a run mapping does not
rebuild or select a different AppHost distribution. Source graph verification
therefore establishes a build boundary only. Distribution selection, ordinary
command execution, signed package qualification and hosted acceptance require
their own evidence.
The `HXW` diagnostics identify workspace failures: `HXW001` invalid active
root or manifest location, `HXW002` invalid root references or duplicate source
projects declaring the same package identity, `HXW003`
initialized nested reference, `HXW004` missing or stale direct source, `HXW005`
duplicate source and direct or restored package identity, and `HXW006` an
unmapped project or executable assembly, ambiguous package conversion, or
dependency metadata that changes the selected configuration. Each diagnostic
names the relevant identity or path and a reason.
`HXW007` reports an unavailable identity, record, or catalog pin; `HXW008`
reports composition commit mismatch; `HXW009` reports a Builds catalog pin or
checkout mismatch, or a pinned catalog that cannot be read, evaluated or
materialized, including a private catalog workspace left after refusal;
`HXW010` reports an unsupported or unavailable tool range; `HXW011` reports an
untagged or dirty Platform checkout; `HXW012` rejects a referenced package
version that differs from the pinned catalog, a missing or modified catalog
snapshot, unavailable restored assets, or a catalog package absent from the
active restored target. Package identity refusals list every holding condition
in one command result.

The checked-in [candidate consumer](test/fixtures/package-consumer/README.md)
pins both published tools to `4.29.1` from source
`21ce044ab465ccb2adab58b3d66e394ffbecf3c2`. Its isolated remote restore,
version checks, and synthetic positive/negative controls are recorded in
[the candidate evidence](evidence/g4/published-4.29.1-20261001/README.md).
The selected EventStore `3.110.0` tuple has independently recorded P1R acceptance;
fresh G-6 acceptance remains pending. Publication and contract controls do not
accept P0.
Use only versions actually published by semantic-release.

Pre-release consumers configure the Hexalith GitHub Packages NuGet source and
authenticated package-read access outside the checked-in tool manifest; stable
consumers restore from NuGet.org. Keep those credentials in user or CI NuGet
configuration/secret storage, never in a module manifest, filter, or retained
evidence artifact.

### Declared suites and v1 environment handoff

`hexalith-module test --manifest module/hexalith.module-manifest.v2.json --profile integration`
provisions a fresh runner-owned environment. Repeat `--profile` to execute a
compatible batch sequentially in that environment. Every entry must come from
the same manifest and agree on mode, composition graph and profile-specific
runtime/resources. Missing, duplicate or incompatible selections refuse before
services start. Legacy `--evidence` export supports a single entry only: a batch
with that option refuses before provisioning because the legacy format has only
one timing/report/count set. Normal batches retain each inspected entry report
in owned diagnostics without claiming future suite-completion proof. Source mode
builds Debug assets; package mode keeps the existing Release and pinned catalog
boundaries.

Startup defaults to 600 seconds from the environment request, including mapping
and builds. Valid justified declaration overrides use that same original clock.
Selected suite handoff refuses a caller fallback other than 600 seconds before
provisioning unless the authoritative declarations supply an existing justified
startup override. Actual execution context is also checked before provisioning:
local execution and complete GitHub Actions metadata are supported; other or
incomplete CI context refuses with the missing-context requirements. Every
resource must be healthy and every required per-start verification task must
pass before execution starts. Execution activates separately after readiness:
each entry receives its declared execution limit, and the batch bound includes
the sum of execution and stopping/processing allowances. Final stopping and
cleanup use the active entry's declared stop limit and original stopping
timestamp. Each entry's validated pre-launch descriptor and inspected native
report are archived durably without rereading child-writable paths or renewing
that stopping budget. `execution-timing.json` records activation and the
immutable absolute expiry before launching the child; retries never renew an
active expiry. Selected service and test builds use run-private output paths.
The legacy selection timestamps and source hashes remain immutable evidence
bindings.

Tests receive absolute `HEXALITH_TEST_ENVIRONMENT_PATH` pointing to private
`test-environment.json`, validated with `TestEnvironmentContractReader` before
handoff. It contains credential-free observed endpoints, real run/attempt/owner/
environment identities, authoritative composition content, verified catalog
commit and run instance, synthetic isolation, readiness, startup tasks and
independent deadlines. Credentials remain in the existing separate private
channel. Missing catalog identity or invalid observations refuse handoff;
unsupported descriptor ABIs still refuse. Unavailable candidate, loaded-artifact
and qualification proof stays `not-yet-populated`, and schema validity grants no
acceptance credit. Every resource needs explicit declaration ownership; shared
runner infrastructure belongs to the active selected module's declaration, or
the selected suite's declaring module when no active module is set. Servers and
Memories stores retain their declaring owners. A closure missing explicit
attribution refuses handoff with the prerequisite in its diagnostic.

Local declared suites apply the same lifecycle policy in source and package
modes: success automatically removes owned resources; test/startup failures and
startup/execution timeouts retain surviving resources until explicit cleanup.
Owner cancellation records `cancelled` and cleans up unless an earlier failure
already established retention. The first outcome, cause and UTC time are committed
before cleanup or optional archival and survive later cancellation and retries.
Automatic deletion requires a reader-valid durable terminal result. If publication
fails, the owner stays discoverable with pending cleanup; repair the result path
and retry exact cleanup. CI keeps its existing cleanup policy.

Command terminal status and outcome preserve the first recorded observation.
Later archive or cleanup errors remain in diagnostics and cleanup disposition.
An exhausted stopping allowance does not suppress the finite metadata commit or
renew the execution, archive or cleanup allowance. Unreadable existing outcomes
refuse replacement.

```sh
hexalith-module list --output human
hexalith-module list --output json
hexalith-module down --run-id <owner-run-id> --output json
```

Listing reads durable owner/environment/attempt identities and creation times,
independently of current manifests. It reports failed owners, retention, pending
cleanup and recorded startup/execution deadlines. Execution activation is persisted
before child launch, so running suites expose their actual independent deadline.
Legacy unknowns stay unavailable. Accepted attachments are listed per owner with
their attached run ids, absolute hold deadlines and the latest live deadline; an owner
that passed with live holds shows cleanup as pending.

```sh
hexalith-module attach --owner-run-id <owner> --mode source --composition-identity <manifest-sha256> [--hold-seconds 300] --output json
hexalith-module attach --owner-run-id <owner> --mode source --composition-identity <manifest-sha256> --attachment-request-id <issued-id>
hexalith-module attach --end --owner-run-id <owner> --attached-run-id <issued-attached-run-id>
```

`attach` applies policy `hexalith.platform.candidate-and-attachment-policy.v1`. The hold is
omitted (1800 seconds) or whole seconds from 1 to 14400; anything else is refused with
`HXR032` and nothing is clamped, defaulted or recorded. `HXR033` refuses ineligible
targets (hosted, developer-started, unknown, legacy, cross-context or cross-job), `HXR034`
incompatible mode, composition, artifact identity, readiness or isolation, `HXR035` a
finished owner (naming its state; a ready local failure-retained environment stays
attachable until it is stopped or cleaned), and `HXR036` an unknown, ended or expired
retry. Artifact-identity matching is refused as unverifiable until Epic 3 populates expected
and loaded identities. CI owners do not wait for holds in this version. The stable reason is the diagnostic field. An accepted attachment records the
original request or omission, acceptance time, effective seconds and absolute expiry, and
issues `originalAttachmentRequestId` and `attachedRunId`; a retry that presents either keeps
the first acceptance and expiry. `--end` ends only that attachment. A passed local owner
waits for every live hold to end or reach its deadline before automatic cleanup.
Exact cleanup accepts an omitted, missing or changed manifest, removes only the
requested owner's resources and claims, and can be retried safely. Exporting
`--evidence` requires an explicit `--manifest`. Incomplete cleanup exposes exact known container/process/workspace/claim leftovers and
explicit discovery uncertainty.
Valid listing rows remain visible alongside an incomplete-list diagnostic when
another state file is corrupt. Both output formats include known leftovers, and
explicit down returns the durable result path.

Validated `hexalith.test-result.v1` results live at
`<state-directory>/diagnostics/<run-id>/test-result.json`, outside deleted run
workspaces. The default state directory is the current user's local application
data directory under `hexalith-builds/g4-runs`. Classified diagnostics and inspected
native reports remain beside the result. A partial startup result preserves issued
identities and independent startup deadlines, with unavailable catalog, readiness,
endpoint and artifact proof explicitly unpopulated. Cleanup and retention advance
without replacing the first terminal outcome. These local results remain diagnostic
and grant no candidate acceptance or package runtime qualification.

Batch archives under `entries/<entry-name-sha256>/` are authoritative for each
entry. Top-level `test-environment.json` and `execution-timing.json` are
replaced for each entry and describe the last archived entry.

The closed `hexalith.module-run-evidence.v1` schema now adds `selections`,
`executionTiming`, and resource ownership/observation/endpoint fields. This is
an additive change that is forward-incompatible with validators embedding the
prior closed v1 schema: pinned validators must move with this tool. Earlier
artifacts remain supported by the current validator.

Managed exclusive listener claims are atomic across runner processes. When a
claim conflicts, the runner allocates fresh ports and retries, up to 64
attempts; only if every attempt conflicts does the run fail with `HXR031`
naming the last blocking run. Independent environments coexist. Port
probes are not operating-system reservations. Claims are released only after the
existing ownership-limited cleanup verifies that the owning resources are gone.
`down --run-id <owner>` continues to target only that run.

### Platform version catalog and startup prerequisites

The tools are published. The `toolVersionRange` in Platform's
`hexalith-platform.json` is the authoritative record of Platform-accepted
versions. Versions up to and including `4.32.0` are not Platform-accepted;
publication and local contract probes grant no Platform acceptance.

`Props/Directory.Packages.props` is the authority for package, AppHost SDK,
Dapr runtime/CLI and Redis selections. Tool builds embed a deterministic
snapshot of the evaluated catalog and the selected EventStore.Aspire package's
Toolkit dependency range. Installed tools read this snapshot offline outside
a source checkout. Missing, duplicate or malformed selections and missing or
invalid package metadata fail the build without fallback versions. Controlled
build overrides must agree with the catalog; changing a selection requires
editing the catalog before rebuilding.

The fixed legacy `schemas/hexalith.module-manifest.v1.json` contract retains
EventStore, Dapr runtime, Dapr SDK and FrontComposer consts. Edit those consts
alongside a catalog pin change; `PlatformPinSchemaAndRuntimeValidationRemainInParity`
enforces their parity. Bound evidence fixtures under `test/fixtures` record all
four pins and are validated against the catalog, so a change to any of them
requires requalifying those fixtures. An EventStore selection change also
requires updating `Tools/G4PackageQualification.functions.ps1` and
`Tools/test-g4-tool-package-contracts.ps1` before their expected run evidence
can change.

Building Tooling requires `pwsh` on `PATH` and access to configured NuGet feeds
when the selected EventStore.Aspire nuspec is not cached. Running
`Tools/validate-package-version-exceptions.ps1` also requires `python3`. Catalog
and installed-tool contract tests load the net10.0 Tooling assembly and require
PowerShell 7.6 or later.

Run `pwsh -NoProfile -File Tools/validate-platform-version-catalog.ps1` to
validate the Platform EventStore.Aspire/CommunityToolkit.Aspire.Hosting.Dapr
pair using NuGet dependency range semantics. The Folders.Aspire exception
continues to apply only to that project.

Before descriptor discovery or run resources, `run` and `test` of executable
manifests probe `aspire --version` within a finite bound. Aspire CLI must match
the selected AppHost SDK, including its prerelease identity; build metadata is ignored.
`HXR015` reports the observed CLI and expected SDK versions in human and JSON
diagnostics. Install the version named by the diagnostic and retry. Declaration
and evidence validation, help and `down` do not require Aspire.

The packaged AppHost's assembly metadata names the DCP, dashboard, project and
intermediate-output paths of the machine that built it. Before starting it,
`run`, `debug` and `test` resolve DCP and the dashboard from this machine's NuGet
global packages folder (`NUGET_PACKAGES`, otherwise `~/.nuget/packages`) at the
catalog Aspire version, and give the AppHost directory and the Aspire store
run-private directories under the run workspace. `HXR016` names a missing
`aspire.hosting.orchestration.<rid>` or `aspire.dashboard.sdk.<rid>` package;
no other location is tried. Restoring any AppHost that uses the catalog
`Aspire.AppHost.Sdk` version caches both packages.

### Platform Declaration v2 Enrollment

Validate local Platform declarations together before composition:

```powershell
dotnet tool run hexalith-module validate --manifest module/first.json --manifest module/second.json --output human
dotnet tool run hexalith-module validate --manifest module/first.json --output json
```

Add `--adoption` to report a domain module's static enrollment requirements:
`workspace`, `declaration`, `references`, `gitlinks`, `nested-references`,
`tool-pin`, `hosting-inventory`, `dependency-selectors` and `dapr-subscriptions`.
Each row gives the requirement, PASS/FAIL, a workspace-relative file and a reason.
JSON output adds the rows as `adoption` with the fixed `adoptionScope` statement.
Any failing row exits `1`, and independent requirements are still reported. The
check only reads the active module Git workspace and its tracked files. It never
initializes references, resolves a source mapping, restores packages or starts
resources. It is a bounded static check: it cannot prove runtime behavior or
replace human source review and SM-2 verification. Module owners follow the
Platform [domain-module enrollment guide](https://github.com/Hexalith/Hexalith.Platform/blob/main/docs/domain-module-enrollment.md).

The strict [Draft 2020-12 schema](schemas/hexalith.module-manifest.v2.json)
defines all five groups: identity, runtime, surfaces, integration and lifecycle.
Configuration keys preserve SDK spelling, including `APP_API_TOKEN` in secret
bindings and extension configuration inputs. Keys contain colon-separated
segments starting with an ASCII letter; single underscores may separate
alphanumeric words. Empty segments, whitespace and control characters are
rejected, and the complete key remains limited to 256 characters.
Unused capabilities use empty collections. Runtime replicas default to one;
startup defaults to 600 seconds (ten minutes). Overrides require a positive
finite duration and a nonblank justification. Required servers need usable
readiness. Tasks declare a scope and authority class; executable references
must be existing canonical repository-relative files. Validation only reads
these files. It never starts servers, executes tasks or changes declarations.

The following complete example exercises every group. Its `README.md` task
references illustrate local path checks; a module supplies its own real task
files. The same example is retained as
[test/fixtures/module/platform/valid.json](test/fixtures/module/platform/valid.json).

```json
{
  "schema": "hexalith.module-manifest.v2",
  "modules": [
    {
      "identity": {
        "moduleId": "sample",
        "servers": [
          {
            "id": "api",
            "appId": "sample-api",
            "resourceId": "sample-api-host",
            "enabled": true,
            "required": true
          }
        ],
        "dependencies": []
      },
      "runtime": {
        "dapr": [
          {
            "role": "events",
            "capability": "pubsub",
            "recoveryClass": "rebuild-only",
            "configurationKey": "Dapr:EventsComponent"
          }
        ],
        "extensions": [
          {
            "packageId": "Hexalith.Sample.Extension",
            "configurationInputs": [
              {
                "key": "Sample:Setting",
                "source": "configuration",
                "reference": "sample-setting"
              }
            ]
          }
        ],
        "resources": [
          {
            "server": "api",
            "requests": {
              "cpu": 0.25,
              "memoryMiB": 128
            },
            "limits": {
              "cpu": 1,
              "memoryMiB": 512
            },
            "volumes": [
              {
                "name": "data",
                "mountPath": "/data",
                "sizeMiB": 1024,
                "accessMode": "read-write-once",
                "recoveryClass": "authoritative-restore"
              }
            ]
          }
        ]
      },
      "surfaces": {
        "interfaces": [
          {
            "name": "sample-api",
            "server": "api",
            "protocol": "http",
            "routePrefix": "/sample",
            "exposure": "internal-only",
            "surfaceClass": "agent",
            "authorization": {
              "policy": "authenticated",
              "permissions": [
                "sample.read"
              ]
            },
            "callers": [
              "mcpcli"
            ],
            "operations": [
              {
                "name": "read",
                "kind": "query",
                "authorizationPolicy": "sample.read"
              }
            ]
          }
        ]
      },
      "integration": {
        "topics": [
          {
            "name": "sample-events",
            "role": "events",
            "deadLetter": {
              "strategy": "topic",
              "topic": "sample-dead-letter"
            }
          }
        ],
        "secrets": [
          {
            "name": "provider-access",
            "configurationKey": "Sample:ProviderAccess"
          }
        ],
        "dynamicSecretNamespaces": [
          {
            "name": "sample-credentials",
            "owner": "sample",
            "configurationKey": "Sample:CredentialsNamespace"
          }
        ],
        "identityNeeds": [
          {
            "name": "sample-client",
            "audience": "sample-api",
            "roles": [
              "reader"
            ],
            "claims": [
              "eventstore:tenant"
            ],
            "applicationPrincipal": true
          }
        ],
        "egress": [
          {
            "name": "provider",
            "protocol": "https",
            "destination": "sample-provider"
          }
        ],
        "providers": [
          {
            "name": "sample-provider",
            "capability": "external-storage",
            "tenancy": {
              "mode": "per-environment",
              "configurationKey": "Sample:ProviderTenant"
            }
          }
        ],
        "workers": [
          {
            "name": "publish",
            "kind": "external-effect",
            "disableControl": {
              "configurationKey": "Sample:PublishDisabled",
              "disabledValue": true
            }
          }
        ]
      },
      "lifecycle": {
        "readiness": [
          {
            "server": "api",
            "kind": "http",
            "endpoint": "/health/ready"
          }
        ],
        "tasks": [
          {
            "name": "verify",
            "scope": "per-start-verify",
            "authorityClass": "application",
            "executable": "README.md",
            "arguments": []
          },
          {
            "name": "recover",
            "scope": "recovery",
            "authorityClass": "recovery",
            "executable": "README.md",
            "arguments": []
          }
        ],
        "recoveryHooks": [
          {
            "name": "restore",
            "task": "recover",
            "resource": "data"
          }
        ],
        "fenceHooks": [
          {
            "name": "fence",
            "task": "recover",
            "resource": "data"
          }
        ],
        "replay": {
          "reopenBeforeReplay": false
        },
        "startup": {},
        "criticalFlows": [
          {
            "name": "sample-read",
            "surface": "sample-api",
            "operations": [
              "read"
            ]
          }
        ],
        "smokeSurfaces": [
          {
            "name": "sample-smoke",
            "surface": "sample-api",
            "suite": "sample-smoke-suite"
          }
        ],
        "classification": {
          "changeClass": "additive",
          "reason": "Initial sample enrollment"
        },
        "recoveryInventory": [
          {
            "name": "data",
            "recoveryClass": "authoritative-restore",
            "owner": "sample",
            "dependencies": [],
            "restoreHook": "restore"
          }
        ]
      }
    }
  ]
}
```

File diagnostics include `source` (the supplied file relative to the current
working directory), `field` (the complete indexed property path), and `message`
(the reason), together with the stable `ruleId`, `phase`, `category` and optional
`hint`. JSON syntax failures also set optional `location` to `line:byte`: the
1-based line and the 1-based UTF-8 byte offset within that line, which differs
from the character column after non-ASCII text. Human output renders these same fields; JSON output returns a single
`status`, `outcome` and `diagnostics` object. Credential values are never copied
into diagnostics. Errors aggregate deterministically across files, including
all duplicate module, app and resource identity locations. Any error returns
exit `1` and no usable declaration set. Success returns `0`; cancellation
returns `130`. Validation accepts at most 256 files, with each UTF-8 document
at most 1 MiB and 64 levels deep. The schema is embedded in Tooling and shipped
under `tools/net10.0/any/schemas/`, so installed validation uses no source checkout
or remote schema downloads.

`source` and `field` are not present on every diagnostic:

- `HXP013` reports the 256-file limit (`source` `manifest`) and the 1 MiB file limit (the file `source`, field `$`). `HXP015` reports an empty request (`source` `manifest`) and a missing required field (the file `source` and the missing property path).
- Cancellation (`HXC130`) and usage (`HXC001`) diagnostics have no `source`.
- A credential-bearing manifest path is reported as `[redacted manifest path]`.
- On Windows, a manifest on a different volume from the working directory is reported by its absolute path.

The enrollment window accepts the current and previous Platform major, bounded
by the first Platform major, 2. Initially only `hexalith.module-manifest.v2`
is eligible. Legacy v1 continues to serve the existing `run`, `down` and `test`
qualification contracts and can never enroll in Platform. Local validation and
package contract probes do not grant Platform tool acceptance or change pins.

### Module Manifest and Runner Contract

`hexalith-module` accepts a strict `hexalith.module-manifest.v1` JSON file.
All descriptor, UI, and fixture paths are forward-slash, repository-relative
paths. Validation rejects unknown or duplicate fields, duplicate identifiers,
path escapes, placeholders, unreadable files, unsupported pins, malformed
dependencies, and secret-bearing values before any lifecycle work.

The profile classes are `pure-domain`, `host-contract`, `persisted-boundary`,
`restart`, `two-instance`, `authenticated-browser`, `authenticated-cli`, and
`authenticated-mcp`. They define runner handoff contracts; they do not turn a
product assertion into a runner-owned pass claim.

Use `--output json` for a machine-readable diagnostic, and `--evidence
<repository-relative>.json` to atomically retain canonical
`hexalith.module-run-evidence.v1` metadata. Filter values are retained only as
SHA-256 fingerprints. Evidence identifies volatile timestamps and run IDs so
semantic comparisons remain deterministic.

The exit-code contract is stable: `0` success, `1` usage/manifest, `2`
prerequisite unavailable, `3` topology/lifecycle, `4` product/test, `5`
persisted state, `6` evidence schema/policy, and `130` cancellation. The
first causal failure is retained; a later evidence-write failure cannot rewrite
an earlier runner failure.

The native-report parser validates TRX counters emitted by both VSTest and
Microsoft Testing Platform/xUnit v3. Missing or invalid reports, zero matching
tests, all-skipped tests, and failed test counts are explicit non-passing
product/test outcomes once a live test invocation supplies a report. Parser
coverage alone is not persisted-runtime proof.

G-6 evidence is validated against one dated baseline passed with `--baseline`;
each `Tools/runtime-toolchain-baseline*.json` file owns its exact tuple and
approval date, and packets bind their baseline by SHA-256. Approval authority
stays in the validator: the approver and the Builds, Platform and
FrontComposer/Web owner roles must match its allowlist, the approval date may
not follow the packet capture, a support-table-listed Dapr pair must equal the
tuple's runtime and .NET packages and carry an approval decision, and the
tuple's Aspire SDK must be one of the audited AppHost SDK pins. Every audited
literal pin must set the Dapr CLI, Dapr runtime or Aspire CLI role, either in its
own text or, for a `default:` or `version:` pin, through its YAML context (the
enclosing `dapr-version`/`dapr-runtime-version` workflow input, or the `with:`
block of a Dapr CLI setup step), and must equal that role's tuple value exactly;
each role needs at least one pin, and a pin that sets no role is rejected. The
mutation-controls command record must be exactly the self-test's
`G6-EVIDENCE-MUTATIONS-PASSED` result line, every count included. The pending
2026-09-29 candidate baseline records the explicit Dapr runtime `1.18.2` / .NET
package `1.18.10` exception with Toolkit `13.5.1-beta.770`; it is not a
support-table-listed pair. The current catalog's Toolkit `13.6.0-beta.910`
and Fluent UI `5.0.0` need fresh G-6 acceptance before current live
qualification. The executable fixture and installed runner implement public
composition and the native `full`/`full-mtp` persisted profiles; retained Stage
5 evidence qualifies their historical local scope. `HXR003` applies to the
metadata-only descriptor fixture, and unsupported executable profiles return
`HXR029`. Neither result implies current published persisted qualification.
`down` remains idempotent and removes only invocation-owned resources.

### Evidence Validator Contract

`hexalith-evidence validate <matrix.yaml>` parses YAML with duplicate-key
checking before strict schema and policy validation. Diagnostics are sorted by
source, row, rule, field, location, and hint. The validator rejects an
undeclared row status such as the currently unresolved `blocked` matrix status;
it does not mutate a consumer-owned matrix or invent status semantics.

Rows that claim passed or failed execution must reference a readable,
repository-relative JSON artifact with the declared
`hexalith.module-run-evidence.v1` schema and SHA-256. Future-path references
remain valid for `pending`, `blocked-external`, and `not-verified` rows.
The positive evidence fixture is a schema/validator contract sample only; it
is not persisted-runtime acceptance evidence.

A `passed` row is additionally bound to the exact inputs its artifact records.
Its `verification_command` must be the canonical space-delimited `test`
invocation carrying `--manifest`, `--profile`, and `--filter` once each, its
`fixture` must be the repository-relative fixture the named manifest profile
selects, and the artifact's own command must be the canonical
`--filter-sha256` form the runner emits. The declared manifest and fixture are
re-hashed from the current checkout, so a `passed` row cannot cite inputs that
have since changed. Every mismatch is the stable `HXE152` policy failure, and
the raw filter is never retained in a diagnostic. Because those hashes are
byte identities, check out byte-bound manifests and fixtures with LF endings
on every platform, as this repository pins for its own evidence fixtures.

### Metadata and Troubleshooting

Do not put bearer tokens, credentials, source payloads, raw environment dumps,
or protected tenant/resource values in a manifest, filter, fixture, or
retained artifact. The tools reject manifest secret-bearing values and avoid
retaining raw filters. If a command returns `2`, resolve the documented
external prerequisite rather than treating the run as a pass. If a command
returns `6`, correct the evidence path, schema, hash, status declaration, or
policy diagnostic and rerun it.

## Tooling test prerequisites

On Linux, `Hexalith.Builds.Module.Tests` requires a C compiler available as `cc`
for its native metadata fault probes. The tests compile a private shim and run
it in isolated subprocesses to verify interrupted locks, failed storage flushes
and cancellation during flush retries.

## Environment Detection

The build properties set environment flags used by consuming projects:

- `CIBuild`: Set to `true` in GitHub Actions or Azure DevOps.
- `IDEBuild`: Set to `true` in Visual Studio, ReSharper, VS Code, or Cursor.

## Version and Release Management

Releases are driven by semantic-release and Angular Conventional Commits.
Release jobs analyze commits, calculate the next version, create a Git tag and
GitHub release, and publish the configured tool or NuGet packages. They do not
commit generated release files back to protected branches.

Domain modules release intentionally through a manually dispatched caller and
the protected-environment reusable
[`domain-release.yml`](.github/workflows/domain-release.md) workflow with a
module-owned `.releaserc.json`. The legacy
[`Github/package-release`](Github/package-release/README.md) action and its
`release.config.json` remain only for pre-domain-workflow repositories.

Package publishing behavior:

- Stable versions are published to NuGet.org through Trusted Publishing. The
  protected Release job exchanges its GitHub OIDC token for a short-lived key
  immediately before publication; no stored NuGet API key is required.
- Pre-release versions are published to GitHub Packages with `GITHUB_TOKEN`.
- Debug and non-release local builds receive a generated `VersionSuffix` from
  `Hexalith.Package.props`.

The NuGet.org `Hexalith` owner must have a Trusted Publishing policy for GitHub
repository owner `Hexalith`, repository `Hexalith.Builds`, workflow file
`build-release.yml`, and environment `production`. Grant publication of new
versions for `Hexalith.Builds.Evidence.Cli` and `Hexalith.Builds.Module.Cli`;
grant new-package publication only if those package IDs have not yet been
created. Set the repository variable `NUGET_USER` to the individual NuGet.org
username that created the policy, rather than the `Hexalith` organization name.
NuGet.org checks this policy before issuing the temporary key.

For domain modules, manually dispatch the module release caller only after its
exact current `main` SHA has successful push CI, then approve the protected
environment deployment. This Builds repository releases only through a manual
dispatch from the exact current `main` tip and its protected `production`
environment. Merging or pushing does not start a release.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for
details.
