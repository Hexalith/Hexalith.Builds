# Create Release GitHub Action

## Overview
This GitHub Action automates the release process using semantic versioning. It leverages the semantic-release framework to analyze commit messages, determine the appropriate version bump, create GitHub releases, and generate changelogs automatically.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `dry_run` | If true, the release process will be simulated without making any changes | No | `false` |

## Outputs

| Output | Description |
|--------|-------------|
| `version` | The new release version (e.g., 1.2.3) |
| `major` | The major version number (e.g., 1) |
| `minor` | The minor version number (e.g., 2) |
| `patch` | The patch version number (e.g., 3) |
| `published` | Boolean indicating whether a new release was published |

## Functionality

The action performs the following steps:

1. **Handle Pull Requests**:
   - If triggered by a pull request, temporarily merges the PR branch to analyze its commits
   - Configures Git user information for the merge operation

2. **Semantic Release Process**:
   - Uses the `cycjimmy/semantic-release-action@v4` action
   - Includes additional plugins for Git integration and changelog generation
   - Determines the version bump based on conventional commit messages
   - Creates a GitHub release with appropriate tags
   - Generates or updates the changelog

3. **Output Information**:
   - Displays the new release version and whether a release was published
   - Makes version information available as outputs for subsequent workflow steps

## Usage Example

```yaml
jobs:
  release:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        with:
          fetch-depth: 0
          
      - name: Create release
        id: create_release
        uses: ./Github/create-release
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          
      - name: Use version in subsequent steps
        if: steps.create_release.outputs.published == 'true'
        run: echo "New version released: ${{ steps.create_release.outputs.version }}"
```

## How It Works

This action uses semantic-release to automate version management and release creation:

1. It analyzes commit messages since the last release to determine the appropriate version bump:
   - `fix:` commits trigger a patch bump (0.0.x)
   - `feat:` commits trigger a minor bump (0.x.0)
   - `BREAKING CHANGE:` commits trigger a major bump (x.0.0)

2. When running in a pull request context, it performs a temporary merge to analyze what would happen if the PR were merged, without actually creating a release.

3. When running in the main branch, it creates a proper release with appropriate tags and changelog updates.

The action requires a properly configured `.releaserc` or `release.config.js` file in your repository to define the semantic-release configuration.