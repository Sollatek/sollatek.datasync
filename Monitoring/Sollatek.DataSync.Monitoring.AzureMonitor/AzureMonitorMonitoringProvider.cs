#nullable enable

using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Monitoring.OpenTelemetry;

namespace Sollatek.DataSync.Monitoring.AzureMonitor;

public sealed class AzureMonitorMonitoringProvider : IDataSyncMonitoringProvider
{
    private readonly OpenTelemetryMonitoringProvider _inner = new(new AzureMonitorMonitoringExporter());

    public string Name => DataSyncMonitoringProviderNames.AzureMonitor;

    public void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options)
    {
        _inner.ConfigureLogging(logging, options);
    }

    public void AddServices(IServiceCollection services, MonitoringOptions options)
    {
        _inner.AddServices(services, options);
    }

    private sealed class AzureMonitorMonitoringExporter : IOpenTelemetryMonitoringExporter
    {
        public string ProviderName => DataSyncMonitoringProviderNames.AzureMonitor;

        public bool ShouldConfigureLogs(MonitoringOptions options)
        {
            return options.Enabled &&
                   options.StructuredLogsEnabled &&
                   options.AzureMonitor.Enabled &&
                   options.AzureMonitor.LogsEnabled;
        }

        public bool ShouldConfigureMetrics(MonitoringOptions options)
        {
            return options.Enabled &&
                   options.MetricsEnabled &&
                   options.AzureMonitor.Enabled &&
                   options.AzureMonitor.MetricsEnabled;
        }

        public void ConfigureLogs(OpenTelemetryLoggerOptions logging, MonitoringOptions options)
        {
            logging.AddAzureMonitorLogExporter(exporterOptions =>
                exporterOptions.ConnectionString = options.AzureMonitor.ConnectionString);
        }

        public void ConfigureMetrics(MeterProviderBuilder metrics, MonitoringOptions options)
        {
            metrics.AddAzureMonitorMetricExporter(exporterOptions =>
                exporterOptions.ConnectionString = options.AzureMonitor.ConnectionString);
        }
    }
}
