#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage;

public sealed class DataSyncStorageProviderRegistry
{
    private readonly IReadOnlyDictionary<StorageProvider, IDataSyncStorageProvider> _providers;

    public DataSyncStorageProviderRegistry(IEnumerable<IDataSyncStorageProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var registrations = new Dictionary<StorageProvider, IDataSyncStorageProvider>();
        foreach (var provider in providers)
        {
            foreach (var supportedProvider in provider.SupportedProviders)
            {
                if (!registrations.TryAdd(supportedProvider, provider))
                {
                    throw new InvalidOperationException(
                        $"Storage provider '{supportedProvider}' is registered more than once.");
                }
            }
        }

        _providers = registrations;
    }

    public IDataSyncStorageProvider GetRequired(StorageProvider provider)
    {
        if (_providers.TryGetValue(provider, out var registeredProvider))
        {
            return registeredProvider;
        }

        throw new InvalidOperationException(
            $"Storage provider '{provider}' is not registered. Add the matching DataSync provider package before selecting this provider.");
    }
}
