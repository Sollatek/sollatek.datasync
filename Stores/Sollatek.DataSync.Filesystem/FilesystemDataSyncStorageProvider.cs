#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Export;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Storage;

namespace Sollatek.DataSync.Filesystem;

public sealed class FilesystemDataSyncStorageProvider : IDataSyncStorageProvider
{
    public IReadOnlyCollection<StorageProvider> SupportedProviders { get; } =
    [
        StorageProvider.Filesystem
    ];

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<IExportDateProvider, SystemExportDateProvider>();
        services.AddSingleton<ParquetFileExportSink>();
        services.AddSingleton<FilesystemSyncStateStore>();
        services.AddSingleton<FilesystemSyncTargetDataStore>();
        services.AddSingleton<FilesystemExportRunner>();
    }

    public ISyncJobRunner ResolveRunner(IServiceProvider services)
    {
        return services.GetRequiredService<FilesystemExportRunner>();
    }

    public ISyncStateStore ResolveStateStore(IServiceProvider services)
    {
        return services.GetRequiredService<FilesystemSyncStateStore>();
    }

    public ISyncTargetDataStore ResolveTargetDataStore(IServiceProvider services)
    {
        return services.GetRequiredService<FilesystemSyncTargetDataStore>();
    }
}
