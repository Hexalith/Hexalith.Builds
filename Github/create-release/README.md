# Create Release GitHub Action

Runs semantic-release for repositories that need versioning, changelog updates,
Git tags, and GitHub releases, but do not build or publish NuGet packages from
this action.

## Inputs

None.

## Outputs

None.

## Steps

1. Set up Node.js with `actions/setup-node@v6` and `node`.
2. Install npm dependencies with `npm ci`.
3. Verify npm package provenance and signatures with `npm audit signatures`.
4. Run `npx semantic-release`.

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `GITHUB_TOKEN` | Yes | Token used by semantic-release to update the changelog commit, create tags, and create the GitHub release. |

## Requirements

- A committed `package-lock.json`, because the action uses `npm ci`.
- A `package.json`, `.releaserc`, or `release.config.*` file that configures
  semantic-release.
- Workflow permissions that allow writing contents and, when needed, issues and
  pull requests.

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
```

## Versioning

semantic-release determines the next version from Conventional Commits:

- `fix:` commits trigger a patch release.
- `feat:` commits trigger a minor release.
- Commits with a `BREAKING CHANGE:` footer trigger a major release.
