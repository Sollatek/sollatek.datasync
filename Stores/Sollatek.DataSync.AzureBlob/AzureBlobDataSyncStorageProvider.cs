#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Export;
using Sollatek.DataSync.Filesystem;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Storage;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobDataSyncStorageProvider : IDataSyncStorageProvider
{
    public IReadOnlyCollection<StorageProvider> SupportedProviders { get; } =
    [
        StorageProvider.AzureBlob
    ];

    public void AddServices(IServiceCollection services)
    {
        services.TryAddSingleton(new StateOptions());
        services.AddSingleton<IExportDateProvider, SystemExportDateProvider>();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<StorageOptions>();
            return AzureBlobContainerClientFactory.Create(options);
        });
        services.AddSingleton<IBlobExportContainer, AzureBlobExportContainer>();
        services.AddSingleton<IBlobStateContainer, AzureBlobStateContainer>();
        services.AddSingleton<AzureBlobFileExportObjectSink>();
        services.AddSingleton<FilesystemSyncStateStore>();
        services.AddSingleton<AzureBlobSyncStateStore>();
        services.AddSingleton<AzureBlobAsyncExportStateStore>();
        services.AddSingleton<AzureBlobSyncTargetDataStore>();
    }

    public ISyncJobRunner ResolveRunner(IServiceProvider services)
    {
        return ActivatorUtilities.CreateInstance<FilesystemExportRunner>(
            services,
            services.GetRequiredService<AzureBlobFileExportObjectSink>());
    }

    public ISyncStateStore ResolveStateStore(IServiceProvider services)
    {
        var stateOptions = services.GetRequiredService<StateOptions>();
        return stateOptions.Provider == StateProvider.AzureBlob
            ? services.GetRequiredService<AzureBlobSyncStateStore>()
            : services.GetRequiredService<FilesystemSyncStateStore>();
    }

    public ISyncTargetDataStore ResolveTargetDataStore(IServiceProvider services)
    {
        return services.GetRequiredService<AzureBlobSyncTargetDataStore>();
    }
}
