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
- `Github/scripts/build-packages.ps1`: Builds library projects during
  semantic-release.
- `Github/scripts/publish-packages.ps1`: Publishes NuGet packages during
  semantic-release.

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
  semantic-release without building packages (legacy — modules use
  `domain-release.yml`).
- [`Github/dapr-init/`](Github/dapr-init/README.md): Install the Dapr CLI and
  run `dapr init` with retry.
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
- `.github/workflows/copy-ai-assistant-instructions.yml`: Copies
  `ai-assistant-instructions.md` to assistant-specific rule files when that
  source file exists and is changed.

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

### Import Central Package Versions

Use `Props/Directory.Packages.props` from a repository
`Directory.Packages.props` file:

```xml
<Project>
  <Import Project="references/Hexalith.Builds/Props/Directory.Packages.props"
          Condition="Exists('references/Hexalith.Builds/Props/Directory.Packages.props')" />

  <ItemGroup>
    <!-- Add repository-specific PackageVersion entries here. -->
  </ItemGroup>
</Project>
```

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

An exact-version consumer manifest is intentionally not checked in before the
first package is published; consumers must not invent a `4.20.0` pin. The
semantic-release version and package hashes are the release record.

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

Live persisted composition remains explicitly unavailable while the separately
owned G-6 Dapr runtime-to-SDK disposition is unresolved. That result is a
non-passing prerequisite outcome, never a skipped or passing qualification.
`down` remains idempotent and only removes runner-owned invocation metadata.

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

### Metadata and Troubleshooting

Do not put bearer tokens, credentials, source payloads, raw environment dumps,
or protected tenant/resource values in a manifest, filter, fixture, or
retained artifact. The tools reject manifest secret-bearing values and avoid
retaining raw filters. If a command returns `2`, resolve the documented
external prerequisite rather than treating the run as a pass. If a command
returns `6`, correct the evidence path, schema, hash, status declaration, or
policy diagnostic and rerun it.

## Environment Detection

The build properties set environment flags used by consuming projects:

- `CIBuild`: Set to `true` in GitHub Actions or Azure DevOps.
- `IDEBuild`: Set to `true` in Visual Studio, ReSharper, VS Code, or Cursor.

## Version and Release Management

Releases are driven by semantic-release and Angular Conventional Commits.
Release jobs analyze commits, calculate the next version, update
`CHANGELOG.md`, create a GitHub release, and optionally build and publish NuGet
packages.

Domain modules release through the reusable
[`domain-release.yml`](.github/workflows/domain-release.md) workflow with a
module-owned `.releaserc.json`. The legacy
[`Github/package-release`](Github/package-release/README.md) action and its
`release.config.json` remain only for pre-domain-workflow repositories.

Package publishing behavior:

- Stable versions are published to NuGet.org with `NUGET_API_KEY`.
- Pre-release versions are published to GitHub Packages with `GITHUB_TOKEN`.
- Debug and non-release local builds receive a generated `VersionSuffix` from
  `Hexalith.Package.props`.

To create a release, merge or push Conventional Commits to a configured release
branch such as `main`, `next`, `next-major`, `alpha`, `beta`, or a maintenance
branch matching `[0-9]+.[0-9]+.x`. The release workflow creates the tag and
release artifacts.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for
details.
