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
| `test-projects` | No | `''` | Newline-separated test project paths to run before release. Leave empty when the caller already proved exact-source CI success. |
| `node-version` | No | `node` | Node.js version passed to `actions/setup-node`. |
| `timeout-minutes` | No | `20` | Timeout for the release job. |
| `environment-name` | No | `production` | Protected caller-repository environment that supplies human release approval and release secrets. |
| `publish-containers` | No | `false` | Whether to prepare semantic-release container publishing for .NET SDK container projects. |
| `container-projects` | No | `''` | Newline-separated container mappings in `path/to/project.csproj|repository-name` format. Required when `publish-containers` is `true`. |
| `builds-execution-sha` | No | `''` | Exact maintainer-approved Builds commit. Required for container publishing and checked against both the resolved reusable workflow and nested action/helper bytes. |

## Protected environment and secrets

| Environment secret | Required | Description |
|--------|----------|-------------|
| `NUGET_API_KEY` | Yes | API key used by semantic-release package publishing. |
| `HEXALITH_ZOT_USERNAME` | No | Hexalith Zot username used when `publish-containers` is `true`. |
| `HEXALITH_ZOT_API_KEY` | No | Hexalith Zot API key used when `publish-containers` is `true`. |

Store these credentials in the protected environment named by
`environment-name`. The reusable job reads them only after environment
protection passes; callers do not pass repository or organization publication
credentials and must not use `secrets: inherit`.

The workflow also uses the caller repository `GITHUB_TOKEN` for semantic-release
GitHub operations. Container publishing uses the organization variable
`HEXALITH_ZOT_REGISTRY`; when it is not set, the workflow defaults to
`registry.hexalith.com`.

## Steps

1. Wait for required protection on the configured environment, check out the
   caller repository with full history, verify the resolved
   reusable-workflow identity, check out that exact Builds commit, then
   initialize root-declared submodules through its local `initialize-build`
   action.
2. Initialize .NET with `actions/setup-dotnet` from `global.json`.
3. Set up Node.js.
4. Cache NuGet packages.
5. Run `npm ci` and `npm audit signatures`.
6. Restore and build the solution in Release configuration with warnings as
   errors.
7. If `test-projects` is not empty, initialize Dapr, run each test project, and
   upload TRX/coverage evidence (`release-test-results`, `if: always()`).
8. If `publish-containers` is `true`, run the SHA-pinned arm64 emulation setup.
9. Invoke the nested
   `Github/publish-containers` action from the immutable local checkout, and
   install the publisher, immutable OCI validator, publication preflight,
   and child-digest smoke helpers. The action also compares its action/helper
   bytes with the same approved Builds commit.
10. Run `npx semantic-release`, passing the approved Builds identity and
    protected environment name to the caller's publication preflight; always
    upload the complete hidden release-evidence directory afterward.

The generated publish helper accepts the semantic-release version as its first
argument. The caller's `verifyReleaseCmd` must freeze exact repository,
version, source, Builds, environment, run, and helper identity and prove
destination absence before semantic-release creates a Git tag. The `publishCmd`
must require exact frozen-identity equality and revalidate immediately before
the first NuGet push, then call the container helper with the same version. The
helper requires both earlier phases and repeats container absence immediately
before publication. Existing package/tag identities are collisions; duplicate
skipping is forbidden. The helper logs in to Hexalith Zot only when
semantic-release reaches `publishCmd`:

```json
"verifyReleaseCmd": "bash scripts/validate-publication-preflight.sh ${nextRelease.version} verify >&2",
"publishCmd": "bash scripts/validate-publication-preflight.sh ${nextRelease.version} publish >&2 && dotnet nuget push ./nupkgs/*.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json && ./.hexalith/release/publish-containers.sh ${nextRelease.version}"
```

## Usage

The standard caller is manually dispatched. A caller-owned preflight must prove
the selected SHA is the current `main` tip with successful exact-SHA push CI
before the reusable protected-environment job is called (see
`ci-cd-standards.md`, "Release Gates"):

```yaml
on:
  workflow_dispatch:

concurrency:
  group: release-production
  cancel-in-progress: false

permissions:
  contents: read

jobs:
  verify-source:
    runs-on: ubuntu-latest
    # Fail unless this is the current main SHA with successful exact-SHA push CI.

  release:
    needs: verify-source
    permissions:
      contents: write
      issues: write
      pull-requests: write
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@main
    with:
      solution: Hexalith.<Module>.slnx
      environment-name: production
      publish-containers: true
      builds-execution-sha: ${{ vars.HEXALITH_BUILDS_RELEASE_SHA }}
      container-projects: |
        src/Hexalith.<Module>/Hexalith.<Module>.csproj|module-name
```

The recommended organization-level values are:

```text
vars.HEXALITH_ZOT_REGISTRY = registry.hexalith.com
vars.HEXALITH_BUILDS_RELEASE_SHA
environment production: secrets.HEXALITH_ZOT_USERNAME
environment production: secrets.HEXALITH_ZOT_API_KEY
environment production: secrets.NUGET_API_KEY
```

Do not pass these publication credentials from repository or organization
scope. Keeping them on the protected environment makes them unreachable until
the reviewer gate passes.

## Version Reference

Use `Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@main` from
consuming repositories. See `ci-cd-standards.md` for shared CI/CD policy.
