ARG DOTNET_RUNTIME_IMAGE=runtime
FROM mcr.microsoft.com/dotnet/${DOTNET_RUNTIME_IMAGE}:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG DATASYNC_PROVIDER=all
ARG DATASYNC_MONITORING_PROVIDER=none
ARG DOTNET_RUNTIME_IMAGE=runtime
RUN monitoring_provider="${DATASYNC_MONITORING_PROVIDER}"; \
    runtime_image="${DOTNET_RUNTIME_IMAGE}"; \
    case "${monitoring_provider}" in \
      all|status|statusendpoint|http|otlp-status|azuremonitor-status|otlp-azuremonitor-status) \
        if [ "${runtime_image}" != "aspnet" ]; then \
          echo "Status endpoint monitoring requires DOTNET_RUNTIME_IMAGE=aspnet." >&2; \
          exit 1; \
        fi; \
        ;; \
    esac
COPY ["Sollatek.DataSync/Sollatek.DataSync.csproj", "Sollatek.DataSync/"]
COPY ["Sollatek.DataSync.Abstractions/Sollatek.DataSync.Abstractions.csproj", "Sollatek.DataSync.Abstractions/"]
COPY ["Sollatek.DataSync.Export/Sollatek.DataSync.Export.csproj", "Sollatek.DataSync.Export/"]
COPY ["Stores/Sollatek.DataSync.Filesystem/Sollatek.DataSync.Filesystem.csproj", "Stores/Sollatek.DataSync.Filesystem/"]
COPY ["Stores/Sollatek.DataSync.AzureBlob/Sollatek.DataSync.AzureBlob.csproj", "Stores/Sollatek.DataSync.AzureBlob/"]
COPY ["Monitoring/Sollatek.DataSync.Monitoring.AzureMonitor/Sollatek.DataSync.Monitoring.AzureMonitor.csproj", "Monitoring/Sollatek.DataSync.Monitoring.AzureMonitor/"]
COPY ["Monitoring/Sollatek.DataSync.Monitoring.OpenTelemetry/Sollatek.DataSync.Monitoring.OpenTelemetry.csproj", "Monitoring/Sollatek.DataSync.Monitoring.OpenTelemetry/"]
COPY ["Monitoring/Sollatek.DataSync.Monitoring.Otlp/Sollatek.DataSync.Monitoring.Otlp.csproj", "Monitoring/Sollatek.DataSync.Monitoring.Otlp/"]
COPY ["Monitoring/Sollatek.DataSync.Monitoring.StatusEndpoint/Sollatek.DataSync.Monitoring.StatusEndpoint.csproj", "Monitoring/Sollatek.DataSync.Monitoring.StatusEndpoint/"]
COPY ["Stores/Sollatek.DataSync.Mongo/Sollatek.DataSync.Mongo.csproj", "Stores/Sollatek.DataSync.Mongo/"]
COPY ["Stores/Sollatek.DataSync.Sql/Sollatek.DataSync.Sql.csproj", "Stores/Sollatek.DataSync.Sql/"]
COPY ["Platform.ApiClient/Platform.ApiClient.csproj", "Platform.ApiClient/"]
RUN dotnet restore "Sollatek.DataSync/Sollatek.DataSync.csproj" -p:DataSyncProvider="${DATASYNC_PROVIDER}" -p:DataSyncMonitoringProvider="${DATASYNC_MONITORING_PROVIDER}"
COPY . .
WORKDIR "/src/Sollatek.DataSync"
RUN dotnet build "Sollatek.DataSync.csproj" -c Release -o /app/build -p:DataSyncProvider="${DATASYNC_PROVIDER}" -p:DataSyncMonitoringProvider="${DATASYNC_MONITORING_PROVIDER}"

FROM build AS test
WORKDIR /src
RUN dotnet test "Sollatek.DataSync.sln" -c Release --disable-build-servers -m:1 --verbosity minimal

FROM test AS publish
WORKDIR "/src/Sollatek.DataSync"
ARG DATASYNC_PROVIDER=all
ARG DATASYNC_MONITORING_PROVIDER=none
ARG DATASYNC_SETTINGS_PATH=Sollatek.DataSync/appsettings.json
RUN dotnet publish "Sollatek.DataSync.csproj" -c Release -o /app/publish -p:DataSyncProvider="${DATASYNC_PROVIDER}" -p:DataSyncMonitoringProvider="${DATASYNC_MONITORING_PROVIDER}" -p:DebugType=None -p:DebugSymbols=false && \
    rm -f /app/publish/appsettings.Development.json
COPY ${DATASYNC_SETTINGS_PATH} /app/publish/appsettings.json
RUN storage_provider="${DATASYNC_PROVIDER}"; \
    if [ "${storage_provider}" = "all" ] || [ "${storage_provider}" = "sql" ] || [ "${storage_provider}" = "mssql" ]; then storage_provider="sqlserver"; fi; \
    if [ "${storage_provider}" = "mongodb" ]; then storage_provider="mongo"; fi; \
    if [ "${storage_provider}" = "files" ]; then storage_provider="filesystem"; fi; \
    if [ "${storage_provider}" = "azureblob" ] || [ "${storage_provider}" = "azureblobstorage" ] || [ "${storage_provider}" = "blob" ]; then storage_provider="azureBlobStorage"; fi; \
    sed -i "s/\"provider\": \"[^\"]*\"/\"provider\": \"${storage_provider}\"/" /app/publish/appsettings.json

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV FileExport__rootPath=/app/volumes/webapi/exports
VOLUME ["/app/volumes/webapi/exports"]
ENTRYPOINT ["dotnet", "Sollatek.DataSync.dll"]
