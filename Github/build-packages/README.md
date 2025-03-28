# Build Packages GitHub Action

## Overview
This GitHub Action is designed to build all .NET projects located in the `src` directory of your repository. It automatically determines the build configuration based on the version number and applies the specified version to all projects during the build process.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `version` | Version number for the build (e.g., 1.0.0 or 1.0.0-preview) | Yes | N/A |

## Functionality

The action performs the following steps:

1. **Determine Build Configuration**:
   - Analyzes the provided version number
   - Sets the build configuration to `Release` for stable versions (without hyphens)
   - Sets the build configuration to `Debug` for preview versions (containing hyphens)

2. **Build .NET Projects**:
   - Recursively finds all `.csproj` files in the `./src` directory
   - Builds each project with the determined configuration
   - Applies the specified version number to both the assembly version and file version

## Usage Example

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: 9.0.x
          
      - name: Build packages
        uses: ./Github/build-packages
        with:
          version: 1.0.0
```

## How It Works

The action uses PowerShell to:
1. Determine the appropriate build configuration based on the version format
2. Find all C# project files in the source directory
3. Build each project with the correct configuration and version information

This approach ensures consistent versioning across all projects in your solution and automatically adjusts the build configuration based on whether you're building a release or preview version.