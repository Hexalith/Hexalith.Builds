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
| `environment-name` | No | `production` | Protected caller-repository environment that supplies human release approval. |
| `publish-containers` | No | `false` | Whether to prepare semantic-release container publishing for .NET SDK container projects. |
| `container-projects` | No | `''` | Newline-separated container mappings in `path/to/project.csproj|repository-name` format. Required when `publish-containers` is `true`. |
| `builds-execution-sha` | Yes | - | Exact maintainer-approved Builds commit. It must equal the reusable workflow's full-SHA `uses` revision and is checked against nested action/helper bytes. |
| `source-branch` | No | `main` | Protected source branch revalidated at every publication boundary. |
| `source-ci-workflow` | No | `ci.yml` | Workflow filename whose successful exact-source `push` run authorizes the source. |
| `package-manifest` | No | `tools/release-packages.json` | Caller package manifest frozen into publication identity. |

## Protected environment and caller secrets

| Caller secret | Required | Description |
|--------|----------|-------------|
| `NUGET_API_KEY` | Yes | API key used by semantic-release package publishing. |
| `HEXALITH_ZOT_USERNAME` | No | Hexalith Zot username used when `publish-containers` is `true`. |
| `HEXALITH_ZOT_API_KEY` | No | Hexalith Zot API key used when `publish-containers` is `true`. |

Store these credentials at caller repository or organization scope and map only
the three declared names explicitly. Do not use `secrets: inherit`. The reusable
publication job references the protected environment named by
`environment-name`, so GitHub does not start the job or expose its explicitly
passed credentials until environment protection passes. The environment does
not need duplicate credential values.

The workflow also uses the caller repository `GITHUB_TOKEN` for semantic-release
GitHub operations. Container publishing uses the organization variable
`HEXALITH_ZOT_REGISTRY`; when it is not set, the workflow defaults to
`registry.hexalith.com`.

## Steps

1. Wait for required protection on the configured environment, check out the
   caller repository with full history and no persisted credentials, verify the resolved
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
10. Run `npx semantic-release`, passing the approved Builds identity, source
    proof inputs, package manifest, and protected environment name to the
    caller's publication preflight; always
    upload the complete hidden release-evidence directory afterward.

The generated publish helper accepts the semantic-release version as its first
argument. After environment approval, the caller's `verifyReleaseCmd` must
re-prove exact current `main` and successful exact-SHA push CI, freeze exact
repository, version, source proof, Builds, environment, run, helper, and
canonical package-manifest identity, and prove destination absence before
semantic-release creates a Git tag. The `publishCmd` must require exact
frozen-identity equality, repeat the live source proof, and revalidate
immediately before the first NuGet push, then call the container helper with the
same version. The helper requires both earlier phases and repeats the live
source proof and container absence immediately before publication. Existing
package/tag identities are collisions; duplicate skipping is forbidden. The
helper logs in to Hexalith Zot only when semantic-release reaches `publishCmd`:

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
      actions: read
      contents: write
      issues: write
      pull-requests: write
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@0123456789abcdef0123456789abcdef01234567
    with:
      solution: Hexalith.<Module>.slnx
      environment-name: production
      publish-containers: true
      builds-execution-sha: 0123456789abcdef0123456789abcdef01234567
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
caller repository/organization: secrets.HEXALITH_ZOT_USERNAME
caller repository/organization: secrets.HEXALITH_ZOT_API_KEY
caller repository/organization: secrets.NUGET_API_KEY
```

Pass exactly these declared names. The job-level protected environment gates
their use without requiring a second credential copy.

The SHA above is a placeholder: replace both occurrences with the same reviewed
40-character Builds commit. Do not substitute a branch, tag, expression, or
repository variable. The caller's `references/Hexalith.Builds` gitlink remains
an independent development dependency and need not equal the executed release
tool SHA. See `ci-cd-standards.md` for shared CI/CD policy.
