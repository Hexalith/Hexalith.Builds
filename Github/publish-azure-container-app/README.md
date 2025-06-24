# Publish Azure Container App GitHub Action

## Overview

This GitHub Action publishes application containers to Azure Container Apps. It automates the deployment process by logging into Azure and updating container apps with new image versions. The action is designed to deploy both web and API components of an application.

## Inputs

| Input | Description | Required | Type |
|-------|-------------|----------|------|
| `version` | Version number for the packages | Yes | string |
| `client-id` | Client ID for the Azure administration | Yes | string |
| `tenant-id` | Tenant ID for the Azure administration | Yes | string |
| `subscription-id` | Subscription ID for the Azure administration | Yes | string |
| `resource-group` | Resource group to deploy the containers to | Yes | string |
| `app-id` | The short name of the application | Yes | string |
| `registry` | Registry to publish the containers to | Yes | string |

## Functionality

This action performs the following operations:

1. **Azure Authentication**: Logs into Azure using service principal credentials
2. **Container App Updates**: Updates both web and API container apps with new image versions
3. **Image Deployment**: Deploys container images from the specified registry with the provided version tag

The action automatically updates two container apps:

- `{app-id}web` - Web application container
- `{app-id}api` - API application container

## Usage Example

```yaml
- name: Publish to Azure Container Apps
  uses: ./.github/actions/publish-azure-container-app
  with:
    version: ${{ steps.version.outputs.version }}
    client-id: ${{ secrets.AZURE_CLIENT_ID }}
    tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
    resource-group: 'my-resource-group'
    app-id: 'myapp'
    registry: 'myregistry.azurecr.io'
```

## How It Works

1. **Authentication**: Uses the `azure/login@v2` action to authenticate with Azure using service principal credentials
2. **Container Update Function**: Defines a bash function `update_container_app()` that updates container apps using Azure CLI
3. **Dual Deployment**: Executes the update function for both web and API components
4. **Image Format**: Uses the format `{registry}/{app-id}{type}:{version}` for container images

The action uses Azure CLI commands to update existing container apps rather than creating new ones, ensuring zero-downtime deployments.

## Prerequisites

- **Azure Service Principal**: A service principal with appropriate permissions to manage Azure Container Apps
- **Existing Container Apps**: The container apps `{app-id}web` and `{app-id}api` must already exist in the specified resource group
- **Container Registry**: Access to the specified container registry with the required images
- **Azure CLI**: The action uses Azure CLI commands for container app management

### Required Azure Permissions

The service principal must have the following permissions:

- `Microsoft.ContainerApps/containerApps/write` - To update container apps
- `Microsoft.ContainerApps/containerApps/read` - To read existing container app configurations
- `Microsoft.Resources/subscriptions/resourceGroups/read` - To access the resource group
