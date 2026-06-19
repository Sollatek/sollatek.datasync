#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.State;

namespace Sollatek.DataSync.Storage;

public interface IDataSyncStorageProvider
{
    IReadOnlyCollection<StorageProvider> SupportedProviders { get; }

    void AddServices(IServiceCollection services);

    ISyncJobRunner ResolveRunner(IServiceProvider services);

    ISyncStateStore ResolveStateStore(IServiceProvider services);

    ISyncTargetDataStore ResolveTargetDataStore(IServiceProvider services);
}
