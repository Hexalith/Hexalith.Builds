# Get Hexalith Version GitHub Action

## Overview
This GitHub Action retrieves version information using semantic-release in dry-run mode. It analyzes commit history to determine the next semantic version without actually creating a release, making it useful for build and CI processes that need version information.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `version-path` | Path to the Hexalith.Version.props file | No | `./Hexalith.Builds/Hexalith.Version.props` |

## Outputs

| Output | Description |
|--------|-------------|
| `version` | The complete version string (e.g., 1.2.3) |
| `major` | The major version number (e.g., 1) |
| `minor` | The minor version number (e.g., 2) |
| `patch` | The patch version number (e.g., 3) |
| `published` | Boolean indicating whether a new release would be published (always false in dry-run mode) |

## Functionality

The action performs the following steps:

1. **Semantic Version Analysis**:
   - Uses the `cycjimmy/semantic-release-action@v4` action in dry-run mode
   - Analyzes commit messages since the last release to determine the next version
   - Does not create any tags or releases

2. **Output Version Information**:
   - Displays the calculated version information
   - Makes version components available as outputs for subsequent workflow steps

## Usage Example

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        with:
          fetch-depth: 0
          
      - name: Get version
        id: version
        uses: ./Github/version
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          
      - name: Build with version
        run: |
          echo "Building version ${{ steps.version.outputs.version }}"
          dotnet build -p:Version=${{ steps.version.outputs.version }}
```

## How It Works

This action leverages semantic-release to determine the next version number based on conventional commit messages, but runs in dry-run mode to avoid actually creating a release:

1. It analyzes commit messages since the last release to determine the appropriate version bump:
   - `fix:` commits would trigger a patch bump (0.0.x)
   - `feat:` commits would trigger a minor bump (0.x.0)
   - `BREAKING CHANGE:` commits would trigger a major bump (x.0.0)

2. It calculates what the next version would be without creating any tags or releases

3. It makes this version information available as outputs that can be used in subsequent steps of your workflow

This approach allows you to use semantic versioning in your build process without actually creating releases, which is particularly useful for:
- CI builds that need version information
- Preview or development builds
- Testing the release process

## Prerequisites

- The repository must use conventional commit messages for proper version calculation
- A properly configured `.releaserc` or `release.config.js` file should be present in your repository
- The workflow must include a checkout step with `fetch-depth: 0` to access the full commit history