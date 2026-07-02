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
| `dapr-version` | No | `1.17.0` | Dapr version used when tests are enabled. |
| `test-projects` | No | `''` | Newline-separated test project paths to run before release. |
| `node-version` | No | `lts/*` | Node.js version passed to `actions/setup-node`. |
| `timeout-minutes` | No | `20` | Timeout for the release job. |

## Secrets

| Secret | Required | Description |
|--------|----------|-------------|
| `NUGET_API_KEY` | Yes | API key used by semantic-release package publishing. |

The workflow also uses the caller repository `GITHUB_TOKEN` for semantic-release
GitHub operations.

## Steps

1. Check out the caller repository with full history and submodules.
2. Initialize .NET with `Github/initialize-dotnet`.
3. Set up Node.js.
4. Cache NuGet packages.
5. Run `npm ci`.
6. Restore and build the solution in Release configuration with warnings as
   errors.
7. If `test-projects` is not empty, initialize Dapr and run each test project.
8. Run `npx semantic-release`.

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
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```

## Version Reference

Use a versioned Hexalith.Builds tag or full commit SHA for reproducible module
release pipelines. See `ci-cd-standards.md` for shared CI/CD policy.
