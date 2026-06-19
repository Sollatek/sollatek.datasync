#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring.OpenTelemetry;

public sealed class OpenTelemetryMonitoringProvider : IDataSyncMonitoringProvider
{
    private readonly IOpenTelemetryMonitoringExporter _exporter;

    public OpenTelemetryMonitoringProvider(IOpenTelemetryMonitoringExporter exporter)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    }

    public string Name => _exporter.ProviderName;

    public void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(options);

        if (!_exporter.ShouldConfigureLogs(options))
        {
            return;
        }

        logging.AddOpenTelemetry(openTelemetry =>
        {
            openTelemetry.IncludeFormattedMessage = true;
            openTelemetry.IncludeScopes = true;
            openTelemetry.ParseStateValues = true;
            openTelemetry.SetResourceBuilder(BuildResource(options));
            _exporter.ConfigureLogs(openTelemetry, options);
        });
    }

    public void AddServices(IServiceCollection services, MonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!_exporter.ShouldConfigureMetrics(options))
        {
            return;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(options.ServiceName);
                _exporter.ConfigureMetrics(metrics, options);
            });
    }

    private static ResourceBuilder BuildResource(MonitoringOptions options)
    {
        return ResourceBuilder.CreateDefault().AddService(options.ServiceName);
    }
}
