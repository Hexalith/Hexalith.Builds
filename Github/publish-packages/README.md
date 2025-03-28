# Publish NuGet Packages GitHub Action

## Overview
This GitHub Action automates the process of publishing NuGet packages to either NuGet.org (for release versions) or GitHub Packages (for preview versions). It intelligently determines the appropriate destination based on the version number format, following the convention that versions with hyphens (e.g., 1.0.0-preview) are preview packages.

## Inputs

| Input | Description | Required |
|-------|-------------|----------|
| `version` | Version number for the packages (e.g., 1.0.0 or 1.0.0-preview) | Yes |
| `nuget-api-key` | NuGet API Key for publishing to NuGet.org | Yes |
| `github-token` | GitHub token for publishing to GitHub Packages | Yes |

## Functionality

The action performs the following steps:

1. **Version Analysis**:
   - Examines the provided version number to determine if it's a preview or release version
   - Preview versions contain a hyphen (e.g., 1.0.0-preview, 1.0.0-beta.1)
   - Release versions do not contain a hyphen (e.g., 1.0.0, 2.1.3)

2. **Package Publishing**:
   - For preview versions:
     - Publishes packages to GitHub Packages repository
     - Uses the provided GitHub token for authentication
     - Targets the repository-specific NuGet feed URL
   
   - For release versions:
     - Publishes packages to NuGet.org
     - Uses the provided NuGet API key for authentication
     - Targets the official NuGet.org feed

3. **Package Discovery**:
   - Automatically finds all `.nupkg` files in the `./src` directory and its subdirectories
   - Uses the `--skip-duplicate` flag to avoid errors when a package version already exists

## Usage Example

```yaml
jobs:
  publish:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: 9.0.x
          
      - name: Build packages
        run: dotnet pack -c Release
        
      - name: Publish packages
        uses: ./Github/publish-packages
        with:
          version: 1.0.0-preview.1
          nuget-api-key: ${{ secrets.NUGET_API_KEY }}
          github-token: ${{ secrets.GITHUB_TOKEN }}
```

## How It Works

This action implements a dual-publishing strategy based on semantic versioning conventions:

1. **Preview Packages**:
   - Identified by the presence of a hyphen in the version number
   - Published to GitHub Packages, which is ideal for pre-release testing
   - The GitHub Packages URL is specific to the repository: `https://nuget.pkg.github.com/Hexalith/index.json`

2. **Release Packages**:
   - Identified by the absence of a hyphen in the version number
   - Published to NuGet.org, making them publicly available
   - Uses the standard NuGet.org endpoint: `https://api.nuget.org/v3/index.json`

This approach ensures that preview packages remain within the GitHub ecosystem for internal testing, while release packages are properly published to the public NuGet registry.

## Prerequisites

- NuGet packages must be built and available in the `./src` directory or its subdirectories
- A valid NuGet API key must be provided for publishing to NuGet.org
- A valid GitHub token with appropriate permissions must be provided for publishing to GitHub Packages