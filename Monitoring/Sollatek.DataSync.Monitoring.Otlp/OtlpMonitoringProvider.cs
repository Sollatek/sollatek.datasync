#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Monitoring.OpenTelemetry;

namespace Sollatek.DataSync.Monitoring.Otlp;

public sealed class OtlpMonitoringProvider : IDataSyncMonitoringProvider
{
    private readonly OpenTelemetryMonitoringProvider _inner = new(new OtlpMonitoringExporter());

    public string Name => DataSyncMonitoringProviderNames.Otlp;

    public void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options)
    {
        _inner.ConfigureLogging(logging, options);
    }

    public void AddServices(IServiceCollection services, MonitoringOptions options)
    {
        _inner.AddServices(services, options);
    }

    private sealed class OtlpMonitoringExporter : IOpenTelemetryMonitoringExporter
    {
        public string ProviderName => DataSyncMonitoringProviderNames.Otlp;

        public bool ShouldConfigureLogs(MonitoringOptions options)
        {
            return options.Enabled &&
                   options.StructuredLogsEnabled &&
                   options.Otlp.Enabled &&
                   options.Otlp.LogsEnabled;
        }

        public bool ShouldConfigureMetrics(MonitoringOptions options)
        {
            return options.Enabled &&
                   options.MetricsEnabled &&
                   options.Otlp.Enabled &&
                   options.Otlp.MetricsEnabled;
        }

        public void ConfigureLogs(OpenTelemetryLoggerOptions logging, MonitoringOptions options)
        {
            logging.AddOtlpExporter(exporterOptions =>
                ConfigureExporter(exporterOptions, options.Otlp, options.Otlp.LogsEndpoint!));
        }

        public void ConfigureMetrics(MeterProviderBuilder metrics, MonitoringOptions options)
        {
            metrics.AddOtlpExporter((exporterOptions, readerOptions) =>
            {
                ConfigureExporter(exporterOptions, options.Otlp, options.Otlp.MetricsEndpoint!);
                readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                    ToMilliseconds(options.Otlp.ExportInterval);
                readerOptions.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds =
                    ToMilliseconds(options.Otlp.Timeout);
            });
        }

        private static void ConfigureExporter(
            OtlpExporterOptions exporterOptions,
            MonitoringOtlpOptions monitoringOptions,
            Uri endpoint)
        {
            exporterOptions.Endpoint = endpoint;
            exporterOptions.Protocol = monitoringOptions.Protocol switch
            {
                MonitoringOtlpProtocol.Grpc => OtlpExportProtocol.Grpc,
                MonitoringOtlpProtocol.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
                _ => throw new InvalidOperationException(
                    $"Unsupported OTLP protocol '{monitoringOptions.Protocol}'.")
            };
            exporterOptions.TimeoutMilliseconds = ToMilliseconds(monitoringOptions.Timeout);
        }

        private static int ToMilliseconds(TimeSpan value)
        {
            var milliseconds = (long)Math.Ceiling(value.TotalMilliseconds);
            if (milliseconds > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Monitoring OTLP time values must be less than Int32.MaxValue milliseconds.");
            }

            return (int)milliseconds;
        }
    }
}
