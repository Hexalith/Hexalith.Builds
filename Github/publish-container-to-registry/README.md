# Publish Application Containers to Registry

## Overview

This GitHub Action publishes Hexalith application containers to a specified container registry. It builds and pushes both Web and API server containers using .NET's built-in container publishing capabilities.

The action automatically:

- Authenticates with the target container registry
- Builds and publishes Web server containers
- Builds and publishes API server containers
- Tags containers with the specified version and 'latest' tag
- Pushes containers to the registry

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `app-id` | The short name of the application | Yes | - |
| `version` | Version number for the containers (e.g., "1.0.0") | Yes | - |
| `registry` | Container registry URL (e.g., "ghcr.io", "docker.io") | Yes | - |
| `username` | Username for the container registry | Yes | - |
| `password` | Password or token for the container registry | Yes | - |

## Functionality

The action performs the following steps:

1. **Registry Authentication**: Logs into the specified container registry using provided credentials
2. **Container Publishing**: For each application type (Web and API):
   - Locates the project file in the HexalithApp source directory
   - Publishes the container using `dotnet publish` with container-specific parameters
   - Tags the container with both the specified version and 'latest'
   - Pushes the container to the registry

### Container Configuration

- **OS**: Linux
- **Architecture**: x64
- **Configuration**: Release
- **Tags**: Version tag + 'latest' tag
- **Repository naming**: Uses the pattern `{app-id}{apptype}` (lowercase)

## Usage Example

```yaml
- name: Publish Application Containers
  uses: ./.github/actions/publish-container-app
  with:
    app-id: myapp
    version: ${{ github.ref_name }}
    registry: ghcr.io
    username: ${{ secrets.REGISTRY_USERNAME }}
    password: ${{ secrets.REGISTRY_TOKEN }}
```

### Complete Workflow Example

```yaml
name: Build and Publish Containers

on:
  push:
    tags:
      - 'v*'

jobs:
  publish-containers:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
          
      - name: Publish Application Containers
        uses: ./.github/actions/publish-container-app
        with:
          app-id: myapp
          version: ${{ github.ref_name }}
          registry: ghcr.io
          username: ${{ secrets.REGISTRY_USERNAME }}
          password: ${{ secrets.REGISTRY_TOKEN }}
```

## How It Works

1. **Authentication**: Uses `docker/login-action@v3` to authenticate with the container registry
2. **Container Building**: Leverages .NET's native container publishing capabilities via `dotnet publish` with the `/t:PublishContainer` target
3. **Project Discovery**: Automatically locates project files in the `./HexalithApp/src/` directory structure
4. **Multi-Container Support**: Publishes both Web and API server containers in a single action
5. **Tagging Strategy**: Applies both version-specific and 'latest' tags for flexible deployment

### Project Structure Expected

```text
HexalithApp/
└── src/
    ├── HexalithApp.WebServer/
    │   └── HexalithApp.WebServer.csproj
    └── HexalithApp.ApiServer/
        └── HexalithApp.ApiServer.csproj
```

## Prerequisites

- **.NET 8.0+**: The action requires .NET 8.0 or later for container publishing
- **Docker**: Container runtime must be available (typically provided by GitHub Actions runners)
- **Registry Access**: Valid credentials for the target container registry
- **Project Structure**: HexalithApp projects must be present in the expected directory structure

### Supported Registries

The action supports any container registry that can be accessed via Docker login, including:

- Azure Container Registry (azurecr.io)
