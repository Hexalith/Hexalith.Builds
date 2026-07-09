# Build Packages Action

> **Deprecated (legacy pipeline generation).** New Hexalith modules should use the
> reusable `domain-release.yml` workflow instead (see `.github/workflows/`). This action is kept
> for existing consumers and receives maintenance fixes only.

This GitHub Action builds and releases packages for Hexalith .NET projects using
[semantic-release](https://semantic-release.org) directly (the official CLI; no
third-party wrapper action).

It is the **release** half of a two-job pipeline: run the
[`verify`](../verify) action first (on pull requests and pushes) to build and
test, then run this action on pushes to release branches. This action does not
re-run the tests.

It runs semantic-release **once**: the version is computed from
[Conventional Commits](https://www.conventionalcommits.org), then the .NET build
and NuGet publish are driven from the semantic-release lifecycle via
[`@semantic-release/exec`](https://github.com/semantic-release/exec). The build
and publish only happen when a release is actually warranted, so there is no
separate "is a release published?" gate.

## Inputs

None.

## Outputs

None. semantic-release performs the full release (version, changelog, git
commit/tag, NuGet publish, and GitHub release) in a single pass.

## Steps

1. **Checkout** (`actions/checkout@v5`, `fetch-depth: 0`) - full history is required by semantic-release.
2. **Initialize build submodules** (`Github/initialize-build`) - checks out root-declared submodules such as `references/Hexalith.Builds`.
3. **Initialize .NET** (`Github/initialize-dotnet`).
4. **Setup Node.js** (`actions/setup-node@v6`, `node`).
5. **Install semantic-release dependencies** (`npm ci` - requires a committed `package-lock.json`).
6. **Verify dependency provenance and signatures** (`npm audit signatures`).
7. **Semantic Release** (`npx semantic-release`) - one pass:
   - analyze commits and calculate the next version
   - `@semantic-release/changelog` updates `CHANGELOG.md`
   - `@semantic-release/exec` `prepareCmd` runs `scripts/build-packages.ps1`
   - `@semantic-release/git` commits `CHANGELOG.md`
   - `@semantic-release/exec` `publishCmd` runs `scripts/publish-packages.ps1`
   - `@semantic-release/github` creates the GitHub release

## Environment Variables

- `GITHUB_TOKEN`: GitHub token (release, changelog commit, GitHub Packages for pre-releases).
- `NUGET_API_KEY`: API key for publishing stable releases to NuGet.org.

## Consuming repository requirements

- A `package.json` whose `release` field extends the shared config and sets `branches`:

  ```json
  "release": {
    "extends": "./references/Hexalith.Builds/Github/package-release/release.config.json",
    "branches": [ "main", "next", "next-major",
      { "name": "beta", "prerelease": true }, { "name": "alpha", "prerelease": true } ]
  }
  ```

- These `devDependencies`: `semantic-release`, `@semantic-release/changelog`,
  `@semantic-release/exec`, `@semantic-release/git`, `@semantic-release/github`.
- A committed `package-lock.json` (run `npm install` once and commit it) so the
  action can install with `npm ci` for reproducible, verifiable builds.
- The `Hexalith.Builds` submodule mounted at `references/Hexalith.Builds`.

## Example Usage

```yaml
on:
  push:
    branches: [main, next, next-major, alpha, beta, '[0-9]+.[0-9]+.x']
  pull_request:
    branches: [main, next, next-major, alpha, beta]

permissions:
  contents: read

jobs:
  verify:
    runs-on: ubuntu-latest
    steps:
    - uses: Hexalith/Hexalith.Builds/Github/verify@main
      with:
        project-name: ${{ github.event.repository.name }}

  release:
    runs-on: ubuntu-latest
    needs: verify
    if: github.event_name == 'push'
    permissions:
      contents: write
      issues: write
      pull-requests: write
      packages: write
    steps:
    - uses: Hexalith/Hexalith.Builds/Github/package-release@main
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```
