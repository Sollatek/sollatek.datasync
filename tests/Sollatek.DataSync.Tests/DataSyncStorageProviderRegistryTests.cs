using Microsoft.Extensions.DependencyInjection;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class DataSyncStorageProviderRegistryTests
{
    [Fact]
    public void GetRequired_ReturnsProviderRegisteredForStorageProvider()
    {
        var services = new ServiceCollection();
        var provider = new RecordingStorageProvider([StorageProvider.Postgres, StorageProvider.MySql]);
        var registry = new DataSyncStorageProviderRegistry([provider]);

        var resolved = registry.GetRequired(StorageProvider.MySql);
        resolved.AddServices(services);

        Assert.Same(provider, resolved);
        Assert.True(provider.AddServicesCalled);
    }

    [Fact]
    public void GetRequired_ResolvesStateStoreFromSelectedProvider()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var provider = new RecordingStorageProvider([StorageProvider.Filesystem]);
        var registry = new DataSyncStorageProviderRegistry([provider]);

        var stateStore = registry.GetRequired(StorageProvider.Filesystem).ResolveStateStore(services);

        Assert.Same(provider.StateStore, stateStore);
    }

    [Fact]
    public void GetRequired_ResolvesTargetDataStoreFromSelectedProvider()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var provider = new RecordingStorageProvider([StorageProvider.Filesystem]);
        var registry = new DataSyncStorageProviderRegistry([provider]);

        var targetDataStore = registry.GetRequired(StorageProvider.Filesystem).ResolveTargetDataStore(services);

        Assert.Same(provider.TargetDataStore, targetDataStore);
    }

    [Fact]
    public void GetRequired_RejectsMissingProvider()
    {
        var registry = new DataSyncStorageProviderRegistry(
            [new RecordingStorageProvider([StorageProvider.SqlServer])]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.GetRequired(StorageProvider.Mongo));

        Assert.Contains("Storage provider 'Mongo' is not registered", exception.Message);
    }

    private sealed class RecordingStorageProvider(IReadOnlyCollection<StorageProvider> supportedProviders) : IDataSyncStorageProvider
    {
        public IReadOnlyCollection<StorageProvider> SupportedProviders { get; } = supportedProviders;

        public bool AddServicesCalled { get; private set; }

        public ISyncStateStore StateStore { get; } = new RecordingSyncStateStore();

        public ISyncTargetDataStore TargetDataStore { get; } = new RecordingSyncTargetDataStore();

        public void AddServices(IServiceCollection services)
        {
            AddServicesCalled = true;
        }

        public ISyncJobRunner ResolveRunner(IServiceProvider services)
        {
            return new RecordingSyncJobRunner();
        }

        public ISyncStateStore ResolveStateStore(IServiceProvider services)
        {
            return StateStore;
        }

        public ISyncTargetDataStore ResolveTargetDataStore(IServiceProvider services)
        {
            return TargetDataStore;
        }
    }

    private sealed class RecordingSyncJobRunner : ISyncJobRunner
    {
        public Task RunAsync(
            string runId,
            IReadOnlyList<SyncJob> jobs,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSyncStateStore : ISyncStateStore
    {
        public Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
            string entityKey,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }

        public Task SaveSuccessfulEndAsync(
            string entityKey,
            DateTimeOffset end,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSyncTargetDataStore : ISyncTargetDataStore
    {
        public Task<bool> HasStoredDataAsync(
            SwaggerSyncEntityMetadata metadata,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
            SwaggerSyncEntityMetadata metadata,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }
    }
}
