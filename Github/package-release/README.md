# Build Packages Action

This GitHub Action builds, tests, and releases packages for Hexalith .NET
projects using [semantic-release](https://semantic-release.org) directly (the
official CLI — no third-party wrapper action).

It runs semantic-release **once**: the version is computed from
[Conventional Commits](https://www.conventionalcommits.org), then the .NET build
and NuGet publish are driven from the semantic-release lifecycle via
[`@semantic-release/exec`](https://github.com/semantic-release/exec). The build
and publish only happen when a release is actually warranted, so there is no
separate "is a release published?" gate.

## Inputs

- `project-name` (required): The name of the project (used to locate the test project).

## Outputs

None. semantic-release performs the full release (version, changelog, git
commit/tag, NuGet publish, and GitHub release) in a single pass.

## Steps

1. **Checkout** (`actions/checkout@v5`, `fetch-depth: 0`) — full history is required by semantic-release.
2. **Initialize build submodules** (`Github/initialize-build`) — checks out `references/Hexalith.Builds`.
3. **Initialize .NET** (`Github/initialize-dotnet`).
4. **Setup Node.js** (`actions/setup-node@v4`, `lts/*`).
5. **Install semantic-release dependencies** (`npm ci` — requires a committed `package-lock.json`).
6. **Verify dependency provenance and signatures** (`npm audit signatures`).
7. **Run unit tests** (`Github/unit-tests`).
8. **Semantic Release** (`npx semantic-release`) — one pass:
   - analyze commits → next version
   - `@semantic-release/changelog` → update `CHANGELOG.md`
   - `@semantic-release/exec` `prepareCmd` → `scripts/build-packages.ps1` builds/packs the libraries
   - `@semantic-release/git` → commit `CHANGELOG.md`
   - `@semantic-release/exec` `publishCmd` → `scripts/publish-packages.ps1` pushes to NuGet.org (stable) or GitHub Packages (pre-release)
   - `@semantic-release/github` → create the GitHub release

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
jobs:
  build:
    runs-on: ubuntu-latest
    name: Build and Test
    steps:
    - name: Build and publish packages
      uses: Hexalith/Hexalith.Builds/Github/package-release@main
      with:
        project-name: ${{ github.event.repository.name }}
      env:
        GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```
