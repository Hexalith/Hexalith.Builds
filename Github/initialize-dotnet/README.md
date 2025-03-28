# Initialize .NET Framework GitHub Action

## Overview
This GitHub Action sets up the .NET 9.0 development environment and optionally installs the Aspire workload. It provides a standardized way to ensure that all necessary .NET components are available for building and testing .NET applications in your GitHub workflow.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `aspire` | Whether to install the Aspire workload. Set to any non-empty value to install. | No | `''` (empty string) |

## Functionality

The action performs the following steps:

1. **Setup .NET 9.0**:
   - Uses the official `actions/setup-dotnet@v4` action to install .NET 9.0
   - Ensures that the latest version of .NET 9.0 is available in the build environment

2. **Add Aspire Workload** (Optional):
   - If the `aspire` input is provided with a non-empty value, installs the Aspire workload
   - Executes `dotnet workload install aspire` to add the necessary components for building Aspire applications

## Usage Example

### Basic Usage (Without Aspire)

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        
      - name: Setup .NET
        uses: ./Github/initialize-dotnet
        
      - name: Build project
        run: dotnet build
```

### With Aspire Workload

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v3
        
      - name: Setup .NET with Aspire
        uses: ./Github/initialize-dotnet
        with:
          aspire: 'true'
        
      - name: Build Aspire project
        run: dotnet build
```

## How It Works

This action leverages the official .NET setup action to ensure a consistent .NET environment across different runners. It:

1. Installs the specified version of .NET (9.0.x) on the runner
2. Configures the environment variables and paths needed for .NET development
3. Optionally installs the Aspire workload, which provides additional templates and libraries for building distributed applications

The action is designed to be simple and focused, handling just the .NET setup portion of your workflow. This makes it easy to reuse across different projects and workflows.

## About .NET Aspire

.NET Aspire is a stack for building distributed applications with .NET. It provides a set of components and tools that simplify the development of cloud-native applications. When the Aspire workload is installed, you gain access to:

- Project templates for Aspire applications
- Libraries for service discovery, health checks, and resilience
- Integration with cloud services and containers

If your project uses .NET Aspire, make sure to set the `aspire` input to a non-empty value.