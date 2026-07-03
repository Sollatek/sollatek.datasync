# Deploy DataSync As An Azure Container Apps Daily Blob Job

This guide deploys DataSync as an Azure Container Apps Job that starts every day at 01:00 UTC, exports completed daily windows to Azure Blob Storage, then stops. The same Blob container stores exported files and DataSync state, so each execution continues from the last completed window.

Use this pattern when the host should be offline between runs. Use a normal Container App or VM service instead when DataSync should stay alive and schedule its own loop.

## Target Result

- One Azure Container Apps Job.
- No public inbound traffic to the DataSync workload.
- One internal Container Apps Environment attached to a VNet subnet.
- One Azure Blob container for exports and `_state`.
- Storage account locked to selected networks with the Container Apps subnet allowed.
- Daily schedule at `01:00` UTC.
- A system-assigned managed identity with `AcrPull` and `Storage Blob Data Contributor`.
- File export path shape like `daily-exports/202602/Assets_20260202.parquet`.
- Assets exported as full data for each day.
- Temperature and door opening data exported as differential daily windows.
- First execution starts from `2025-01-01T00:00:00Z` when no blob state exists.

## Prerequisites

- Azure subscription access with permission to create resource groups, storage accounts, storage network rules, Azure Container Registry, Container Apps environments, Container Apps Jobs, managed identities, and role assignments.
- Azure CLI installed locally or available in Azure Cloud Shell.
- Platform API credentials for `Settings:clientKey` and `Settings:clientSecret`.
- The DataSync image must include the Azure Blob storage provider. The default Docker image includes all storage providers.
- A blob container created before deployment when `storage.skipContainerSetup=true`.

## Included Files

- Deployment script: `deploy-containerapps-job.ps1`
- Daily deployment config: `deploy.daily.azure-containerapps-job.json`
- Optional Databricks storage connector script: `connect-databricks-storage.ps1`
- Optional Databricks storage connector config: `connect.databricks-storage.json`
- Optional Databricks source registration script: `register-databricks-blob-source.ps1`
- Optional Databricks source registration config: `register.databricks-blob-source.json`
- Optional Databricks catalog setup script: `setup-databricks-blob-catalog.ps1`
- Optional Databricks catalog setup config: `setup.databricks-blob-catalog.json`
- Cloud-team low-level design: `AZURE_DEPLOYMENT_LLD.md`

The script creates missing resources and updates an existing DataSync job only when `containerApps.updateExistingJob=true`.

## Network Model

The simplified deployment uses Azure Storage service endpoints and storage firewall rules.

When `network.skipSetup=false`, the script creates or configures:

- VNet.
- Container Apps infrastructure subnet with `Microsoft.App/environments` delegation.
- `Microsoft.Storage` service endpoint on the Container Apps subnet.
- Storage firewall rule allowing the Container Apps subnet.
- Internal Container Apps Environment for new environments.

For storage, the script creates new accounts with:

- HTTPS only.
- Minimum TLS 1.2.
- Blob public access disabled.
- Firewall default action denied.
- No network bypass.
- Public network access enabled for selected-network rules.

This does not make the blob container public. Requests still need both an allowed network path and valid authorization.

The Container Apps deployment script does not make Databricks network changes. If an existing Databricks workspace must read the same storage account, use `connect-databricks-storage.ps1` after the storage account and Databricks VNet/subnets exist.

## Daily Config Highlights

The included config schedules the job with both Container Apps cron and DataSync schedule settings:

```json
"containerApps": {
  "jobName": "sollatek-datasync-daily",
  "cronExpression": "0 1 * * *",
  "replicaTimeout": 82800,
  "runNow": false
},
"environmentVariables": {
  "SOL_Sync__runOnStartup": "historicalOnly",
  "SOL_Sync__stopWhenFinished": "true",
  "SOL_Sync__schedule__mode": "daily",
  "SOL_Sync__schedule__time": "01:00:00",
  "SOL_Sync__startFrom": "2025-01-01T00:00:00Z",
  "SOL_FileExport__folderFormat": "yyyyMM",
  "SOL_FileExport__fileNameFormat": "{entity}_{date:yyyyMMdd}.{format}",
  "SOL_FileExport__format": "parquet",
  "SOL_FileExport__replaceExisting": "true"
}
```

The included network config is:

```json
"network": {
  "skipSetup": false,
  "vnetName": "vnet-sollatek-datasync-prod",
  "addressPrefix": "10.70.0.0/16",
  "containerAppsSubnetName": "snet-sollatek-datasync-containerapps",
  "containerAppsSubnetPrefix": "10.70.0.0/23",
  "containerAppsInternalOnly": true
}
```

Set `network.skipSetup=true` only when the VNet, subnet, service endpoint, storage firewall rule, and Container Apps Environment network settings are already handled outside this script.

## Portal Setup

### 1. Create The Resource Group

1. Open the Azure portal.
2. Search for **Resource groups**.
3. Select **Create**.
4. Choose the subscription and region.
5. Name it, for example `rg-sollatek-datasync-prod`.
6. Select **Review + create**, then **Create**.

### 2. Create The Azure Container Registry

1. Search for **Container registries**.
2. Select **Create**.
3. Use the DataSync resource group and the same region as the Container Apps Job.
4. Enter a globally unique registry name, for example `acrsollatekdatasync001`.
5. Choose **Basic** unless your cloud team requires another SKU.
6. Keep the admin user disabled.
7. Select **Review + create**, then **Create**.
8. After deployment, open the registry and copy **Login server**, for example `acrsollatekdatasync001.azurecr.io`.

Registry names must be lowercase alphanumeric and unique across Azure.

### 3. Build And Push The DataSync Image

Run these commands from the DataSync repository root, the folder that contains `Sollatek.DataSync.sln` and `Sollatek.DataSync/Dockerfile`:

```powershell
az login
az account set --subscription "<subscription-id-or-name>"

$resourceGroup = "rg-sollatek-datasync-prod"
$registryName = "acrsollatekdatasync001"
$imageName = "sollatek-datasync"
$imageTag = "2026-06-23.1"

az acr build `
  --resource-group $resourceGroup `
  --registry $registryName `
  --image "${imageName}:${imageTag}" `
  --file Sollatek.DataSync/Dockerfile `
  --build-arg DATASYNC_PROVIDER=all `
  --build-arg DATASYNC_MONITORING_PROVIDER=none `
  .

$image = "$registryName.azurecr.io/${imageName}:${imageTag}"
$image
```

Use a unique immutable tag for each release. A date/build number or Git commit is better than `latest` because it makes job executions auditable.

### 4. Create The Storage Account

1. Search for **Storage accounts**.
2. Select **Create**.
3. Use the DataSync resource group and region.
4. Enter a globally unique storage account name, for example `stsollatekdsync001`.
5. Use **Standard** performance and **LRS** redundancy unless your cloud team requires a different redundancy option.
6. Keep **Allow Blob anonymous access** disabled.
7. In **Networking**, choose **Enabled from selected virtual networks and IP addresses**.
8. Keep default network access denied.
9. Select **Review + create**, then **Create**.

If the deployment script creates the storage account, it applies the same core settings automatically.

### 5. Create The Blob Container

The included config sets `storage.skipContainerSetup=true`, so create the container before deployment.

1. Open the storage account.
2. Go to **Data storage** > **Containers**.
3. Select **+ Container**.
4. Name it, for example `sollatek-datasync`.
5. Keep anonymous access disabled.
6. Select **Create**.

If the storage firewall blocks the portal or Cloud Shell session, create the container from an allowed network path or temporarily use your approved cloud-team process.

### 6. Prepare The VNet And Subnet

If `network.skipSetup=false`, the script can create the VNet and Container Apps subnet. If you create them manually:

1. Search for **Virtual networks**.
2. Select **Create**.
3. Use the DataSync resource group and region.
4. Use an address space such as `10.70.0.0/16`.
5. Create a dedicated subnet such as `snet-sollatek-datasync-containerapps` with prefix `10.70.0.0/23`.
6. Delegate the subnet to `Microsoft.App/environments`.
7. Enable the `Microsoft.Storage` service endpoint on the subnet.
8. On the storage account, add this subnet under **Networking** > **Virtual networks**.

### 7. Create A Container Apps Environment

If `network.skipSetup=false`, the script can create the Container Apps Environment. If you create it manually:

1. Search for **Container Apps Environments**.
2. Select **Create**.
3. Use the DataSync resource group and region.
4. Configure VNet integration with the dedicated Container Apps infrastructure subnet.
5. Use an internal environment. The DataSync job does not need public inbound traffic.
6. Use no persistent logs unless Log Analytics is approved and configured.

### 8. Prepare Secrets

The sample config reads API credentials from environment variables:

```powershell
$env:DATASYNC_PLATFORM_CLIENT_KEY = "<client-key>"
$env:DATASYNC_PLATFORM_CLIENT_SECRET = "<client-secret>"
```

Do not put client secrets in source-controlled JSON files.

For production, prefer Key Vault references in the `secrets` section after the cloud team grants the Container Apps Job identity access to the secret.

## Deploy With The Script

Review the plan first:

From the `deployment/azure` folder:

```powershell
.\deploy-containerapps-job.ps1 `
  -ConfigPath .\deploy.daily.azure-containerapps-job.json `
  -PlanOnly
```

Deploy:

```powershell
.\deploy-containerapps-job.ps1 `
  -ConfigPath .\deploy.daily.azure-containerapps-job.json
```

The script does the following:

- Selects the subscription.
- Ensures the Container Apps Azure CLI extension is installed.
- Creates the resource group if missing.
- Creates the VNet and Container Apps subnet when `network.skipSetup=false`.
- Creates the ACR if missing.
- Builds the DataSync image unless `-SkipBuild` is used.
- Reuses an existing ACR image tag when the tag already exists and `appsettings` are not baked into the image.
- Rebuilds the image when `appsettings` are present in the deployment config, because that configuration is copied into `Sollatek.DataSync/appsettings.json` during the image build.
- Creates the storage account if missing.
- Adds the storage service endpoint and storage firewall subnet rule when `network.skipSetup=false`.
- Creates the Container Apps Environment if missing.
- Creates or optionally updates the Container Apps Job.
- Assigns the job managed identity.
- Assigns `AcrPull` and `Storage Blob Data Contributor`.
- Sets registry, secrets, environment variables, image, schedule, CPU, memory, and retry settings.

Existing jobs are not changed unless `containerApps.updateExistingJob=true`.

## Update Only The Job Image

Use image-only mode when the Azure resources and Container Apps Job already exist and you only need the job to use a rebuilt DataSync app image:

```powershell
.\deploy-containerapps-job.ps1 `
  -ConfigPath .\deploy.daily.azure-containerapps-job.json `
  -ImageOnly
```

Image-only mode:

- Requires the configured resource group, Azure Container Registry, and Container Apps Job to already exist.
- Rebuilds the configured ACR image tag even when that tag already exists.
- Updates only the Container Apps Job image reference.
- Does not create or update storage, networking, secrets, environment variables, RBAC, schedule, CPU, memory, retry settings, or start a job execution.

For a deterministic rollout, update `image.tag` in the config to a new release tag before running image-only mode. Reusing the same tag is allowed, but a unique tag makes it clear which image each job execution used.

## Optional Databricks Storage Connection

Use this only when an existing Databricks workspace must read the same Blob container.

This step handles Azure network and RBAC access only. Use the next section when the Blob container also needs to be registered in Databricks Unity Catalog.

Update `connect.databricks-storage.json` with:

- Storage account resource group, account name, and container name.
- Existing Databricks workspace name and resource group, when you want the script to validate the workspace.
- Existing Databricks VNet resource group, VNet name, and compute subnet names.
- Optional Databricks access principal object ID for Storage Blob RBAC.

Review the plan:

```powershell
.\connect-databricks-storage.ps1 `
  -ConfigPath .\connect.databricks-storage.json `
  -PlanOnly
```

Apply:

```powershell
.\connect-databricks-storage.ps1 `
  -ConfigPath .\connect.databricks-storage.json
```

The connector enables `Microsoft.Storage` service endpoints on the configured Databricks subnets, adds those subnets to the storage account network rules, and optionally assigns Storage Blob RBAC. It does not create Databricks workspaces, clusters, Unity Catalog objects, VNets, or subnets.

## Optional Databricks Source Registration

Use this after the storage account, blob container, Databricks workspace, Databricks access connector, storage RBAC, and storage network rules are ready.

The script uses the Databricks CLI to create or reuse:

- A Unity Catalog storage credential backed by an Azure Databricks access connector managed identity.
- A Unity Catalog external location over the configured storage container or folder.
- An optional catalog, schema, and external volume so the storage path is browsable as a Databricks source.

The storage account must be compatible with Azure Data Lake Storage Gen2 external locations. The generated URL uses `abfss://<container>@<account>.dfs.core.windows.net/<path>/`.

Update `register.databricks-blob-source.json` with:

- `storage.accountName`, `storage.containerName`, and optional `storage.path`, or a full `storage.url`.
- `databricks.profile`, matching an authenticated Databricks CLI profile.
- `unityCatalog.accessConnectorId`, the Azure resource ID of the Access Connector for Azure Databricks.
- Optional `unityCatalog.managedIdentityId` when the access connector uses a user-assigned managed identity.
- Unity Catalog names for `storageCredentialName`, `externalLocationName`, and optional `catalogName`, `schemaName`, and `volumeName`.

Review the plan:

```powershell
.\register-databricks-blob-source.ps1 `
  -ConfigPath .\register.databricks-blob-source.json `
  -PlanOnly
```

Apply:

```powershell
.\register-databricks-blob-source.ps1 `
  -ConfigPath .\register.databricks-blob-source.json
```

Existing Unity Catalog objects are reused and not modified. The script creates missing objects only.

## Optional Databricks Catalog Setup

Use `setup-databricks-blob-catalog.ps1` when you want one script to reuse an existing Azure Databricks access connector, allow that access connector through the storage account network rules, assign Blob RBAC on the container, and then register the storage path in Unity Catalog.

The Azure Storage resource-instance rule is applied at storage account level. The blob container is used for RBAC scope and the Unity Catalog external location URL.

Update `setup.databricks-blob-catalog.json` with:

- Storage account resource group, account name, container name, and optional folder path.
- Existing access connector resource group and name, or full access connector resource ID.
- Optional user-assigned managed identity resource ID and principal ID when the access connector does not use its system-assigned identity.
- Databricks CLI profile.
- Unity Catalog storage credential, external location, catalog, optional schema, and optional external volume names.

Review the plan:

```powershell
.\setup-databricks-blob-catalog.ps1 `
  -ConfigPath .\setup.databricks-blob-catalog.json `
  -PlanOnly
```

Apply:

```powershell
.\setup-databricks-blob-catalog.ps1 `
  -ConfigPath .\setup.databricks-blob-catalog.json
```

Set `storage.createContainer=true` only when the deployment machine is allowed by the storage firewall and should create the container. Otherwise create the container through the approved storage process first.

## Validate Deployment

Check the job:

```powershell
az containerapp job show `
  --resource-group rg-sollatek-datasync-prod `
  --name sollatek-datasync-daily `
  --query "{name:name,trigger:configuration.triggerType,schedule:configuration.scheduleTriggerConfig.cronExpression,identity:identity.principalId}"
```

Check storage network rules:

```powershell
az storage account show `
  --resource-group rg-sollatek-datasync-prod `
  --name stsollatekdsync001 `
  --query "{publicNetworkAccess:publicNetworkAccess,defaultAction:networkRuleSet.defaultAction,bypass:networkRuleSet.bypass,subnetRules:networkRuleSet.virtualNetworkRules[].virtualNetworkResourceId}"
```

Check the subnet:

```powershell
az network vnet subnet show `
  --resource-group rg-sollatek-datasync-prod `
  --vnet-name vnet-sollatek-datasync-prod `
  --name snet-sollatek-datasync-containerapps `
  --query "{delegations:delegations[].serviceName,serviceEndpoints:serviceEndpoints[].service}"
```

Start one run manually only after credentials, blob container, RBAC, and network rules are ready:

```powershell
az containerapp job start `
  --resource-group rg-sollatek-datasync-prod `
  --name sollatek-datasync-daily
```

## Troubleshooting

| Symptom | Likely cause | Checks |
| --- | --- | --- |
| Job cannot write blobs | Missing container, missing RBAC, or storage firewall rule missing | Check container existence, job identity role assignment, subnet service endpoint, and storage network rules. |
| Job cannot pull image | Missing `AcrPull`, wrong registry server, or image tag missing | Check job identity, registry setting, ACR repository, and image tag. |
| Script cannot create the blob container | Deployment machine is blocked by storage firewall | Keep `storage.skipContainerSetup=true` and create the container from an allowed network path. |
| Existing job was not changed | `containerApps.updateExistingJob=false` | Set it to `true` only for an intentional redeploy. |
| Job stops before the historical backlog completes | Replica timeout is too low | Included config uses `82800` seconds, which is 23 hours. Increase only if your platform limit and operations policy allow it. |

## Cost Notes

- Network-only estimate for this daily export scenario: `$0/month` for the VNet, subnet, `Microsoft.Storage` service endpoint, and storage firewall subnet rule.
- This estimate assumes the app, Container Apps Environment, and Storage account are in the same Azure region, and that the deployment does not add VNet peering, NAT Gateway, Azure Firewall, VPN Gateway, ExpressRoute, Load Balancer, public IP addresses, or any other extra networking service.
- Same-region Azure service data transfer does not add a separate data-transfer charge. Incoming data transfer to Azure is free. Internet egress from Azure has the first 100 GB/month free on current public Azure pricing, then the published regional bandwidth rates apply.
- For the daily export flow, downloads from the platform into Azure are inbound to Azure, and writes from the job to Blob Storage stay inside Azure. Normal non-network costs still apply.
- Container Apps Job execution, ACR storage/build, Blob Storage capacity/transactions, Log Analytics, Application Insights, NAT Gateway, firewall, and extra networking services can create charges.
- The included config keeps `containerApps.logsDestination=none` to avoid surprise platform log ingestion.
