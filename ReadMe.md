![Sollatek](Images/sollatek_logo.png)

# Sollatek.DataSync

Sollatek.DataSync is a .NET worker application that reads selected Sollatek Platform API entities and writes them to a configured target:

- SQL Server, PostgreSQL, or MySQL relational tables.
- MongoDB collections.
- Parquet files on the local/container filesystem.

The worker is configured from `appsettings.json`, .NET user secrets, command-line arguments, or environment variables prefixed with `SOL_`.

## Quick Start

Use this path when you only want to run the application.

### 1. Choose A Provider

Set `Storage:provider` to one of:

| Provider | Target | Connection string required |
| --- | --- | --- |
| `sqlserver` | SQL Server tables | Yes |
| `postgres` | PostgreSQL tables | Yes |
| `mysql` | MySQL tables | Yes |
| `mongo` | MongoDB collections | Yes |
| `filesystem` | Parquet files | No |

The default executable includes every storage provider and no optional monitoring provider. Provider-specific published builds are described in [Publish For Deployment](#publish-for-deployment).

### 2. Configure Credentials

For local development, use .NET user secrets. The project already has `UserSecretsId` set to `Sollatek_datasync`, so `dotnet user-secrets init` is not needed.

```powershell
dotnet user-secrets set "Settings:clientKey" "YourClientKey" --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
dotnet user-secrets set "Settings:clientSecret" "YourClientSecret" --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
```

For deployment, use `appsettings.json`, command-line arguments, or environment variables:

```powershell
$env:SOL_Settings__clientKey="YourClientKey"
$env:SOL_Settings__clientSecret="YourClientSecret"
```

`Settings:oauthUrl` defaults to `https://id.sollatek.io/` in `appsettings.json`.
`Settings:apiUrl` defaults to `https://api.sollatek.io/`.

### 3. Configure Storage

Database example:

```powershell
dotnet user-secrets set "Storage:provider" "sqlserver" --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
dotnet user-secrets set "Storage:connectionString" "Server=localhost;Database=sollatek_datasync;User Id=sa;Password=YourStrongPassword;Encrypt=False;TrustServerCertificate=True;" --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
dotnet user-secrets set "Storage:schemaMode" "applySafeChanges" --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
```

Filesystem export example:

```json
{
  "Storage": {
    "provider": "filesystem"
  },
  "FileExport": {
    "rootPath": ".artifacts/exports",
    "format": "parquet"
  }
}
```

`Storage:schemaMode` controls relational schema handling:

- `validate`: startup fails if required tables, columns, or flexible foreign keys are missing.
- `applySafeChanges`: DataSync creates missing tables, columns, and flexible foreign keys.

Use `applySafeChanges` for a new local relational database. Use `validate` when the schema is managed separately.

### 4. Configure What To Sync

`SyncPlan` controls exactly which API entities are synced and in which order. DataSync does not add dependencies automatically. If the plan contains only `assets`, only assets are fetched and stored.

Example matching the original default table set:

```json
{
  "SyncPlan": [
    { "customers": { "initial": "full" } },
    { "devices": { "initial": "full" } },
    { "pointsOfInterest": { "initial": "full" } },
    { "assets": { "initial": "full" } },
    { "rawdata/locationdata": { "initial": "full" } },
    { "rawdata/extrainfodata": { "initial": "full" } },
    { "rawdata/temperaturedata": { "initial": "full" } },
    { "rawdata/dooropeningdata": { "initial": "full" } }
  ]
}
```

Shorter example:

```json
{
  "SyncPlan": [
    { "assets": { "initial": "full" } },
    "rawdata/batteryperiods",
    {
      "rawdata/dooropeningdata": {
        "dataMode": "differential",
        "partitionDate": "exportRunDay"
      }
    }
  ]
}
```

Supported entity selectors:

- Exact metadata keys, for example `rawDataBatteryperiods`.
- Normalized raw-data paths, for example `rawdata/batteryperiods`.
- Operation paths, for example `/api/RawData/batteryperiods`.
- Legacy raw-data aliases when they resolve to one entity, for example `locationData`.

If a selector is unknown, empty, duplicated, or matches multiple entities, startup fails with a clear error.

### 5. Configure Schedule And First Run

```json
{
  "Sync": {
    "runOnStartup": true,
    "stopWhenFinished": false,
    "runInterval": "06:00:00",
    "maxPageSize": 500,
    "startFrom": null
  }
}
```

Important behavior:

- `Sync:runOnStartup` starts the first cycle immediately. Default: `true`.
- `Sync:runInterval` controls the delay between completed cycles. Default: `06:00:00`.
- `Sync:stopWhenFinished` makes the process run one cycle and then stop gracefully. Use this for timer-triggered container jobs.
- `Sync:maxPageSize` controls paged API request size and the maximum rows held for one store write/export part. Default: `500`.
- `Sync:startFrom` is only the bootstrap date for a new/empty target when the selected entity is not configured with `initial: "full"`. When omitted, it defaults to January 1 of the current year.

All stores use paged API requests for every date range. Filesystem export serializes those pages locally to Parquet part files.

For SQL Server, PostgreSQL, MySQL, and MongoDB, DataSync prefers the latest stored entity watermark for the next cycle and sends an open-ended lower-bound filter. If the target cannot provide a watermark, it falls back to DataSync state. Filesystem export uses bounded date ranges from DataSync state because Parquet files are not a cheap queryable target.

`initial: "full"` defines first-load behavior. If the selected target has no stored data for that entity, DataSync removes the watermark filter and performs a full load. Once data exists, the same entity returns to differential sync.

Default compatibility with the previous hardcoded DataSync:

| Area | Previous behavior | Current default behavior |
| --- | --- | --- |
| Entity set | Customers, devices, points of interest, assets, location data, extra info data, temperature data, door opening data. | Same entities, in the same order, expressed through `SyncPlan`. |
| Empty target | No API filter for the hardcoded processors, so the first run loaded all available rows for those entities. | Default `SyncPlan` uses `initial: "full"` for those entities, so the first run removes the watermark filter. |
| Periodic target | Each run started from the latest timestamp already saved in the target table and used a lower-bound API filter. | Database providers prefer the latest stored watermark from the target and use the same lower-bound API filter; DataSync state is only a fallback. |
| Schedule | First run immediately, then every 6 hours. | `Sync:runOnStartup=true` and `Sync:runInterval=06:00:00` by default. |

### 6. Run

```powershell
dotnet run --project .\Sollatek.DataSync\Sollatek.DataSync.csproj
```

For a deployed executable:

```powershell
$env:SOL_Storage__provider="sqlserver"
$env:SOL_Storage__connectionString="Server=localhost;Database=sollatek_datasync;User Id=sa;Password=YourStrongPassword;Encrypt=False;TrustServerCertificate=True;"
$env:SOL_Settings__clientKey="YourClientKey"
$env:SOL_Settings__clientSecret="YourClientSecret"
.\Sollatek.DataSync
```

## Configuration Reference

### Storage

`Storage:connectionString` is the preferred connection-string key. `ConnectionStrings:DataSync` and the old `Settings:dbConnection` key are still accepted as fallbacks.

Connection string examples:

```json
{
  "Storage": {
    "provider": "sqlserver",
    "connectionString": "Server=localhost;Database=sollatek_datasync;User Id=sa;Password=YourStrongPassword;Encrypt=False;TrustServerCertificate=True;",
    "schemaMode": "validate"
  }
}
```

```json
{
  "Storage": {
    "provider": "postgres",
    "connectionString": "Host=localhost;Port=5432;Database=sollatek_datasync;Username=postgres;Password=YourPassword;SSL Mode=Prefer;Trust Server Certificate=true;",
    "schemaMode": "validate"
  }
}
```

```json
{
  "Storage": {
    "provider": "mysql",
    "connectionString": "Server=localhost;Port=3306;Database=sollatek_datasync;User Id=root;Password=YourPassword;SslMode=Preferred;",
    "schemaMode": "validate"
  }
}
```

```json
{
  "Storage": {
    "provider": "mongo",
    "connectionString": "mongodb://localhost:27017/sollatek_datasync"
  }
}
```

### SyncPlan Policies

Per-entity policy values:

- `initial`: `differential` or `full`. Default: `differential`. `full` removes the watermark filter only when the selected target has no stored data for that entity.
- `dataMode`: `differential` or `full`. Filesystem only. Default: `differential`. `full` removes the watermark filter every time that entity is exported.
- `partitionDate`: `watermarkDay` or `exportRunDay`. Filesystem only. Default: `watermarkDay`.

Use `partitionDate: "exportRunDay"` for telemetry-style late arrivals. For example, data exported on 15/06 is stored under the 15/06 folder even when the event timestamp is from 12/06.

For environment variables and containers, `SyncPlan:entities` can replace the JSON array with a comma, semicolon, or newline separated entity list:

```powershell
$env:SOL_SyncPlan__entities="customers;devices;pointsOfInterest;assets;rawdata/locationdata;rawdata/extrainfodata;rawdata/temperaturedata;rawdata/dooropeningdata"
```

`SyncPlan:entities` can only carry entity names. It cannot carry `initial`, `dataMode`, or `partitionDate`.

### State Storage

Provider state locations:

- SQL Server, PostgreSQL, MySQL: `__sollatek_datasync_state`.
- MongoDB: `__sollatek_datasync_state`.
- Filesystem: `<FileExport:rootPath>/_state/sync-state.json`.

This state store is DataSync-owned operational metadata. It must be writable even when relational `Storage:schemaMode` is `validate`.

Before using a stored state value, DataSync checks whether the selected target has data for that entity:

- Relational providers check table existence and then query for one row.
- MongoDB queries for one document in the target collection.
- Filesystem export checks for existing Parquet parts under the entity export folder.

If the target is empty, DataSync ignores old state for that entity. Entities configured with `initial: "full"` run without a watermark filter; other entities start from `Sync:startFrom`.

For relational and MongoDB targets with existing data, DataSync reads the latest stored watermark from the target and uses it as the next cycle start. Those providers use a lower-bound API filter for periodic paged requests. If no stored watermark is available, DataSync uses the state-store value. Filesystem targets use the state-store value once data exists.

### Retry

```json
{
  "Retry": {
    "maxTries": 3,
    "period": "00:05:00",
    "delayFunction": "linear"
  }
}
```

`Retry:delayFunction` values:

- `fixed`: next retry time is `now + period`.
- `linear`: next retry time is `now + tryNumber * period`.

## Monitoring

DataSync is a worker process. Current status is observable through console logs, an optional local HTTP status endpoint, and optional OpenTelemetry export.

Console logging and the in-process monitor are always available. External monitoring sinks are build-time optional so deployments only carry what they use:

| Monitoring build value | Included monitoring packages |
| --- | --- |
| `none` | No OTLP, Azure Monitor, or status HTTP endpoint packages |
| `otlp` | OTLP exporter |
| `azuremonitor` | Azure Monitor/Application Insights exporter |
| `status` | HTTP status endpoint |
| `otlp-status` | OTLP exporter and HTTP status endpoint |
| `azuremonitor-status` | Azure Monitor/Application Insights exporter and HTTP status endpoint |
| `otlp-azuremonitor` | OTLP and Azure Monitor/Application Insights exporters |
| `otlp-azuremonitor-status` | OTLP, Azure Monitor/Application Insights, and HTTP status endpoint |
| `all` | All monitoring providers |

If a runtime `Monitoring` section enables a provider that was not included in the published build, startup fails with a clear message.

The built-in monitor records:

- Run state: idle, running, processing entity, waiting to retry, failed, succeeded.
- Current entity.
- Retry attempt and next retry time.
- Last error summary.
- Records, pages, and files processed.

### Console Logs

Console logging is always registered. Use `Logging:LogLevel` to make the console more or less verbose:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "System.Net.Http.HttpClient": "Warning"
    }
  }
}
```

Less output:

```powershell
$env:SOL_Logging__LogLevel__Default="Warning"
```

More diagnostic output:

```powershell
$env:SOL_Logging__LogLevel__Default="Debug"
```

Disable sync monitor progress/status log events while keeping normal application logs:

```powershell
$env:SOL_Monitoring__structuredLogsEnabled="false"
```

### Status Endpoint

The status endpoint package is not included in the default `none` monitoring build. Publish with `DataSyncMonitoringProvider=status`, `otlp-status`, `azuremonitor-status`, `otlp-azuremonitor-status`, or `all` before enabling it at runtime.

The status endpoint is disabled by default. Enable it when another process should poll current worker status:

```powershell
$env:SOL_Monitoring__statusEndpoint__enabled="true"
$env:SOL_Monitoring__statusEndpoint__url="http://127.0.0.1:5055"
```

Default paths:

- `GET /status`: current `SyncRunStatus` as JSON.
- `GET /health`: `{ "status": "ok" }`.

In containers, bind only to an internal/private interface unless the endpoint is intentionally exposed:

```powershell
$env:SOL_Monitoring__statusEndpoint__url="http://0.0.0.0:5055"
```

### Metrics And OTLP

Metrics are enabled in-process by default through `System.Diagnostics.Metrics`, but they are exported only when OTLP or Azure Monitor export is enabled and the matching provider package is included in the published build. The DataSync meter name defaults to `sollatek-datasync` through `Monitoring:serviceName`.

Common metrics after OTLP-to-Prometheus conversion:

- `datasync_worker_state`
- `datasync_retry_attempt`
- `datasync_records_processed`
- `datasync_pages_processed`
- `datasync_files_processed`
- `datasync_runs_started_total`
- `datasync_runs_succeeded_total`
- `datasync_runs_failed_total`
- `datasync_entities_started_total`
- `datasync_retries_scheduled_total`

Enable OTLP metrics:

```powershell
dotnet publish .\Sollatek.DataSync\Sollatek.DataSync.csproj -c Release -p:DataSyncMonitoringProvider=otlp
$env:SOL_Monitoring__otlp__enabled="true"
$env:SOL_Monitoring__otlp__metricsEnabled="true"
$env:SOL_Monitoring__otlp__endpoint="http://localhost:4317"
$env:SOL_Monitoring__otlp__protocol="grpc"
```

OTLP logs are off by default. Enable them only when your collector is ready to receive logs:

```powershell
$env:SOL_Monitoring__otlp__logsEnabled="true"
```

For OTLP HTTP/protobuf, configure signal-specific endpoints:

```powershell
$env:SOL_Monitoring__otlp__protocol="httpProtobuf"
$env:SOL_Monitoring__otlp__metricsEndpoint="http://localhost:4318/v1/metrics"
$env:SOL_Monitoring__otlp__logsEndpoint="http://localhost:4318/v1/logs"
```

### Prometheus And Grafana

The repository includes a local observability stack:

```powershell
docker compose -f docker-compose.observability.yml up -d
```

Local endpoints:

- OpenTelemetry Collector health: `http://localhost:13133`
- Collector Prometheus exporter: `http://localhost:8889/metrics`
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000`

The stack provisions a Prometheus datasource and a `Sollatek DataSync` Grafana dashboard. DataSync sends OTLP to the collector; Prometheus scrapes the collector; Grafana reads from Prometheus.

For a DataSync container outside the observability compose network, use Docker Desktop's host endpoint:

```powershell
$env:SOL_Monitoring__otlp__endpoint="http://host.docker.internal:4317"
```

For a DataSync container on the same Docker network as the collector:

```powershell
$env:SOL_Monitoring__otlp__endpoint="http://otel-collector:4317"
```

Safe local checks:

```powershell
curl.exe http://localhost:13133
curl.exe http://localhost:8889/metrics
curl.exe http://localhost:9090/-/ready
curl.exe http://localhost:3000/api/health
```

More detail is in `docs/observability.md`.

### Application Insights / Azure Monitor

DataSync can export metrics and logs directly to Azure Monitor/Application Insights when the published build includes `DataSyncMonitoringProvider=azuremonitor`, `azuremonitor-status`, `otlp-azuremonitor`, `otlp-azuremonitor-status`, or `all`.

Enable direct export explicitly. `APPLICATIONINSIGHTS_CONNECTION_STRING` can supply the connection string, but it does not enable Azure Monitor by itself:

```powershell
dotnet publish .\Sollatek.DataSync\Sollatek.DataSync.csproj -c Release -p:DataSyncMonitoringProvider=azuremonitor
$env:SOL_Monitoring__azureMonitor__enabled="true"
$env:APPLICATIONINSIGHTS_CONNECTION_STRING="InstrumentationKey=...;IngestionEndpoint=..."
```

You can also set the connection string through explicit `SOL_` settings:

```powershell
$env:SOL_Monitoring__azureMonitor__enabled="true"
$env:SOL_Monitoring__azureMonitor__connectionString="InstrumentationKey=...;IngestionEndpoint=..."
$env:SOL_Monitoring__azureMonitor__metricsEnabled="true"
$env:SOL_Monitoring__azureMonitor__logsEnabled="true"
```

Logs are disabled by default to avoid surprise ingestion volume. Set `Monitoring:azureMonitor:logsEnabled=true` when logs should be sent to Application Insights.

## Deployment

### Docker Runtime

Build the default container image from the repository root. It includes all storage providers and no optional monitoring providers:

```powershell
docker build -f .\Sollatek.DataSync\Dockerfile -t sollatek-datasync:local .
```

Include monitoring providers by setting `DATASYNC_MONITORING_PROVIDER`. The status endpoint uses ASP.NET Core hosting, so use the ASP.NET runtime image when that provider is included:

```powershell
docker build -f .\Sollatek.DataSync\Dockerfile -t sollatek-datasync:local --build-arg DATASYNC_MONITORING_PROVIDER=otlp .
docker build -f .\Sollatek.DataSync\Dockerfile -t sollatek-datasync:local --build-arg DATASYNC_MONITORING_PROVIDER=status --build-arg DOTNET_RUNTIME_IMAGE=aspnet .
```

The Dockerfile fails the build with a clear error if a status endpoint monitoring provider is selected without `DOTNET_RUNTIME_IMAGE=aspnet`.

`docker-compose.datasync.example.yml` shows filesystem export with a named volume mapped to `/app/volumes/webapi/exports`.

The Docker image sets:

```text
FileExport__rootPath=/app/volumes/webapi/exports
```

and declares `/app/volumes/webapi/exports` as a volume so exported files are not tied to the container writable layer.

### Hosting Scenarios

DataSync can run either as a continuous worker or as a platform-scheduled one-shot job.

Use a continuous worker when the host process should stay alive and schedule its own cycles:

```json
{
  "Sync": {
    "runOnStartup": true,
    "stopWhenFinished": false,
    "runInterval": "06:00:00"
  }
}
```

Use a platform-scheduled job when Azure, AWS, Google Cloud, Kubernetes, cron, or another scheduler starts the process on a timer:

```json
{
  "Sync": {
    "runOnStartup": true,
    "stopWhenFinished": true
  }
}
```

Run only one active DataSync instance per target database or export folder unless an external lock is added. Multiple instances can fetch the same range and compete for the same state. For scheduled jobs, set the platform schedule interval longer than the normal sync duration, or configure the scheduler/orchestrator to prevent overlapping executions.

| Environment | Continuous worker | Scheduled one-shot job | Notes |
| --- | --- | --- | --- |
| Azure | Azure Container Apps app, Azure App Service container, AKS deployment, or VM service. | Azure Container Apps job with a schedule trigger. | Container Apps jobs support manual, scheduled, and event-driven executions. Use Azure SQL, Azure Database for PostgreSQL/MySQL, MongoDB-compatible hosting, or a mounted persistent share for filesystem exports. |
| AWS | ECS service on Fargate/EC2, EKS deployment, or EC2 system service. | EventBridge Scheduler invoking an ECS `RunTask` target. | EventBridge Scheduler supports rate, cron, and one-time schedules for ECS tasks. Use RDS/Aurora, DocumentDB/MongoDB-compatible hosting where appropriate, or EFS for filesystem exports. |
| Google Cloud | GKE deployment or Compute Engine system service. | Cloud Run job executed on a schedule. | Cloud Run jobs are designed for finite tasks. Use Cloud SQL, MongoDB-compatible hosting where appropriate, or a mounted/shared filesystem for exports. |
| Self-hosted | Docker Compose, Kubernetes deployment, systemd service, or Windows service. | Kubernetes CronJob, host cron, Task Scheduler, or a CI/CD scheduler. | Keep secrets outside source control. For filesystem exports, mount `/app/volumes/webapi/exports` or set `FileExport:rootPath` to a durable path. |

References:

- [Azure Container Apps jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
- [Amazon ECS scheduled tasks with EventBridge Scheduler](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/tasks-scheduled-eventbridge-scheduler.html)
- [Google Cloud Run jobs](https://cloud.google.com/run/docs/create-jobs)
- [Google Cloud Run jobs on a schedule](https://cloud.google.com/run/docs/execute/jobs-on-schedule)

### Publish For Deployment

Publish with the .NET SDK:

```powershell
dotnet publish .\Sollatek.DataSync\Sollatek.DataSync.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

Common runtime identifiers:

- Windows x64: `win-x64`
- Linux x64: `linux-x64`
- macOS x64: `osx-x64`
- macOS ARM64: `osx-arm64`

The `scripts` folder contains Docker-based publish scripts for machines that should not install the .NET SDK locally. Each script takes a storage provider parameter and an optional monitoring provider parameter, then exports a self-contained single-file application:

```powershell
.\scripts\publish-datasync-windows.ps1 sqlserver
.\scripts\publish-datasync-windows.ps1 filesystem -MonitoringProvider otlp-status
```

```bash
./scripts/publish-datasync-linux.sh postgres
./scripts/publish-datasync-macos.sh mongo osx-arm64 "" Release azuremonitor
```

Default output path:

```text
.artifacts/publish/datasync-<provider>-<runtime>
```

When a non-`none` monitoring provider is selected and no custom output path is supplied, the default output path is:

```text
.artifacts/publish/datasync-<provider>-<monitoring-provider>-<runtime>
```

Publish provider values:

| Publish provider value | Provider packages included | Runtime `Storage:provider` values available in that build |
| --- | --- | --- |
| `all` | SQL, MongoDB, filesystem | `sqlserver`, `postgres`, `mysql`, `mongo`, `filesystem` |
| `sql` | SQL | `sqlserver`, `postgres`, `mysql` |
| `sqlserver` | SQL | `sqlserver`, `postgres`, `mysql` |
| `mssql` | SQL | `sqlserver`, `postgres`, `mysql` |
| `postgres` | SQL | `sqlserver`, `postgres`, `mysql` |
| `mysql` | SQL | `sqlserver`, `postgres`, `mysql` |
| `mongo` | MongoDB | `mongo` |
| `mongodb` | MongoDB | `mongo` |
| `filesystem` | Filesystem | `filesystem` |
| `files` | Filesystem | `filesystem` |

The publish storage provider controls what persistence code is included in the executable. The runtime `Storage:provider` setting controls what the executable uses when it starts. The publish monitoring provider controls which optional monitoring packages are included. Runtime monitoring settings only work when their provider was included in the published build.

## Build And Test

Use this path when you want to build, test, or modify the project.

### Prerequisites

- .NET SDK 10, matching `global.json`.
- Docker, only for Docker publish scripts, local database containers, or the local observability stack.

### Restore, Build, Test

```powershell
dotnet restore .\Sollatek.DataSync.sln
dotnet build .\Sollatek.DataSync.sln -c Release --no-restore
dotnet test .\Sollatek.DataSync.sln --no-restore --verbosity minimal
```

Local Docker database services for provider testing:

```powershell
docker compose -f docker-compose.datasync-tests.yml up -d
```

### Project Structure

- `Sollatek.DataSync`: executable worker and scheduling.
- `Sollatek.DataSync.Abstractions`: shared contracts, configuration, state, monitoring, and storage abstractions.
- `Stores\Sollatek.DataSync.Sql`: SQL Server, PostgreSQL, and MySQL provider implementation.
- `Stores\Sollatek.DataSync.Mongo`: MongoDB provider implementation.
- `Stores\Sollatek.DataSync.Filesystem`: filesystem/Parquet provider implementation.
- `Monitoring\Sollatek.DataSync.Monitoring.OpenTelemetry`: shared OpenTelemetry wiring for monitoring providers.
- `Monitoring\Sollatek.DataSync.Monitoring.Otlp`: OTLP monitoring provider.
- `Monitoring\Sollatek.DataSync.Monitoring.AzureMonitor`: Azure Monitor/Application Insights monitoring provider.
- `Monitoring\Sollatek.DataSync.Monitoring.StatusEndpoint`: HTTP status endpoint monitoring provider.
- `Platform.ApiClient`: generated API client.
- `tests`: unit and provider tests, grouped by project.
- `observability`: local OpenTelemetry Collector, Prometheus, and Grafana configuration.
- `scripts`: Docker-based publish scripts.

The worker orchestration is separate from persistence. A custom host can reference only `Sollatek.DataSync.Abstractions` plus the provider package it needs, then register that provider through `DataSyncStorageProviderRegistry`.

### API Client And Swagger

The API client is generated from Sollatek Platform swagger documents. The default runtime configuration reads:

```json
{
  "SwaggerDocuments": [
    {
      "name": "data-v1",
      "url": "https://api.sollatek.io/swagger/data-v1/swagger.json"
    },
    {
      "name": "portal-v1",
      "url": "https://api.sollatek.io/swagger/portal-v1/swagger.json"
    }
  ]
}
```

![How to get the swagger.json link from the documentation](Images/swagger_link_location.png)

Other client generators can read the same swagger documents:

- [NSwag](https://github.com/RicoSuter/NSwag)
- [Swagger Codegen](https://github.com/swagger-api/swagger-codegen)
- [Swagger Editor](https://editor.swagger.io/)

![Swagger Editor Languages](Images/swagger_editor_languages.png)

## License

This project is source-available under the MIT License with Commons Clause. You may use, copy, modify, publish, and distribute the code, including modified versions, but you may not sell the software itself. Because selling the software is restricted, this is not an OSI open-source license. See [LICENCE.md](LICENCE.md) for the full terms.
