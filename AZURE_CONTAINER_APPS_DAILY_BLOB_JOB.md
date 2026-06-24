# Deploy DataSync As An Azure Container Apps Daily Blob Job

This example deploys DataSync as an Azure Container Apps Job that starts every day at 01:00 UTC, exports completed daily windows to Azure Blob Storage, then stops. It uses Azure Blob Storage for both exported files and DataSync state, so the next execution continues from the last completed window.

Use this pattern when the host should be offline between runs. Use a normal Container App or VM service instead when DataSync should stay alive and schedule its own loop.

## Target Result

- One Azure Container Apps Job.
- Schedule trigger: `0 1 * * *` in UTC.
- One replica per execution.
- Azure Blob export layout:

```text
daily/202606/Assets_20260621.parquet
daily/202606/Temperature_20260621.parquet
daily/202606/Dooropening_20260621.parquet
_state/sync-state.json
```

- Assets exported as full data for each day.
- Temperature and door opening data exported as differential daily windows.
- If no blob state exists yet, the first execution starts from `2025-01-01T00:00:00Z`.
- If blob state exists, the next execution continues from that state.

## Prerequisites

- Azure subscription access with permission to create resource groups, storage accounts, Container Apps environments, Container Apps Jobs, role assignments, and optional Application Insights resources.
- Azure CLI access through Azure Cloud Shell or a local Azure CLI installation. The image build step uses Azure Container Registry Tasks, so local Docker is not required.
- Platform API credentials for `Settings:clientKey` and `Settings:clientSecret`.
- The DataSync image must include the Azure Blob storage provider. The default Docker image includes all storage providers.

## Deployment Components

Create these Azure resources for the daily job deployment:

| Resource | Purpose |
| --- | --- |
| Resource group | Holds the deployment resources. |
| Azure Storage account | Stores exported blobs and blob-backed DataSync state. |
| Blob container | Holds the `daily/` export prefix and `_state/` state prefix. |
| Azure Container Registry | Stores the DataSync container image. |
| Container Apps environment | Runtime boundary for the Container Apps Job and logs. |
| Container Apps Job | Runs the DataSync container daily at 01:00 UTC and exits. |
| Managed identity | Lets the job pull from ACR and read/write Azure Blob Storage without storing Azure keys. |
| Optional Application Insights | Receives logs/metrics when the image is built with Azure Monitor support. |

## Recommended Configuration Strategy

Keep the exact `SyncPlan` and non-secret defaults in the image, for example in `appsettings.Production.json`. Set secrets and environment-specific values in Azure Container Apps Job secrets and environment variables.

Reason: .NET configuration arrays merge by index. If the base image contains a longer default `SyncPlan`, setting only `SOL_SyncPlan__0`, `SOL_SyncPlan__1`, and `SOL_SyncPlan__2` in environment variables can leave extra default plan items active. Packaging the exact plan avoids accidental extra exports.

Use environment variables for secrets, storage account names, container names, connection strings, monitoring keys, and other deployment-specific values.

## Example `appsettings.Production.json`

Put this file in the image or equivalent deployment-specific configuration source. Do not put client secrets in the file.

```json
{
  "Settings": {
    "oauthUrl": "https://id.sollatek.io/",
    "apiUrl": "https://api.sollatek.io/"
  },
  "Sync": {
    "runOnStartup": "historicalOnly",
    "stopWhenFinished": true,
    "schedule": {
      "mode": "daily",
      "time": "01:00:00"
    },
    "startFrom": "2025-01-01T00:00:00Z",
    "initial": "differential",
    "transferMode": "asyncExport",
    "apiRequestTimeout": "00:10:00",
    "maxRetries": 3,
    "retryDelay": "00:05:00"
  },
  "Storage": {
    "provider": "azureBlobStorage",
    "authentication": "defaultAzureCredential",
    "accountName": "<storage-account-name>",
    "containerName": "<blob-container-name>"
  },
  "State": {
    "provider": "azureBlobStorage",
    "rootPath": "_state"
  },
  "SyncPlan": [
    {
      "assets": {
        "initial": "full",
        "dataMode": "full",
        "outputName": "Assets"
      }
    },
    {
      "rawDataTemperaturedata": {
        "initial": "differential",
        "dataMode": "differential",
        "outputName": "Temperature"
      }
    },
    {
      "rawDataDooropeningdata": {
        "initial": "differential",
        "dataMode": "differential",
        "outputName": "Dooropening"
      }
    }
  ],
  "FileExport": {
    "rootPath": "daily",
    "folderFormat": "yyyyMM",
    "fileNameFormat": "{entity}_{date:yyyyMMdd}.{format}",
    "format": "parquet"
  },
  "AsyncExport": {
    "maxSubmissions": 60,
    "submissionWindow": "00:05:00",
    "maxParallelRequests": 10,
    "pollInterval": "00:01:00",
    "rateLimitRetryDelay": "00:05:00"
  },
  "Notifications": {
    "failureEmail": {
      "enabled": false
    }
  }
}
```

`rawDataTemperaturedata` and `rawDataDooropeningdata` are canonical metadata keys and are safe for environment variable overrides. JSON config can also use route-style selectors where supported by DataSync, but avoid `/` in environment variable names.

## Azure Portal Steps

Container Apps Jobs run container images. This guide creates a private Azure Container Registry and uses that registry as the job image source.

## Deployment Script

For repeatable deployments, use the idempotent Azure CLI wrapper script:

```powershell
$env:DATASYNC_PLATFORM_CLIENT_KEY = "<platform-client-key>"
$env:DATASYNC_PLATFORM_CLIENT_SECRET = "<platform-client-secret>"

.\deployment\azure\deploy-containerapps-job.ps1 `
  -ConfigPath .\deployment\azure\deploy.daily.azure-containerapps-job.json
```

Use `-PlanOnly` to validate and print the resolved resource names without changing Azure:

```powershell
.\deployment\azure\deploy-containerapps-job.ps1 `
  -ConfigPath .\deployment\azure\deploy.daily.azure-containerapps-job.json `
  -PlanOnly
```

The script creates or updates:

- Resource group.
- Azure Container Registry.
- ACR-built DataSync image.
- Storage account and private blob container.
- Container Apps environment.
- Scheduled Container Apps Job.
- System managed identity.
- `AcrPull` and `Storage Blob Data Contributor` role assignments.
- Job secrets and environment variables.

Secrets are read from environment variables or Key Vault references defined in the JSON config. Keep API client secrets out of source-controlled JSON files. Container Apps Job secret names must be 20 characters or shorter.

The included `deploy.daily.azure-containerapps-job.json` uses `0 1 * * *` and `Sync:schedule:time=01:00:00`. Change both values together if the deployment should run at a different UTC time, for example `0 2 * * *` and `02:00:00`.

The included deployment config sets `containerApps.logsDestination` to `none`, so the script does not require a Log Analytics workspace or the `Microsoft.OperationalInsights` resource provider. Change it to `log-analytics` only when the subscription is prepared for Log Analytics and persisted Container Apps environment logs are required.

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
5. Choose **Basic** for the smallest production-ready registry unless the deployment needs Premium networking features such as private endpoints.
6. Keep public network access according to the client's network policy. For a first deployment, public access with RBAC is the simplest path.
7. Select **Review + create**, then **Create**.
8. After deployment, open the registry and copy **Login server**, for example `acrsollatekdatasync001.azurecr.io`.

Registry names must be lowercase alphanumeric and unique across Azure. The login server is the value used by the Container Apps Job image reference.

### 3. Build And Push The DataSync Image

Use Azure Container Registry Tasks to build the image in Azure and push it into the registry. This avoids requiring Docker on the operator's machine.

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

Monitoring build options:

- Default/no external monitoring: `DATASYNC_MONITORING_PROVIDER=none`.
- Azure Monitor metrics/logs: `DATASYNC_MONITORING_PROVIDER=azuremonitor`.
- HTTP status endpoint plus Azure Monitor: add `--build-arg DATASYNC_MONITORING_PROVIDER=azuremonitor-status --build-arg DOTNET_RUNTIME_IMAGE=aspnet`.

The image reference used later by the job is:

```text
<registry-name>.azurecr.io/datasync:<tag>
```

### 4. Create The Storage Account

1. Search for **Storage accounts**.
2. Select **Create**.
3. Use the DataSync resource group.
4. Choose a globally unique storage account name.
5. Choose the same region as the Container Apps Job.
6. For redundancy, choose the level required by the deployment. `LRS` is the simplest low-cost option; production may require `ZRS` or `GRS`.
7. Select **Review + create**, then **Create**.

### 5. Create The Blob Container

1. Open the storage account.
2. Go to **Data storage** > **Containers**.
3. Select **+ Container**.
4. Name it, for example `exports`.
5. Keep public access disabled.
6. Select **Create**.

### 6. Create A Container Apps Environment

1. Search for **Container Apps Environments**.
2. Select **Create**.
3. Use the DataSync resource group and region.
4. Choose the log destination required by the deployment. Log Analytics gives persisted troubleshooting logs but requires the subscription to support `Microsoft.OperationalInsights`; `none` avoids Log Analytics.
5. Select **Review + create**, then **Create**.

### 7. Create The Scheduled Container Apps Job

1. Search for **Container App Jobs**.
2. Select **Create**.
3. On **Basics**:
   - Resource group: the DataSync resource group.
   - Container Apps environment: the environment from step 6.
   - Job name: for example `sollatek-datasync-daily`.
   - Trigger type: **Schedule**.
   - Cron expression: `0 1 * * *`.
4. On **Container**:
   - Image source: Azure Container Registry or other private registry.
   - Registry: the ACR created in step 2.
   - Image: the immutable DataSync image tag from step 3.
   - CPU and memory: start with `1 vCPU` and `2 Gi`.
   - Command and arguments: leave empty unless the image requires an override.
5. On **Scale** or **Job settings**:
   - Parallelism: `1`.
   - Replica completion count: `1`.
   - Replica retry limit: `0` or `1`. Prefer DataSync retries for API failures; use platform retry only for host-level failures.
   - Replica timeout: `82800` seconds. This allows a 23-hour run and leaves one hour before the next daily trigger.
6. Select **Review + create**, then **Create**.

Azure evaluates the schedule cron expression in UTC. `0 1 * * *` means 01:00 UTC every day.

If the portal flow does not let you set managed identity and private ACR image pull in one pass, create the job from Azure Cloud Shell instead:

```powershell
az extension add --name containerapp --upgrade

$resourceGroup = "rg-sollatek-datasync-prod"
$location = "westeurope"
$containerAppsEnvironment = "cae-sollatek-datasync-prod"
$registryName = "acrsollatekdatasync001"
$imageName = "sollatek-datasync"
$imageTag = "2026-06-23.1"
$jobName = "sollatek-datasync-daily"
$image = "$registryName.azurecr.io/${imageName}:${imageTag}"

az containerapp job create `
  --name $jobName `
  --resource-group $resourceGroup `
  --environment $containerAppsEnvironment `
  --trigger-type Schedule `
  --cron-expression "0 1 * * *" `
  --replica-timeout 82800 `
  --replica-retry-limit 0 `
  --parallelism 1 `
  --replica-completion-count 1 `
  --image $image `
  --cpu 1 `
  --memory 2Gi `
  --mi-system-assigned `
  --registry-server "$registryName.azurecr.io" `
  --registry-identity system
```

The CLI can assign the system identity and configure ACR image pull during job creation. Step 9 verifies or adds the `AcrPull` role assignment explicitly.

### 8. Enable Managed Identity On The Job

1. Open the created Container Apps Job.
2. Go to **Settings** > **Identity**.
3. On **System assigned**, switch status to **On**.
4. Select **Save**.
5. Copy the displayed object/principal ID if the portal shows it.

Use a user-assigned managed identity instead if your organization wants the identity to survive job deletion or be shared by multiple jobs.

### 9. Grant ACR Pull Access

The job identity needs `AcrPull` on the Azure Container Registry. The CLI job creation command may add this automatically when permissions allow it, but verify the role assignment.

Portal:

1. Open the Azure Container Registry.
2. Go to **Access control (IAM)**.
3. Select **Add** > **Add role assignment**.
4. Role: **AcrPull**.
5. Members: select the Container Apps Job managed identity.
6. Select **Review + assign**.

Azure CLI:

```powershell
$resourceGroup = "rg-sollatek-datasync-prod"
$registryName = "acrsollatekdatasync001"
$jobName = "sollatek-datasync-daily"

$acrId = az acr show `
  --resource-group $resourceGroup `
  --name $registryName `
  --query id `
  --output tsv

$principalId = az containerapp job show `
  --resource-group $resourceGroup `
  --name $jobName `
  --query identity.principalId `
  --output tsv

az role assignment create `
  --assignee-object-id $principalId `
  --assignee-principal-type ServicePrincipal `
  --role AcrPull `
  --scope $acrId
```

### 10. Grant Blob Access

1. Open the storage account or the specific blob container.
2. Go to **Access control (IAM)**.
3. Select **Add** > **Add role assignment**.
4. Role: **Storage Blob Data Contributor**.
5. Members: select the Container Apps Job managed identity.
6. Scope: prefer the blob container scope if the portal allows it; otherwise use the storage account scope.
7. Select **Review + assign**.

RBAC changes can take several minutes to take effect. If the first manual run fails with authorization errors, wait and run it again after confirming the role assignment.

### 11. Add Job Secrets

Open the Container Apps Job and add secrets for sensitive values:

| Secret name | Value |
| --- | --- |
| `platform-key` | Platform API client key |
| `platform-secret` | Platform API client secret |
| `smtp-password` | Optional SMTP password |
| `appinsights-conn` | Optional Application Insights connection string |

Do not put API client secrets, SMTP passwords, storage account keys, or SAS tokens into source-controlled settings files.

### 12. Add Environment Variables

Set these variables on the job container. For secret values, reference the job secret instead of pasting the value directly.

| Name | Value |
| --- | --- |
| `DOTNET_ENVIRONMENT` | `Production` |
| `SOL_Settings__clientKey` | secret reference: `platform-key` |
| `SOL_Settings__clientSecret` | secret reference: `platform-secret` |
| `SOL_Storage__provider` | `azureBlobStorage` |
| `SOL_Storage__authentication` | `defaultAzureCredential` |
| `SOL_Storage__accountName` | `<storage-account-name>` |
| `SOL_Storage__containerName` | `<blob-container-name>` |
| `SOL_State__provider` | `azureBlobStorage` |
| `SOL_State__rootPath` | `_state` |
| `SOL_Sync__runOnStartup` | `historicalOnly` |
| `SOL_Sync__stopWhenFinished` | `true` |
| `SOL_Sync__schedule__mode` | `daily` |
| `SOL_Sync__schedule__time` | `01:00:00` |
| `SOL_Sync__startFrom` | `2025-01-01T00:00:00Z` |
| `SOL_Sync__transferMode` | `asyncExport` |
| `SOL_FileExport__rootPath` | `daily` |
| `SOL_FileExport__folderFormat` | `yyyyMM` |
| `SOL_FileExport__fileNameFormat` | `{entity}_{date:yyyyMMdd}.{format}` |
| `SOL_FileExport__format` | `parquet` |
| `SOL_AsyncExport__maxSubmissions` | `60` |
| `SOL_AsyncExport__submissionWindow` | `00:05:00` |
| `SOL_AsyncExport__maxParallelRequests` | `10` |

If you cannot package `SyncPlan` into the image, ensure the image does not contain unwanted default `SyncPlan` entries before using environment-variable array overrides.

### 13. Optional Failure Email

If SMTP failure notifications are required, add these environment variables and secrets:

| Name | Value |
| --- | --- |
| `SOL_Notifications__failureEmail__enabled` | `true` |
| `SOL_Notifications__failureEmail__smtpHost` | `<smtp-host>` |
| `SOL_Notifications__failureEmail__smtpPort` | `587` |
| `SOL_Notifications__failureEmail__enableSsl` | `true` |
| `SOL_Notifications__failureEmail__username` | `<smtp-user>` |
| `SOL_Notifications__failureEmail__password` | secret reference: `smtp-password` |
| `SOL_Notifications__failureEmail__from` | `<sender-address>` |
| `SOL_Notifications__failureEmail__to` | `<recipient-addresses>` |
| `SOL_Notifications__failureEmail__subjectPrefix` | `[DataSync]` |

Leave `failureEmail:enabled` false or omit the section when no email should be sent.

### 14. Optional Azure Monitor

If the image was built with an Azure Monitor-capable monitoring provider, add:

| Name | Value |
| --- | --- |
| `SOL_Monitoring__azureMonitor__enabled` | `true` |
| `SOL_Monitoring__azureMonitor__metricsEnabled` | `true` |
| `SOL_Monitoring__azureMonitor__logsEnabled` | `true` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | secret reference: `appinsights-conn` |

If the image was not built with Azure Monitor support, leave these unset. Container console logs are persisted only when the Container Apps environment log destination is configured for a logging backend such as Log Analytics.

### 15. Run A Manual Validation Execution

1. Open the Container Apps Job.
2. Select **Run now**.
3. Go to **Monitoring** > **Execution history**.
4. Open the newest execution and inspect status and logs.
5. Confirm that blobs are created in the container:

```text
daily/<yyyyMM>/Assets_<yyyyMMdd>.parquet
daily/<yyyyMM>/Temperature_<yyyyMMdd>.parquet
daily/<yyyyMM>/Dooropening_<yyyyMMdd>.parquet
_state/sync-state.json
```

If the first run has a large historical backlog, it may create many daily files before reaching the latest completed day.

### 16. Verify Normal Daily Behavior

After the first successful catch-up:

1. Leave the scheduled trigger enabled.
2. The next 01:00 UTC execution should read `_state/sync-state.json`.
3. It should export only missing completed daily windows.
4. It should stop when finished.
5. Execution history should show one new execution per day.

## Connection String Alternative

Managed identity is preferred because no storage key is stored in job settings. If managed identity is not available, set:

```text
SOL_Storage__authentication=connectionString
SOL_Storage__connectionString=<secret-reference>
SOL_Storage__containerName=<blob-container-name>
```

Store the connection string as a Container Apps Job secret. Keep `State:provider=azureBlobStorage` only when the same blob configuration has read, write, create, list, and delete permissions for the state prefix.

## Operational Notes

- The Container Apps Job schedule controls when the process starts. `Sync:schedule` tells DataSync how to plan completed windows and status/backlog metadata for the run.
- Keep `replicaTimeout` lower than the schedule interval. The included daily deployment uses `82800` seconds, so a stuck execution is stopped before the next 24-hour trigger.
- `runOnStartup=historicalOnly` makes a job execution process historical completed windows and exit instead of staying alive for another interval.
- `stopWhenFinished=true` is required for the process to exit cleanly after catch-up.
- Blob-backed state is available only with the `azureBlobStorage` storage provider.
- `AsyncExport:statePath` remains a local temporary workspace for downloaded files during one execution. Durable pending-request state moves to blob state when `State:provider=azureBlobStorage`.
- Do not run multiple scheduled replicas against the same state prefix. Keep parallelism and completion count at `1` unless a separate state prefix and export prefix are intentionally used.
- If an execution fails after DataSync exhausts configured retries, optional SMTP notification is sent only when failure email is configured and enabled.

## Microsoft References

- [Jobs in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
- [Managed identities in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity)
- [Azure Container Apps image pull with managed identity](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull)
- [Azure Container Apps job identity CLI reference](https://learn.microsoft.com/en-us/cli/azure/containerapp/job/identity)
- [Assign an Azure role for blob data access](https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access)
- [Containers in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/containers)
- [Create an Azure Container Registry in the portal](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-get-started-portal)
- [Build a container image with Azure Container Registry Tasks](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-quickstart-task-cli)
