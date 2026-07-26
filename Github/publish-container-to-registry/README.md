# Publish Application Containers to Registry

> **Deprecated (legacy HexalithApp-era action).** Domain modules publish containers
> through the `domain-release.yml` reusable workflow and the `Github/publish-containers`
> composite action. This action also mutates submodules to branch heads at run time,
> which the current submodule standard forbids. Kept for existing consumers only.

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

1. Initialize the `HexalithApp` and `references/Hexalith.Builds` submodules at the
   gitlinks committed by the caller, so the published image is reproducible from
   the release commit.
2. Validate `version` as SemVer without build metadata, and `app-id` and
   `registry` as valid container repository and host segments.
3. Log in to the target registry with `docker/login-action`, pinned to a full
   commit SHA with a trailing version comment.
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
references/Hexalith.Builds/
```

## Usage

Reference this action by full commit SHA with a trailing version comment, and
pass a version the release process produced rather than a ref name. `github.ref_name`
carries whatever branch or tag name was pushed, so it is attacker-influenceable
and is rejected here unless it happens to be bare SemVer.

```yaml
- name: Publish application containers
  uses: Hexalith/Hexalith.Builds/Github/publish-container-to-registry@<full-40-hex-sha> # vX.Y.Z
  with:
    app-id: myapp
    version: ${{ needs.build.outputs.version }}
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
        uses: actions/checkout@<full-40-hex-sha> # v7.0.1
        with:
          persist-credentials: false

      - name: Initialize .NET
        uses: Hexalith/Hexalith.Builds/Github/initialize-dotnet@<full-40-hex-sha> # vX.Y.Z
        with:
          dotnet-version: '10.0.302'

      - name: Resolve the release version
        id: release-version
        env:
          REF_NAME: ${{ github.ref_name }}
        run: |
          set -euo pipefail
          version="${REF_NAME#v}"
          [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] ||
            { echo "Tag '$REF_NAME' is not a SemVer release tag." >&2; exit 1; }
          echo "version=$version" >> "$GITHUB_OUTPUT"

      - name: Publish application containers
        uses: Hexalith/Hexalith.Builds/Github/publish-container-to-registry@<full-40-hex-sha> # vX.Y.Z
        with:
          app-id: myapp
          version: ${{ steps.release-version.outputs.version }}
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
```

## Prerequisites

- The consuming repository must declare `HexalithApp` and
  `references/Hexalith.Builds` as root-declared submodules.
- A .NET SDK compatible with the Web/API server projects must be installed
  before this action runs.
- Docker must be available on the runner.
- Registry credentials must have permission to push the target repositories.

## Notes

- The action currently moves the `HexalithApp` and
  `references/Hexalith.Builds` submodules to the latest `main` branch before
  publishing.
- The action uses .NET's `/t:PublishContainer` target with Release
  configuration, Linux OS, and x64 architecture.
- Any registry supported by Docker login can be used, including GitHub
  Container Registry, Azure Container Registry, Docker Hub, and private
  registries.
