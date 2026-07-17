# Create Release GitHub Action

> **Deprecated (legacy pipeline generation).** New Hexalith modules should use the
> reusable `domain-release.yml` workflow instead (see `.github/workflows/`). This action is kept
> for existing consumers and receives maintenance fixes only.

Runs the repository's configured semantic-release lifecycle. The lifecycle
always handles versioning, changelog updates, Git tags, and GitHub releases;
repositories may additionally configure package preparation or publication
through their own semantic-release plugins. This action does not hard-code a
package format, registry, or product-specific release script.

## Inputs

None.

## Outputs

None.

## Steps

1. Set up Node.js with `actions/setup-node@v6` and `node`.
2. Install npm dependencies with `npm ci`.
3. Verify npm package provenance and signatures with `npm audit signatures`.
4. Run `npx semantic-release` with the caller's `GITHUB_TOKEN` and optional
   `NUGET_API_KEY` available to repository-configured plugins.

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `GITHUB_TOKEN` | Yes | Token used by semantic-release to update the changelog commit, create tags, and create the GitHub release. |
| `NUGET_API_KEY` | Only for configured NuGet.org publication | Passed through unchanged for a repository semantic-release lifecycle that publishes stable NuGet packages. |

## Requirements

- A committed `package-lock.json`, because the action uses `npm ci`.
- A `package.json`, `.releaserc`, or `release.config.*` file that configures
  semantic-release.
- Any package lifecycle is configured by that repository (for example through
  `@semantic-release/exec`); this shared action only invokes semantic-release.
- Workflow permissions that allow writing contents and, when needed, issues and
  pull requests. Add `packages: write` only when the repository lifecycle
  publishes to GitHub Packages.

## Usage

```yaml
jobs:
  release:
    runs-on: ubuntu-latest
    permissions:
      contents: write
      issues: write
      pull-requests: write
    steps:
      - name: Checkout code
        uses: actions/checkout@v5
        with:
          fetch-depth: 0

      - name: Create release
        uses: Hexalith/Hexalith.Builds/Github/create-release@main
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
```

## Versioning

semantic-release determines the next version from Conventional Commits:

- `fix:` commits trigger a patch release.
- `feat:` commits trigger a minor release.
- Commits with a `BREAKING CHANGE:` footer trigger a major release.
