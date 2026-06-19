#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring;

public interface IDataSyncMonitoringProvider
{
    string Name { get; }

    void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options);

    void AddServices(IServiceCollection services, MonitoringOptions options);
}
