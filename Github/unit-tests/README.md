# Run Unit Tests GitHub Action

## Overview
This GitHub Action automates the process of running unit tests for a specific project and cleaning up the solution afterward. It's designed to work with the standard test project structure where test projects are located in the `test` directory and follow the naming convention `{ProjectName}.Tests`.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `project-name` | The name of the project to test | Yes | N/A |

## Functionality

The action performs the following steps:

1. **Run Unit Tests**:
   - Executes the unit tests for the specified project using `dotnet test`
   - Uses the Release configuration for optimal performance
   - Targets the test project at `./test/{project-name}.Tests/{project-name}.Tests.csproj`

2. **Clean Solution**:
   - Cleans the test project using `dotnet clean`
   - Removes build artifacts and temporary files
   - Ensures a clean state for subsequent operations

## Usage Example

```yaml
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: 9.0.x
          
      - name: Run unit tests for Core project
        uses: ./Github/unit-tests
        with:
          project-name: Core
```

## How It Works

This action assumes a specific project structure where:

1. Test projects are located in the `test` directory
2. Test projects follow the naming convention `{ProjectName}.Tests`
3. The test project contains a `.csproj` file with the same name

For example, if you provide `Core` as the `project-name` input, the action will:
- Run tests from `./test/Core.Tests/Core.Tests.csproj`
- Clean the project at `./test/Core.Tests/Core.Tests.csproj`

This standardized approach makes it easy to run tests for different projects by simply changing the `project-name` input.

## Prerequisites

- The repository must follow the expected project structure
- The test project must use a testing framework compatible with `dotnet test` (such as xUnit, NUnit, or MSTest)
- The .NET SDK must be installed in the runner environment