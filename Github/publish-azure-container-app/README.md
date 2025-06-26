# Publish Azure Container App Action

This GitHub Action deploys container images to Azure Container Apps using OIDC (OpenID Connect) authentication.

## Prerequisites

### 1. Azure Configuration

You need to set up OIDC authentication between GitHub and Azure:

1. **Create an Azure App Registration**:
   ```bash
   az ad app create --display-name "GitHub-Actions-<YourRepoName>"
   ```

2. **Add Federated Credentials**:
   ```bash
   az ad app federated-credential create \
     --id <APPLICATION-OBJECT-ID> \
     --parameters @federated-credential.json
   ```

   Where `federated-credential.json` contains:
   ```json
   {
     "name": "GitHub-<YourRepoName>-<Environment>",
     "issuer": "https://token.actions.githubusercontent.com",
     "subject": "repo:<YourOrg>/<YourRepo>:environment:<Environment>",
     "audiences": ["api://AzureADTokenExchange"]
   }
   ```

3. **Grant Required Permissions**:
   - Assign the service principal the "Contributor" role on your resource group or container apps

### 2. GitHub Configuration

Configure the following secrets in your repository or environment:

- `AZURE_APPLICATIONID`: The Application (client) ID from your Azure App Registration
- `AZURE_TENANTID`: Your Azure Active Directory tenant ID
- `AZURE_SUBSCRIPTION_ID`: Your Azure subscription ID

Configure the following variables:

- `HEXALITH_MODULE_SHORT_NAME`: Short name for your application (used as prefix for container apps)
- `HEXALITH_RESOURCE_GROUP`: Azure resource group containing your container apps
- `AZURE_REGISTRY`: Your Azure Container Registry URL

### 3. Workflow Permissions

Your workflow must have the `id-token: write` permission for OIDC authentication:

```yaml
permissions:
  id-token: write
```

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

### Error: "Not all values are present"

This error occurs when the authentication values are not properly passed to the action. Check:

1. **Secrets are configured**: Go to Settings → Secrets and variables → Actions in your repository
2. **Environment is specified**: If using environment-specific secrets, ensure your job specifies the environment:
   ```yaml
   environment: Staging
   ```
3. **Secrets have correct values**: Verify the values match your Azure configuration
4. **No typos in secret names**: The names are case-sensitive

### Error: "AADSTS700016: Application with identifier..."

This error indicates the OIDC configuration in Azure is incorrect. Verify:

1. The federated credential subject matches your repository and environment
2. The issuer is exactly `https://token.actions.githubusercontent.com`
3. The audience is `api://AzureADTokenExchange`

### Debugging Steps

1. **Verify secrets are accessible**:
   Add a debug step before the deployment:
   ```yaml
   - name: Debug Auth Values
     run: |
       echo "Client ID exists: ${{ secrets.AZURE_APPLICATIONID != '' }}"
       echo "Tenant ID exists: ${{ secrets.AZURE_TENANTID != '' }}"
       echo "Subscription ID exists: ${{ secrets.AZURE_SUBSCRIPTION_ID != '' }}"
   ```

2. **Check Azure configuration**:
   ```bash
   # List federated credentials
   az ad app federated-credential list --id <APPLICATION-OBJECT-ID>
   
   # Verify service principal permissions
   az role assignment list --assignee <APPLICATION-ID>
   ```

3. **Enable Azure CLI debugging**:
   Set the `AZURE_LOG_LEVEL` environment variable to `DEBUG` in your workflow

## What This Action Does

1. Authenticates to Azure using OIDC
2. Updates container images for two container apps:
   - `<app-id>web`: Web application container
   - `<app-id>api`: API application container

Both container apps must already exist in the specified resource group.

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
