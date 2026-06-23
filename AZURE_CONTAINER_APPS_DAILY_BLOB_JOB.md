# Deploy DataSync As An Azure Container Apps Daily Blob Job

This example deploys DataSync as an Azure Container Apps Job that starts every day at 02:00 UTC, exports completed daily windows to Azure Blob Storage, then stops. It uses Azure Blob Storage for both exported files and DataSync state, so the next execution continues from the last completed window.

Use this pattern when the host should be offline between runs. Use a normal Container App or VM service instead when DataSync should stay alive and schedule its own loop.

## Target Result

- One Azure Container Apps Job.
- Schedule trigger: `0 2 * * *` in UTC.
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
- A DataSync container image pushed to Azure Container Registry or another registry accessible by Azure Container Apps.
- Platform API credentials for `Settings:clientKey` and `Settings:clientSecret`.
- The DataSync image must include the Azure Blob storage provider. The default Docker image includes all storage providers.

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
      "time": "02:00:00"
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

### 1. Create The Resource Group

1. Open the Azure portal.
2. Search for **Resource groups**.
3. Select **Create**.
4. Choose the subscription and region.
5. Name it, for example `rg-datasync-prod`.
6. Select **Review + create**, then **Create**.

### 2. Create The Storage Account

1. Search for **Storage accounts**.
2. Select **Create**.
3. Use the DataSync resource group.
4. Choose a globally unique storage account name.
5. Choose the same region as the Container Apps Job.
6. For redundancy, choose the level required by the deployment. `LRS` is the simplest low-cost option; production may require `ZRS` or `GRS`.
7. Select **Review + create**, then **Create**.

### 3. Create The Blob Container

1. Open the storage account.
2. Go to **Data storage** > **Containers**.
3. Select **+ Container**.
4. Name it, for example `exports`.
5. Keep public access disabled.
6. Select **Create**.

### 4. Prepare The Container Image

Create or reuse an Azure Container Registry, then push a DataSync image.

Recommended image properties:

- Use a unique immutable tag for each release, for example `datasync:2026-06-23.1`.
- Avoid `latest` for production jobs because it makes executions harder to audit.
- Include `DATASYNC_MONITORING_PROVIDER=azuremonitor` or `azuremonitor-status` at build time only if Azure Monitor export or the HTTP status endpoint is required.

The Azure portal job creation flow needs an image reference similar to:

```text
<registry-name>.azurecr.io/datasync:2026-06-23.1
```

### 5. Create A Container Apps Environment

1. Search for **Container Apps Environments**.
2. Select **Create**.
3. Use the DataSync resource group and region.
4. Create or select a Log Analytics workspace. Keep logs enabled for job troubleshooting.
5. Select **Review + create**, then **Create**.

### 6. Create The Scheduled Container Apps Job

1. Search for **Container App Jobs**.
2. Select **Create**.
3. On **Basics**:
   - Resource group: the DataSync resource group.
   - Container Apps environment: the environment from step 5.
   - Job name: for example `datasync-daily`.
   - Trigger type: **Schedule**.
   - Cron expression: `0 2 * * *`.
4. On **Container**:
   - Image source: your registry.
   - Image: the immutable DataSync image tag.
   - CPU and memory: start with `1 vCPU` and `2 Gi`.
   - Command and arguments: leave empty unless the image requires an override.
5. On **Scale** or **Job settings**:
   - Parallelism: `1`.
   - Replica completion count: `1`.
   - Replica retry limit: `0` or `1`. Prefer DataSync retries for API failures; use platform retry only for host-level failures.
   - Replica timeout: set high enough for the largest expected catch-up run, for example `21600` seconds for 6 hours.
6. Select **Review + create**, then **Create**.

Azure evaluates the schedule cron expression in UTC. `0 2 * * *` means 02:00 UTC every day.

### 7. Enable Managed Identity On The Job

1. Open the created Container Apps Job.
2. Go to **Settings** > **Identity**.
3. On **System assigned**, switch status to **On**.
4. Select **Save**.
5. Copy the displayed object/principal ID if the portal shows it.

Use a user-assigned managed identity instead if your organization wants the identity to survive job deletion or be shared by multiple jobs.

### 8. Grant Blob Access

1. Open the storage account or the specific blob container.
2. Go to **Access control (IAM)**.
3. Select **Add** > **Add role assignment**.
4. Role: **Storage Blob Data Contributor**.
5. Members: select the Container Apps Job managed identity.
6. Scope: prefer the blob container scope if the portal allows it; otherwise use the storage account scope.
7. Select **Review + assign**.

RBAC changes can take several minutes to take effect. If the first manual run fails with authorization errors, wait and run it again after confirming the role assignment.

### 9. Add Job Secrets

Open the Container Apps Job and add secrets for sensitive values:

| Secret name | Value |
| --- | --- |
| `platform-client-key` | Platform API client key |
| `platform-client-secret` | Platform API client secret |
| `smtp-password` | Optional SMTP password |
| `appinsights-connection-string` | Optional Application Insights connection string |

Do not put API client secrets, SMTP passwords, storage account keys, or SAS tokens into source-controlled settings files.

### 10. Add Environment Variables

Set these variables on the job container. For secret values, reference the job secret instead of pasting the value directly.

| Name | Value |
| --- | --- |
| `DOTNET_ENVIRONMENT` | `Production` |
| `SOL_Settings__clientKey` | secret reference: `platform-client-key` |
| `SOL_Settings__clientSecret` | secret reference: `platform-client-secret` |
| `SOL_Storage__provider` | `azureBlobStorage` |
| `SOL_Storage__authentication` | `defaultAzureCredential` |
| `SOL_Storage__accountName` | `<storage-account-name>` |
| `SOL_Storage__containerName` | `<blob-container-name>` |
| `SOL_State__provider` | `azureBlobStorage` |
| `SOL_State__rootPath` | `_state` |
| `SOL_Sync__runOnStartup` | `historicalOnly` |
| `SOL_Sync__stopWhenFinished` | `true` |
| `SOL_Sync__schedule__mode` | `daily` |
| `SOL_Sync__schedule__time` | `02:00:00` |
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

### 11. Optional Failure Email

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

### 12. Optional Azure Monitor

If the image was built with an Azure Monitor-capable monitoring provider, add:

| Name | Value |
| --- | --- |
| `SOL_Monitoring__azureMonitor__enabled` | `true` |
| `SOL_Monitoring__azureMonitor__metricsEnabled` | `true` |
| `SOL_Monitoring__azureMonitor__logsEnabled` | `true` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | secret reference: `appinsights-connection-string` |

If the image was not built with Azure Monitor support, leave these unset. Console logs are still available through the Container Apps environment logs.

### 13. Run A Manual Validation Execution

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

### 14. Verify Normal Daily Behavior

After the first successful catch-up:

1. Leave the scheduled trigger enabled.
2. The next 02:00 UTC execution should read `_state/sync-state.json`.
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
- `runOnStartup=historicalOnly` makes a job execution process historical completed windows and exit instead of staying alive for another interval.
- `stopWhenFinished=true` is required for the process to exit cleanly after catch-up.
- Blob-backed state is available only with the `azureBlobStorage` storage provider.
- `AsyncExport:statePath` remains a local temporary workspace for downloaded files during one execution. Durable pending-request state moves to blob state when `State:provider=azureBlobStorage`.
- Do not run multiple scheduled replicas against the same state prefix. Keep parallelism and completion count at `1` unless a separate state prefix and export prefix are intentionally used.
- If an execution fails after DataSync exhausts configured retries, optional SMTP notification is sent only when failure email is configured and enabled.

## Microsoft References

- [Jobs in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
- [Managed identities in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity)
- [Azure Container Apps job identity CLI reference](https://learn.microsoft.com/en-us/cli/azure/containerapp/job/identity)
- [Assign an Azure role for blob data access](https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access)
- [Containers in Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/containers)
