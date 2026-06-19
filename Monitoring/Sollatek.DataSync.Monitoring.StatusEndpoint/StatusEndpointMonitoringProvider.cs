#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring.StatusEndpoint;

public sealed class StatusEndpointMonitoringProvider : IDataSyncMonitoringProvider
{
    public string Name => DataSyncMonitoringProviderNames.StatusEndpoint;

    public void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options)
    {
    }

    public void AddServices(IServiceCollection services, MonitoringOptions options)
    {
        if (options.Enabled && options.StatusEndpoint.Enabled)
        {
            services.AddHostedService<StatusHttpHostedService>();
        }
    }
}
