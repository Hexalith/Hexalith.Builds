# Hexalith Domain Release reusable workflow

`domain-release.yml` is a reusable (`workflow_call`) release pipeline for
Hexalith domain modules. It checks out the caller repository, initializes .NET,
installs Node.js dependencies, restores and builds the solution, optionally runs
Dapr-backed tests, and then runs semantic-release.

## Inputs

| Input | Required | Default | Description |
|-------|----------|---------|-------------|
| `solution` | Yes | - | Path to the `.slnx` or `.sln` file to restore and build. |
| `dotnet-global-json` | No | `global.json` | Path to the SDK-pinning `global.json` file. |
| `packages-lock-file` | No | `Directory.Packages.props` | File used to build the NuGet cache key. |
| `dapr-version` | No | `1.18.0` | Dapr version used when tests are enabled. |
| `test-platform` | No | `vstest` | Test command contract. Set `microsoft-testing-platform` for xUnit v3 MTP-native TRX reporting. |
| `test-projects` | No | `''` | Newline-separated test project paths to run before release. Leave empty when the caller is triggered by `workflow_run` on CI success — CI already ran the same gate. |
| `node-version` | No | `node` | Node.js version passed to `actions/setup-node`. |
| `timeout-minutes` | No | `20` | Timeout for the release job. |
| `publish-containers` | No | `false` | Whether to prepare semantic-release container publishing for .NET SDK container projects. |
| `container-projects` | No | `''` | Newline-separated container mappings in `path/to/project.csproj|repository-name` format. Required when `publish-containers` is `true`. |
| `builds-execution-sha` | No | `''` | Exact maintainer-approved Builds commit. Required for container publishing and checked against both the resolved reusable workflow and nested action/helper bytes. |
| `release-authority-url` | No | `''` | Durable HTTPS source for the release-owner authority JSON. Required for container publishing. |

## Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `NUGET_API_KEY` | Yes | API key used by semantic-release package publishing. |
| `HEXALITH_ZOT_USERNAME` | No | Hexalith Zot username used when `publish-containers` is `true`. |
| `HEXALITH_ZOT_API_KEY` | No | Hexalith Zot API key used when `publish-containers` is `true`. |

Pass secrets explicitly from the caller (see Usage below). The workflow declares
its exact secret surface, so `secrets: inherit` grants more than it needs; use
explicit mapping unless a repository has a documented reason not to.

The workflow also uses the caller repository `GITHUB_TOKEN` for semantic-release
GitHub operations. Container publishing uses the organization variable
`HEXALITH_ZOT_REGISTRY`; when it is not set, the workflow defaults to
`registry.hexalith.com`.

## Steps

1. Check out the caller repository with full history, then initialize
   root-declared submodules only (`Github/initialize-build`).
2. Initialize .NET with `actions/setup-dotnet` from `global.json`.
3. Set up Node.js.
4. Cache NuGet packages.
5. Run `npm ci` and `npm audit signatures`.
6. Restore and build the solution in Release configuration with warnings as
   errors.
7. If `test-projects` is not empty, initialize Dapr, run each test project, and
   upload TRX/coverage evidence (`release-test-results`, `if: always()`).
8. If `publish-containers` is `true`, require `job.workflow_sha` and the workflow
   repository to match `builds-execution-sha`, then run the SHA-pinned arm64
   emulation setup.
9. Check out Hexalith.Builds at that exact commit, invoke the nested
   `Github/publish-containers` action from the immutable local checkout, and
   install the publisher, immutable OCI validator, durable authority validator,
   and child-digest smoke helpers. The action also compares its action/helper
   bytes with the same approved Builds commit.
10. Run `npx semantic-release`, passing the approved Builds identity and durable
    authority source to the caller's publication preflight.

The generated publish helper accepts the semantic-release version as its first
argument. The caller's `verifyReleaseCmd` must validate its full durable
authority and destination absence before semantic-release creates a Git tag.
The `publishCmd` must revalidate immediately before the first NuGet push, then
call the container helper with the same version. Existing package/tag identities
are collisions; duplicate skipping is forbidden. The helper logs in to Hexalith
Zot only when semantic-release reaches `publishCmd`, so non-releasing commits do
not require registry credentials:

```json
"verifyReleaseCmd": "bash scripts/validate-release-authority.sh ${nextRelease.version} >&2",
"publishCmd": "bash scripts/validate-release-authority.sh ${nextRelease.version} >&2 && dotnet nuget push ./nupkgs/*.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json && ./.hexalith/release/publish-containers.sh ${nextRelease.version}"
```

## Usage

The standard caller runs after CI succeeds (`workflow_run`), so the release
never duplicates the CI gate and never publishes from a commit whose CI failed
(see `ci-cd-standards.md`, "Release Gates"):

```yaml
on:
  workflow_run:
    workflows: [CI]
    types: [completed]
    branches: [main]

concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false

permissions:
  contents: read

jobs:
  release:
    if: >-
      github.event.workflow_run.conclusion == 'success' &&
      github.event.workflow_run.event == 'push'
    permissions:
      contents: write
      issues: write
      pull-requests: write
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@main
    with:
      solution: Hexalith.<Module>.slnx
      publish-containers: true
      builds-execution-sha: ${{ vars.HEXALITH_BUILDS_RELEASE_SHA }}
      release-authority-url: ${{ vars.HEXALITH_RELEASE_AUTHORITY_URL }}
      container-projects: |
        src/Hexalith.<Module>/Hexalith.<Module>.csproj|module-name
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
      HEXALITH_ZOT_USERNAME: ${{ secrets.HEXALITH_ZOT_USERNAME }}
      HEXALITH_ZOT_API_KEY: ${{ secrets.HEXALITH_ZOT_API_KEY }}
```

The recommended organization-level values are:

```text
vars.HEXALITH_ZOT_REGISTRY = registry.hexalith.com
vars.HEXALITH_BUILDS_RELEASE_SHA
vars.HEXALITH_RELEASE_AUTHORITY_URL
secrets.HEXALITH_ZOT_USERNAME
secrets.HEXALITH_ZOT_API_KEY
secrets.NUGET_API_KEY
```

`secrets: inherit` also works but grants the called workflow every organization
and repository secret; prefer the explicit mapping shown above.

## Version Reference

Use `Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@main` from
consuming repositories. See `ci-cd-standards.md` for shared CI/CD policy.
