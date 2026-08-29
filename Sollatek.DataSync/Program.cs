using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.ApiClient;
using Platform.ApiClient.Base;
using Platform.ApiClient.Extension;
using Sollatek.DataSync;
#if DATASYNC_PROVIDER_AZUREBLOB
using Sollatek.DataSync.AzureBlob;
#endif
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
#if DATASYNC_PROVIDER_FILESYSTEM
using Sollatek.DataSync.Filesystem;
#endif
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Notifications;
#if DATASYNC_MONITORING_AZUREMONITOR
using Sollatek.DataSync.Monitoring.AzureMonitor;
#endif
#if DATASYNC_MONITORING_OTLP
using Sollatek.DataSync.Monitoring.Otlp;
#endif
#if DATASYNC_MONITORING_STATUSENDPOINT
using Sollatek.DataSync.Monitoring.StatusEndpoint;
#endif
#if DATASYNC_PROVIDER_MONGO
using Sollatek.DataSync.Mongo;
#endif
#if DATASYNC_PROVIDER_SQL
using Sollatek.DataSync.Sql;
#endif
using Sollatek.DataSync.State;
using Sollatek.DataSync.Storage;

using IHost host = Host.CreateDefaultBuilder(args).ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        var monitoringOptions = MonitoringOptions.FromConfiguration(context.Configuration);
        new DataSyncMonitoringProviderRegistry(CreateMonitoringProviders())
            .ConfigureLogging(logging, monitoringOptions);
    })
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.Sources.Clear();
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", true, true);
        config.AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json",
            true, true);
        config.AddUserSecrets<Program>(optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
        config.AddEnvironmentVariables("SOL_");
        config.AddCommandLine(args);
    })
    .ConfigureServices((s, services) =>
    {
        var configuration = s.Configuration;
        var oauthUrl = GetRequiredUriSetting(configuration, "Settings:oauthUrl");
        var oauthTokenEndpointPath = configuration.GetValue<string>("Settings:oauthTokenEndpointPath");
        var clientKey = GetRequiredSetting(configuration, "Settings:clientKey");
        var clientSecret = GetRequiredSetting(configuration, "Settings:clientSecret");
        var apiUrl = GetRequiredUriSetting(configuration, "Settings:apiUrl");
        var storageOptions = StorageOptions.FromConfiguration(configuration);
        var monitoringOptions = MonitoringOptions.FromConfiguration(configuration);
        var syncOptions = SyncOptions.FromConfiguration(configuration);
        var failureEmailNotificationOptions = FailureEmailNotificationOptions.FromConfiguration(configuration);
        var fileExportOptions = FileExportOptions.FromConfiguration(configuration);
        var stateOptions = StateOptions.FromConfiguration(configuration);
        ValidateStateOptions(storageOptions, stateOptions);
        var asyncExportFallbackFormat = storageOptions.Provider is StorageProvider.Filesystem or StorageProvider.AzureBlob
            ? fileExportOptions.Format
            : null;
        services.AddSingleton(syncOptions);
        services.AddSingleton(AsyncExportOptions.FromConfiguration(configuration, asyncExportFallbackFormat));
        services.AddSingleton(SyncPlanOptions.FromConfiguration(configuration));
        services.AddSingleton(RetryOptions.FromConfiguration(configuration));
        services.AddSingleton(failureEmailNotificationOptions);
        services.AddSingleton(monitoringOptions);
        services.AddSingleton(storageOptions);
        services.AddSingleton(fileExportOptions);
        services.AddSingleton(stateOptions);
        services.AddSingleton<ISyncMetrics>(sp =>
        {
            var options = sp.GetRequiredService<MonitoringOptions>();
            return options.Enabled && options.MetricsEnabled
                ? new MeterSyncMetrics(options)
                : NoopSyncMetrics.Instance;
        });
        services.AddSingleton<LoggingSyncMonitor>();
        services.AddSingleton<ISyncMonitor>(sp => sp.GetRequiredService<LoggingSyncMonitor>());
        services.AddSingleton<IFailureNotificationSender>(sp =>
            failureEmailNotificationOptions.Enabled
                ? new SmtpFailureNotificationSender(
                    failureEmailNotificationOptions,
                    sp.GetRequiredService<ILogger<SmtpFailureNotificationSender>>())
                : NoopFailureNotificationSender.Instance);
        new DataSyncMonitoringProviderRegistry(CreateMonitoringProviders())
            .AddServices(services, monitoringOptions);

        var storageProviders = new List<IDataSyncStorageProvider>();
#if DATASYNC_PROVIDER_SQL
        storageProviders.Add(new SqlDataSyncStorageProvider());
#endif
#if DATASYNC_PROVIDER_MONGO
        storageProviders.Add(new MongoDataSyncStorageProvider());
#endif
#if DATASYNC_PROVIDER_FILESYSTEM
        storageProviders.Add(new FilesystemDataSyncStorageProvider());
#endif
#if DATASYNC_PROVIDER_AZUREBLOB
        storageProviders.Add(new AzureBlobDataSyncStorageProvider());
#endif
        foreach (var provider in storageProviders)
        {
            provider.AddServices(services);
        }

        services.AddSingleton(new DataSyncStorageProviderRegistry(storageProviders));
        services.AddSingleton<ISyncJobRunner>(sp =>
        {
            var provider = sp.GetRequiredService<StorageOptions>().Provider;
            var registry = sp.GetRequiredService<DataSyncStorageProviderRegistry>();
            return registry.GetRequired(provider).ResolveRunner(sp);
        });
        services.AddSingleton<ISyncStateStore>(sp =>
        {
            var provider = sp.GetRequiredService<StorageOptions>().Provider;
            var registry = sp.GetRequiredService<DataSyncStorageProviderRegistry>();
            return registry.GetRequired(provider).ResolveStateStore(sp);
        });
        services.AddSingleton<ISyncContractStore>(sp =>
        {
            var provider = sp.GetRequiredService<StorageOptions>().Provider;
            var registry = sp.GetRequiredService<DataSyncStorageProviderRegistry>();
            return registry.GetRequired(provider).ResolveContractStore(sp);
        });
        services.AddSingleton<ISyncTargetDataStore>(sp =>
        {
            var provider = sp.GetRequiredService<StorageOptions>().Provider;
            var registry = sp.GetRequiredService<DataSyncStorageProviderRegistry>();
            return registry.GetRequired(provider).ResolveTargetDataStore(sp);
        });
        services.AddSingleton<FilesystemAsyncExportStateStore>();
        services.AddSingleton<IAsyncExportStateStore>(sp =>
        {
            var stateProvider = sp.GetRequiredService<StateOptions>().Provider;
            if (stateProvider == StateProvider.Filesystem)
            {
                return sp.GetRequiredService<FilesystemAsyncExportStateStore>();
            }

#if DATASYNC_PROVIDER_AZUREBLOB
            return sp.GetRequiredService<AzureBlobAsyncExportStateStore>();
#else
            throw new InvalidOperationException(
                "State:provider=azureBlobStorage requires the Azure Blob storage provider build.");
#endif
        });
        services.AddHttpClient();
        services.AddApiClients(apiUrl, oauthUrl, clientKey, clientSecret, oauthTokenEndpointPath);
        services.AddHttpClient<PagedApiClient>(client =>
                ConfigurePlatformHttpClient(client, apiUrl, syncOptions.ApiRequestTimeout))
            .AddHttpMessageHandler<ProtectedApiBearerTokenHandler>();
        services.AddTransient<IPagedApiClient>(sp => sp.GetRequiredService<PagedApiClient>());
        services.AddHttpClient<HttpAsyncExportRowSource>(client =>
                ConfigurePlatformHttpClient(client, apiUrl, syncOptions.ApiRequestTimeout))
            .AddHttpMessageHandler<ProtectedApiBearerTokenHandler>();
        services.AddTransient<IAsyncExportRowSource>(sp => sp.GetRequiredService<HttpAsyncExportRowSource>());
        services.AddHostedService<TimedHostedService>();
    })
    .Build();
using (var scope = host.Services.CreateScope())
{
    var l = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var storageOptions = scope.ServiceProvider.GetRequiredService<StorageOptions>();
    switch (storageOptions.Provider)
    {
        case StorageProvider.Filesystem:
            l.LogInformation("Skipping database migrations for filesystem export storage.");
            break;
        case StorageProvider.AzureBlob:
            l.LogInformation("Skipping database migrations for Azure Blob export storage.");
            break;
        case StorageProvider.SqlServer:
        case StorageProvider.Postgres:
        case StorageProvider.MySql:
        case StorageProvider.Mongo:
            l.LogInformation(
                "No startup database migrations are required for metadata-backed {Provider} storage.",
                storageOptions.Provider);
            break;
        default:
            throw new InvalidOperationException(
                $"The configured storage provider '{storageOptions.Provider}' does not have a runnable sink yet.");
    }
}

await host.RunAsync();

static void ConfigurePlatformHttpClient(HttpClient client, string apiUrl, TimeSpan timeout)
{
    client.BaseAddress = new Uri(apiUrl);
    client.Timeout = timeout;
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}

static string GetRequiredSetting(IConfiguration configuration, string key)
{
    var value = configuration.GetValue<string>(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{key} must be configured.");
    }

    return value.Trim();
}

static string GetRequiredUriSetting(IConfiguration configuration, string key)
{
    var value = GetRequiredSetting(configuration, key);
    if (!Uri.TryCreate(value, UriKind.Absolute, out _))
    {
        throw new InvalidOperationException($"{key} must be an absolute URI.");
    }

    return value;
}

static void ValidateStateOptions(
    StorageOptions storageOptions,
    StateOptions stateOptions)
{
    if (stateOptions.Provider == StateProvider.AzureBlob &&
        storageOptions.Provider != StorageProvider.AzureBlob)
    {
        throw new InvalidOperationException(
            "State:provider=azureBlobStorage is available only when Storage:provider is azureBlobStorage.");
    }
}

static IReadOnlyList<IDataSyncMonitoringProvider> CreateMonitoringProviders()
{
    var providers = new List<IDataSyncMonitoringProvider>();
#if DATASYNC_MONITORING_OTLP
    providers.Add(new OtlpMonitoringProvider());
#endif
#if DATASYNC_MONITORING_AZUREMONITOR
    providers.Add(new AzureMonitorMonitoringProvider());
#endif
#if DATASYNC_MONITORING_STATUSENDPOINT
    providers.Add(new StatusEndpointMonitoringProvider());
#endif

    return providers;
}
