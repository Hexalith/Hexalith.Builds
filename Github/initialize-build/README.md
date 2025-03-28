# Initialize Build GitHub Action

## Overview
This GitHub Action initializes the build environment for a project that uses the Hexalith.Builds submodule. It handles the initialization and update of the submodule, ensuring that the build process has access to the necessary build configuration files and scripts.

## Functionality

The action performs the following steps:

1. **Initialize Hexalith.Builds Submodule**:
   - Executes `git submodule init Hexalith.Builds` to initialize the submodule reference

2. **Update Hexalith.Builds Submodule**:
   - Executes `git submodule update Hexalith.Builds` to fetch the submodule content at the specified commit

3. **Change to Hexalith.Builds Directory**:
   - Navigates to the Hexalith.Builds directory to prepare for subsequent operations

4. **Checkout Main Branch**:
   - Switches to the main branch of the Hexalith.Builds submodule to ensure the latest build configuration is used

## Usage Example

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        
      - name: Initialize build environment
        uses: ./Github/initialize-build
        
      - name: Additional build steps
        run: |
          # Your build commands here
```

## How It Works

This action is designed for projects that use the Hexalith.Builds repository as a Git submodule to standardize build configurations across multiple projects. The submodule contains common build properties, package references, and version information.

By initializing and updating the submodule, this action ensures that:

1. All necessary build configuration files are available
2. The build process uses consistent settings across different repositories
3. Updates to the build configuration can be managed centrally in the Hexalith.Builds repository

This approach simplifies maintenance of build configurations across multiple projects and ensures consistency in the build process.

## Prerequisites

- The repository must have a submodule reference to Hexalith.Builds
- The workflow must include a checkout step before using this action