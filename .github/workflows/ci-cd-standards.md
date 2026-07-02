# Hexalith CI/CD Standards

This document defines CI/CD rules shared by Hexalith modules. Module
repositories should keep only module-specific workflow wiring, package lists,
test project lists, and operational exceptions in their own docs.

## Shared Workflow Ownership

- Keep reusable workflow logic, composite actions, and shared CI/CD guidance in
  `Hexalith.Builds`.
- Keep module-specific inputs in the consuming module: solution path, package
  list, test project tiers, release package names, and infrastructure exceptions.
- Prefer reusable workflows for standard module pipelines. If a module needs a
  local workflow, use the shared composite actions from its pinned
  `references/Hexalith.Builds` submodule where practical.

## Action References

- Pin third-party actions to a full commit SHA and record the upstream version in
  a trailing comment.
- Do not use mutable action references such as `@main`, `@master`, or floating
  major tags in shared workflows or actions.
- Reusable workflow references from consuming repositories should use a
  versioned Hexalith.Builds tag or a full Hexalith.Builds commit SHA when the
  consuming module requires reproducibility.

## Submodules

- Initialize only root-declared submodules.
- Do not use recursive submodule checkout or recursive submodule update in
  shared workflows.
- Prefer `actions/checkout` with `submodules: false`, followed by
  `git -c submodule.recurse=false submodule update --init`.

## .NET Builds

- Restore and build solutions, but run tests by project rather than
  solution-level `dotnet test`.
- Use the module `global.json` to select the .NET SDK.
- Build Release with warnings as errors.
- Publish packages with source/project-reference mode disabled unless the module
  has a documented exception.

## Caching

- Cache the NuGet global packages folder only.
- Include dependency-defining files in cache keys, such as `global.json`,
  `nuget.config`, `Directory.Packages.props`, imported shared package props, and
  project files.
- Do not cache secrets, token-bearing files, build outputs that may contain
  credentials, or mutable workspace state.

## Release Gates

- Release jobs should not duplicate the full CI gate when the platform supports
  a reliable CI-success trigger.
- Release jobs may still restore/build/pack when the release tool needs to
  produce versioned artifacts, but those steps should run only after CI has
  passed and preferably only when a release is warranted.
- Keep release permissions at the job level. Non-release jobs should use
  `contents: read`; semantic-release jobs need only the write scopes they use.

## Artifacts

- Upload TRX and coverage artifacts from every blocking test job with
  `if: always()`.
- Keep retention short for routine CI evidence unless a module has compliance
  requirements for longer retention.

## Runtime Dependencies (Dapr)

- The supported Dapr baseline is **1.18+**. Shared reusable workflows and the
  `dapr-init` composite action default to `1.18.0`; do not pin a module below
  this baseline without a documented exception.

## Dependency Auditing

- Keep the NuGet vulnerability audit **enabled** (`NuGetAudit=true`,
  `NuGetAuditMode=all`). Do not disable it globally with `-p:NuGetAudit=false`;
  that silences the scanner across the whole pipeline.
- When `TreatWarningsAsErrors` is on, exclude the audit advisory codes
  (`NU1901`–`NU1904`) from `WarningsNotAsErrors` so a transitive advisory that
  cannot be upgraded immediately does not block CI, while still surfacing it in
  build logs.
- Acknowledge or waive an individual advisory with `<NuGetAuditSuppress>` rather
  than turning the whole audit off.

## Security Scanning

- Every module ships a **Dependabot** config (`.github/dependabot.yml`) covering
  the `nuget`, `github-actions`, and (when a `package.json` is present) `npm`
  ecosystems.
- Every module runs **CodeQL** on push/PR to `main` plus a weekly schedule.
  Prefer calling the shared reusable workflow:

  ```yaml
  jobs:
    codeql:
      uses: Hexalith/Hexalith.Builds/.github/workflows/codeql.yml@main
      permissions:
        security-events: write
        contents: read
      with:
        languages: csharp
  ```

- Pull requests run **dependency review** to block newly introduced vulnerable
  or non-compliant dependencies:

  ```yaml
  jobs:
    dependency-review:
      uses: Hexalith/Hexalith.Builds/.github/workflows/dependency-review.yml@main
  ```

## Commit Message Validation

- Modules that release with semantic-release MUST validate Conventional Commits
  on pull requests, because versioning is derived entirely from commit messages.
- Provide `@commitlint/*` devDependencies, a commitlint config, and a
  `package-lock.json`, then call the shared reusable workflow:

  ```yaml
  jobs:
    commitlint:
      uses: Hexalith/Hexalith.Builds/.github/workflows/commitlint.yml@main
  ```
