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

- Reference third-party actions by their latest upstream release tag, using the
  most specific tag that upstream publishes: prefer an exact `vX.Y.Z` tag, and
  fall back to a major-line tag such as `@v2` only when the upstream repository
  publishes nothing narrower. Record why in a trailing comment when falling back.
- When an action publishes no usable tag at all, reference its default branch
  (`@main`, or `@master` when that is the default) instead.
- A trailing version comment is redundant once the ref is the tag itself; omit it.
- This trades supply-chain immutability for readability: a tag can be moved by a
  compromised upstream, whereas a commit SHA cannot. Dependabot's weekly
  `github-actions` updates for `/` and `/Github/*` are what keep these tags
  current.
- Routine, non-publication Hexalith.Builds workflow and action references use
  the latest `main` branch reference, including this repository's own composite
  actions under `Github/`. Publication is the deliberate exception:
  pin the reusable release workflow to one reviewed full commit SHA and pass
  that identical literal as `builds-execution-sha`. The executed release-tool
  SHA is independent of the caller's development-time
  `references/Hexalith.Builds` gitlink.

### Governed closure exception

The tag-over-SHA preference above is a readability trade-off, and it stops
applying wherever a closure has to be *provable*. Inside the governed BUILD-REL-1
surface — `domain-ci.yml`, `domain-release.yml`, and every composite action
reachable from them (`Github/initialize-build`, `Github/dapr-init`,
`Github/publish-containers`, `Github/governed-provenance`) — every `uses:` is
either a literal lowercase 40-hex commit or a local
`./.hexalith/builds-execution/...` path. There is no third form:

- A governed run emits a closure digest over the exact bytes that executed. A
  tag cannot appear in that digest as anything but a promise, so the collector
  in `Github/governed-provenance` rejects tags, branches, expressions, and
  `docker://` references outright.
- Because the ref is no longer self-describing, these references **do** carry a
  trailing version comment (`# v7.0.1`). This is the inverse of the rule above
  and is deliberate: the comment is the only remaining human-readable name.
- Builds-owned composites used by a governed job load from the
  reusable-workflow commit through the local path, so the composite bytes can
  never drift from the workflow that invoked them. `domain-ci.yml` keeps a
  second, 40-hex-pinned form of the same composites for legacy callers, which
  never check Builds out.
- `Tools/test-governed-provenance.ps1` evaluates both shipped workflows on every
  CI run, so a reference that stops being resolvable fails the pull request
  rather than a release.

Self-pinning has a known consequence: a commit that changes a composite cannot
also pin itself to its own future SHA. The legacy 40-hex pins in `domain-ci.yml`
therefore name the previous reviewed commit and must be bumped in a follow-up
commit after the change that alters those composites merges.

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
        expected-package-count: 5
  ```

  Declare `expected-package-count` as the module's own NuGet package count whenever
  containers are published. It is caller-declared rather than counted from the
  package manifest so that gaining or losing a package fails closed until the
  change is reviewed against the manifest.

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

### Release publication freeze

- Every module's publication is gated by the caller variable
  `HEXALITH_RELEASE_PUBLISH_ENABLED`. Semantic Release runs only when it is
  exactly the four-character string `true`.
- **Rollout is fail-closed ecosystem-wide.** A module stops publishing as soon
  as it re-pins to a Builds commit carrying this gate. Set the variable on every
  module that should keep publishing before re-pinning it.
- A frozen module **skips and concludes green**. Freezing is a deliberate
  operational state, so it must not manufacture a red run on every dispatch.
- The comparison is a case-sensitive, untrimmed shell comparison, not a workflow
  expression: GitHub's `==` folds case, so `TRUE` would otherwise unfreeze a
  module nobody unfroze.
- Set the variable at **repository** scope on every module, including the ones
  meant to stay frozen. A repository value shadows the organization value, so an
  organization-wide `true` silently leaks into every repository that never set
  its own. Reusable workflows resolve `vars` from the caller's repository and
  organization, which is why the variable belongs on the module.

### Governed release mode (BUILD-REL-1)

- Governed mode is opt-in (`governed-release: true` / `governed-ci: true`) and
  default-off. A caller that sets nothing keeps the pre-BUILD-REL-1 contract.
- `id-token: write` and `attestations: write` live only on the governed release
  job. Job permissions resolve statically, so the governed path is a separate
  job rather than a conditional permission block.
- Governed Release consumes only `release-commit` for checkout, prepare, seal,
  verify, classify, and publish. The event head authenticates the triggering CI
  run and nothing else.
- Candidate packages are packed, signed, timestamped, and attested with
  `actions/attest-build-provenance` **before** the first publication side
  effect. A missing or empty attestation bundle fails the run closed.
- Missing signing secrets fail closed at contract validation, before checkout.
  Signing material is scoped to the candidate phase and never reaches Semantic
  Release, the container publisher, or an uploaded artifact.
- Governed runs always upload their verification data, including when frozen or
  failed. Unavailable values are explicit `null`; the artifact is never omitted.

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
  (`NU1901`–`NU1904`) from warnings-as-errors by adding them to
  `WarningsNotAsErrors`, so a transitive advisory that cannot be upgraded
  immediately does not block CI, while still surfacing it in build logs.
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
