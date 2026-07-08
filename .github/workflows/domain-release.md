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
| `test-projects` | No | `''` | Newline-separated test project paths to run before release. |
| `node-version` | No | `lts/*` | Node.js version passed to `actions/setup-node`. |
| `timeout-minutes` | No | `20` | Timeout for the release job. |
| `publish-containers` | No | `false` | Whether to prepare semantic-release container publishing for .NET SDK container projects. |
| `container-projects` | No | `''` | Newline-separated container mappings in `path/to/project.csproj|repository-name` format. Required when `publish-containers` is `true`. |

## Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `NUGET_API_KEY` | Yes | API key used by semantic-release package publishing. |
| `HEXALITH_ZOT_USERNAME` | No | Hexalith Zot username used when `publish-containers` is `true`. Usually supplied through organization secrets and `secrets: inherit`. |
| `HEXALITH_ZOT_API_KEY` | No | Hexalith Zot API key used when `publish-containers` is `true`. Usually supplied through organization secrets and `secrets: inherit`. |

The workflow also uses the caller repository `GITHUB_TOKEN` for semantic-release
GitHub operations. Container publishing uses the organization variable
`HEXALITH_ZOT_REGISTRY`; when it is not set, the workflow defaults to
`registry.hexalith.com`.

## Steps

1. Check out the caller repository with full history and submodules.
2. Initialize .NET with `Github/initialize-dotnet`.
3. Set up Node.js.
4. Cache NuGet packages.
5. Run `npm ci`.
6. Restore and build the solution in Release configuration with warnings as
   errors.
7. If `test-projects` is not empty, initialize Dapr and run each test project.
8. If `publish-containers` is `true`, log in to Hexalith Zot and create
   `.hexalith/release/publish-containers.sh` in the caller workspace.
9. Run `npx semantic-release`.

The generated publish helper accepts the semantic-release version as its first
argument. Call it from the caller repository semantic-release `publishCmd` after
the NuGet publish command:

```json
"publishCmd": "dotnet nuget push ./nupkgs/*.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json --skip-duplicate && ./.hexalith/release/publish-containers.sh ${nextRelease.version}"
```

## Usage

```yaml
jobs:
  release:
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@main
    with:
      solution: Hexalith.<Module>.slnx
      test-projects: |
        tests/Hexalith.<Module>.Server.Tests
        tests/Hexalith.<Module>.IntegrationTests
      publish-containers: true
      container-projects: |
        src/Hexalith.<Module>/Hexalith.<Module>.csproj|module-name
    secrets: inherit
```

The recommended organization-level values are:

```text
vars.HEXALITH_ZOT_REGISTRY = registry.hexalith.com
secrets.HEXALITH_ZOT_USERNAME
secrets.HEXALITH_ZOT_API_KEY
secrets.NUGET_API_KEY
```

For repositories that do not inherit organization secrets, map the required
secrets explicitly instead:

```yaml
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
      HEXALITH_ZOT_USERNAME: ${{ secrets.HEXALITH_ZOT_USERNAME }}
      HEXALITH_ZOT_API_KEY: ${{ secrets.HEXALITH_ZOT_API_KEY }}
```

## Version Reference

Use `Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@main` from
consuming repositories. See `ci-cd-standards.md` for shared CI/CD policy.
