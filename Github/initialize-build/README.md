# Initialize Build GitHub Action

## Overview
This GitHub Action initializes the build environment for a project that uses
Git submodules declared in the repository root `.gitmodules` file. It handles
the initialization and update of those submodules, ensuring that the build
process has access to the necessary build configuration files, source
references, and scripts.

## Functionality

The action performs the following step:

1. **Initialize Root-Declared Submodules**:
   - Executes `git -c submodule.recurse=false submodule update --init` to
     initialize and update only the submodules declared in the root
     `.gitmodules` file

The action does not use `--recursive` or `--remote`, and it leaves submodules nested inside other submodules uninitialized.

## Usage Example

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@main
        
      - name: Initialize build environment
        uses: ./Github/initialize-build
        
      - name: Additional build steps
        run: |
          # Your build commands here
```

## How It Works

This action is designed for projects that use the Hexalith.Builds repository
at `references/Hexalith.Builds`, and optionally other source dependencies, as
root-declared Git submodules. The Hexalith.Builds submodule contains common
build properties, package references, and version information.

By initializing and updating the submodule, this action ensures that:

1. All necessary root-declared submodule files are available
2. The build process uses consistent settings across different repositories
3. Builds use the submodule commits recorded by the parent repository

This approach simplifies maintenance of build configurations across multiple projects and ensures consistency in the build process.

## Prerequisites

- The repository must declare its required submodules in the root
  `.gitmodules` file
- The workflow must include a checkout step before using this action
