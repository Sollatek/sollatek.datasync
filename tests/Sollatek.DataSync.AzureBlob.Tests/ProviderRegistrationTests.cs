using Microsoft.Extensions.DependencyInjection;
using Sollatek.DataSync.AzureBlob;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Filesystem;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.AzureBlob.Tests;

public sealed class ProviderRegistrationTests
{
    [Fact]
    public void FilesystemRunner_UsesFilesystemSinkWhenAzureBlobProviderIsAlsoRegistered()
    {
        var filesystemProvider = new FilesystemDataSyncStorageProvider();
        var azureBlobProvider = new AzureBlobDataSyncStorageProvider();
        var services = CreateCommonServices(StorageProvider.Filesystem);

        filesystemProvider.AddServices(services);
        azureBlobProvider.AddServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var runner = filesystemProvider.ResolveRunner(serviceProvider);

        Assert.IsType<FilesystemExportRunner>(runner);
    }

    [Fact]
    public void AzureBlobProvider_UsesFilesystemStateByDefault()
    {
        var azureBlobProvider = new AzureBlobDataSyncStorageProvider();
        var services = CreateCommonServices(StorageProvider.AzureBlob);
        azureBlobProvider.AddServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var stateStore = azureBlobProvider.ResolveStateStore(serviceProvider);

        Assert.IsType<FilesystemSyncStateStore>(stateStore);
    }

    [Fact]
    public void AzureBlobProvider_UsesBlobStateWhenConfigured()
    {
        var azureBlobProvider = new AzureBlobDataSyncStorageProvider();
        var services = CreateCommonServices(StorageProvider.AzureBlob);
        services.AddSingleton(new StateOptions
        {
            Provider = StateProvider.AzureBlob,
            RootPath = "_state"
        });
        azureBlobProvider.AddServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        var stateStore = azureBlobProvider.ResolveStateStore(serviceProvider);

        Assert.IsType<AzureBlobSyncStateStore>(stateStore);
    }

    private static ServiceCollection CreateCommonServices(StorageProvider provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new StorageOptions
        {
            Provider = provider,
            ConnectionString = provider == StorageProvider.AzureBlob ? "UseDevelopmentStorage=true" : null,
            ContainerName = provider == StorageProvider.AzureBlob ? "exports" : null
        });
        services.AddSingleton(new FileExportOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "datasync-provider-registration-tests"),
            Format = "parquet"
        });
        services.AddSingleton(new StateOptions());
        services.AddSingleton(new SyncOptions
        {
            StartFrom = DateTimeOffset.UnixEpoch
        });
        services.AddSingleton<ISyncMonitor, NoopSyncMonitor>();
        services.AddSingleton<IPagedApiClient, ThrowingPagedApiClient>();
        return services;
    }

    private sealed class ThrowingPagedApiClient : IPagedApiClient
    {
        public Task<PagedApiPage> GetPageAsync(
            SwaggerSyncEntityMetadata metadata,
            SyncDateRange range,
            int top,
            int skip,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NoopSyncMonitor : ISyncMonitor
    {
        public SyncRunStatus Current => SyncRunStatus.Idle;

        public void RecordRunStarted(string runId, DateTimeOffset? startedAt = null)
        {
        }

        public void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null)
        {
        }

        public void RecordProgress(
            string runId,
            string entityKey,
            long recordsProcessed = 0,
            long pagesProcessed = 0,
            long filesProcessed = 0)
        {
        }

        public void RecordFailure(
            string runId,
            string? entityKey,
            string error,
            int tryNumber,
            DateTimeOffset? nextRetryAt,
            DateTimeOffset? failedAt = null)
        {
        }

        public void RecordSuccess(string runId, DateTimeOffset finishedAt)
        {
        }
    }
}
