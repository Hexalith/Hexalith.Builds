# Publish Application Containers to Registry

Builds and pushes the Hexalith Web and API server containers to a container
registry using .NET container publishing.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `app-id` | Short application name used as the container repository prefix. | Yes | - |
| `version` | Version tag applied to the images. | Yes | - |
| `registry` | Container registry host, such as `ghcr.io` or `myregistry.azurecr.io`. | Yes | - |
| `username` | Registry username. | Yes | - |
| `password` | Registry password or token. | Yes | - |

## Steps

1. Initialize the `HexalithApp` and `Hexalith.Builds` submodules.
2. Check out and pull `main` inside both submodules.
3. Log in to the target registry with `docker/login-action@master`.
4. Publish the `HexalithApp.WebServer` project as a Linux x64 container.
5. Publish the `HexalithApp.ApiServer` project as a Linux x64 container.

Each image is tagged with the supplied version and `latest`.

## Image Names

The action publishes two repositories:

| Project | Repository |
|---------|------------|
| `HexalithApp.WebServer` | `{app-id}web` |
| `HexalithApp.ApiServer` | `{app-id}api` |

For example, with `app-id: myapp`, `registry: ghcr.io`, and
`version: 1.2.3`, the action publishes:

- `ghcr.io/myappweb:1.2.3`
- `ghcr.io/myappweb:latest`
- `ghcr.io/myappapi:1.2.3`
- `ghcr.io/myappapi:latest`

## Expected Project Structure

```text
HexalithApp/
+-- src/
    +-- HexalithApp.WebServer/
    |   +-- HexalithApp.WebServer.csproj
    +-- HexalithApp.ApiServer/
        +-- HexalithApp.ApiServer.csproj
```

## Usage

```yaml
- name: Publish application containers
  uses: Hexalith/Hexalith.Builds/Github/publish-container-to-registry@main
  with:
    app-id: myapp
    version: ${{ github.ref_name }}
    registry: ghcr.io
    username: ${{ github.actor }}
    password: ${{ secrets.GITHUB_TOKEN }}
```

## Complete Workflow Example

```yaml
name: Build and Publish Containers

on:
  push:
    tags:
      - 'v*'

jobs:
  publish-containers:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - name: Checkout code
        uses: actions/checkout@v5

      - name: Initialize .NET
        uses: Hexalith/Hexalith.Builds/Github/initialize-dotnet@main
        with:
          dotnet-version: '10.0.300'

      - name: Publish application containers
        uses: Hexalith/Hexalith.Builds/Github/publish-container-to-registry@main
        with:
          app-id: myapp
          version: ${{ github.ref_name }}
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
```

## Prerequisites

- The consuming repository must declare `HexalithApp` and `Hexalith.Builds` as
  root-level submodules.
- A .NET SDK compatible with the Web/API server projects must be installed
  before this action runs.
- Docker must be available on the runner.
- Registry credentials must have permission to push the target repositories.

## Notes

- The action currently moves the `HexalithApp` and `Hexalith.Builds` submodules
  to the latest `main` branch before publishing.
- The action uses .NET's `/t:PublishContainer` target with Release
  configuration, Linux OS, and x64 architecture.
- Any registry supported by Docker login can be used, including GitHub
  Container Registry, Azure Container Registry, Docker Hub, and private
  registries.
