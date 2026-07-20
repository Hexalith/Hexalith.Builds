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
  major tags for third-party actions in shared workflows or actions.
- Routine, non-publication Hexalith.Builds workflow and action references use
  the latest `main` branch reference. Publication is the deliberate exception:
  pin the reusable release workflow to one reviewed full commit SHA and pass
  that identical literal as `builds-execution-sha`. The executed release-tool
  SHA is independent of the caller's development-time
  `references/Hexalith.Builds` gitlink.

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

- Release is an intentional operator action, not a side effect of every merge.
  Module release callers use `workflow_dispatch`; ordinary pushes and pull
  requests run CI only.
- Before calling the reusable release workflow, the caller must fail closed
  unless the dispatch selected the current `main` tip and an exact-SHA
  successful push CI run exists. Keep this source check outside the protected
  release job so invalid dispatches cannot request approval or access release
  secrets.
- The reusable release job is associated with the protected environment named
  by `environment-name` (`production` by default). Configure that environment
  in the caller repository with required reviewers and a `main`-only deployment
  policy. The environment approval is the human publication authority:

  ```yaml
  on:
    workflow_dispatch:

  concurrency:
    group: release-production
    cancel-in-progress: false

  jobs:
    verify-source:
      # Caller-owned steps prove current main and exact-SHA green push CI.
      runs-on: ubuntu-latest

    release:
      needs: verify-source
      uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@0123456789abcdef0123456789abcdef01234567
      with:
        environment-name: production
        builds-execution-sha: 0123456789abcdef0123456789abcdef01234567
  ```

  Leave `test-projects` empty when the exact source CI already ran those tiers.
- After environment approval, the reusable workflow independently re-proves
  that its source SHA is still the exact current `main` tip and that an exact
  successful `push` run of the declared CI workflow exists. It repeats that
  proof before freezing verification evidence, before the first NuGet write,
  and before the first container write. A new main commit makes the pending
  release stale and fails it closed.
- Release jobs may still restore/build/pack when the release tool needs to
  produce versioned artifacts, but those steps should run only after CI has
  passed and preferably only when a release is warranted.
- Give release workflows a non-cancelling concurrency group
  (`cancel-in-progress: false`) so overlapping merges queue instead of racing
  semantic-release on tags and publication destinations.
- Keep release permissions at the job level. Non-release jobs should use
  `contents: read`; semantic-release jobs need only the write scopes they use.
- Pass only the reusable workflow's declared publication secrets explicitly
  from caller repository or organization scope; never use `secrets: inherit`.
  The reusable publication job still references the protected environment, so
  those credentials cannot be used until its protection rules pass.

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
  and the prospective squash title on pull requests, because versioning is
  derived entirely from the final commit message.
- Provide `@commitlint/*` devDependencies, a commitlint config, and a
  `package-lock.json`. Subscribe the caller to title edits and pass the title as
  a reusable-workflow input; the shared workflow transfers it through an
  environment variable and stdin rather than interpolating it into shell code:

  ```yaml
  on:
    pull_request:
      branches: [main]
      types: [opened, synchronize, reopened, edited]

  jobs:
    commitlint:
      uses: Hexalith/Hexalith.Builds/.github/workflows/commitlint.yml@main
      with:
        pull-request-title: ${{ github.event.pull_request.title }}
  ```
