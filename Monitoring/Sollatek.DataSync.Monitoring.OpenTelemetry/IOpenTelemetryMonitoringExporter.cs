#nullable enable

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring.OpenTelemetry;

public interface IOpenTelemetryMonitoringExporter
{
    string ProviderName { get; }

    bool ShouldConfigureLogs(MonitoringOptions options);

    bool ShouldConfigureMetrics(MonitoringOptions options);

    void ConfigureLogs(OpenTelemetryLoggerOptions logging, MonitoringOptions options);

    void ConfigureMetrics(MeterProviderBuilder metrics, MonitoringOptions options);
}
