#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring;

public sealed class DataSyncMonitoringProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IDataSyncMonitoringProvider> _providers;

    public DataSyncMonitoringProviderRegistry(IEnumerable<IDataSyncMonitoringProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var registrations = new Dictionary<string, IDataSyncMonitoringProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                throw new InvalidOperationException("Monitoring providers must declare a non-empty name.");
            }

            if (!registrations.TryAdd(provider.Name.Trim(), provider))
            {
                throw new InvalidOperationException(
                    $"Monitoring provider '{provider.Name}' is registered more than once.");
            }
        }

        _providers = registrations;
    }

    public void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var provider in GetRequestedProviders(options))
        {
            provider.ConfigureLogging(logging, options);
        }
    }

    public void AddServices(IServiceCollection services, MonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var provider in GetRequestedProviders(options))
        {
            provider.AddServices(services, options);
        }
    }

    private IReadOnlyList<IDataSyncMonitoringProvider> GetRequestedProviders(MonitoringOptions options)
    {
        var requestedNames = options.RequestedProviderNames;
        if (requestedNames.Count == 0)
        {
            return [];
        }

        var requestedProviders = new List<IDataSyncMonitoringProvider>(requestedNames.Count);
        foreach (var providerName in requestedNames)
        {
            if (_providers.TryGetValue(providerName, out var provider))
            {
                requestedProviders.Add(provider);
                continue;
            }

            throw new InvalidOperationException(
                $"Monitoring provider '{providerName}' is enabled but is not included in this build. Publish with DataSyncMonitoringProvider including '{providerName}', or disable that Monitoring section.");
        }

        return requestedProviders;
    }
}
