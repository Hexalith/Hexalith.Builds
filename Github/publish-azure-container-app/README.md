# Publish Azure Container App Action

> **Legacy HexalithApp-era action.** Not part of the domain module pipeline
> generation (`domain-ci.yml` / `domain-release.yml`). Kept for existing consumers.

Updates existing Azure Container Apps to use a new image version. The action
logs in to Azure with `azure/login@master` and updates the Web and API
container apps with Azure CLI.

## Inputs

| Input | Description | Required |
|-------|-------------|----------|
| `version` | Image version tag to deploy. | Yes |
| `client-id` | Azure application client ID. | Yes |
| `tenant-id` | Azure tenant ID. | Yes |
| `subscription-id` | Azure subscription ID. | Yes |
| `resource-group` | Resource group containing the container apps. | Yes |
| `app-id` | Short application name used as the container app and image prefix. | Yes |
| `registry` | Container registry host containing the images. | Yes |

## What This Action Updates

The action updates two existing container apps:

| Container app | Image |
|---------------|-------|
| `{app-id}web` | `{registry}/{app-id}web:{version}` |
| `{app-id}api` | `{registry}/{app-id}api:{version}` |

For example, with `app-id: myapp`, `registry: myregistry.azurecr.io`, and
`version: 1.2.3`, the action deploys:

- `myregistry.azurecr.io/myappweb:1.2.3` to `myappweb`
- `myregistry.azurecr.io/myappapi:1.2.3` to `myappapi`

## Prerequisites

### Azure

- An Azure application registration or managed identity configured for
  `azure/login`.
- Federated credentials for GitHub OIDC if using token-based login.
- Permissions to update the target Azure Container Apps. The Contributor role
  on the resource group is sufficient.
- Existing container apps named `{app-id}web` and `{app-id}api`.
- Published container images in the registry with the requested `version` tag.

### GitHub

The workflow must allow OIDC token issuance:

```yaml
permissions:
  id-token: write
  contents: read
```

Configure these secrets or environment values in the consuming repository:

- Azure client ID
- Azure tenant ID
- Azure subscription ID
- Azure resource group name
- Application short name
- Registry host

## Usage

```yaml
- name: Deploy to Azure Container Apps
  uses: Hexalith/Hexalith.Builds/Github/publish-azure-container-app@main
  with:
    version: ${{ needs.build.outputs.version }}
    client-id: ${{ secrets.AZURE_APPLICATIONID }}
    tenant-id: ${{ secrets.AZURE_TENANTID }}
    subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
    resource-group: ${{ vars.HEXALITH_RESOURCE_GROUP }}
    app-id: ${{ vars.HEXALITH_MODULE_SHORT_NAME }}
    registry: ${{ vars.AZURE_REGISTRY }}
```

## Troubleshooting

### Not all values are present

This usually means one or more Azure login values were not passed to the action.
Check:

- The repository or environment secrets exist.
- The job specifies the expected environment when using environment secrets.
- The secret names match the workflow exactly.
- The values are not empty.

When GitHub Actions debug logging is enabled, the action prints whether each
authentication input was provided without writing secret values.

### AADSTS700016 application not found

Verify the Azure identity configuration:

- The client ID matches the application registration.
- The federated credential subject matches the repository and environment.
- The issuer is `https://token.actions.githubusercontent.com`.
- The audience is `api://AzureADTokenExchange`.

### Container app update fails

Check:

- The container app names are exactly `{app-id}web` and `{app-id}api`.
- The container apps are in the specified resource group.
- The image tags exist in the specified registry.
- The Azure identity has permission to update Container Apps.
