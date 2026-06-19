#nullable enable

using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Monitoring;

namespace Sollatek.DataSync.Config;

public sealed record MonitoringOptions
{
    public static MonitoringOptions Default { get; } = new();

    public bool Enabled { get; init; } = true;

    public bool MetricsEnabled { get; init; } = true;

    public bool StructuredLogsEnabled { get; init; } = true;

    public string ServiceName { get; init; } = "sollatek-datasync";

    public MonitoringOtlpOptions Otlp { get; init; } = MonitoringOtlpOptions.Default;

    public MonitoringAzureMonitorOptions AzureMonitor { get; init; } = MonitoringAzureMonitorOptions.Default;

    public MonitoringStatusEndpointOptions StatusEndpoint { get; init; } = MonitoringStatusEndpointOptions.Default;

    public IReadOnlyCollection<string> RequestedProviderNames
    {
        get
        {
            if (!Enabled)
            {
                return [];
            }

            var providers = new List<string>();
            if (Otlp.Enabled &&
                (MetricsEnabled && Otlp.MetricsEnabled ||
                 StructuredLogsEnabled && Otlp.LogsEnabled))
            {
                providers.Add(DataSyncMonitoringProviderNames.Otlp);
            }

            if (AzureMonitor.Enabled &&
                (MetricsEnabled && AzureMonitor.MetricsEnabled ||
                 StructuredLogsEnabled && AzureMonitor.LogsEnabled))
            {
                providers.Add(DataSyncMonitoringProviderNames.AzureMonitor);
            }

            if (StatusEndpoint.Enabled)
            {
                providers.Add(DataSyncMonitoringProviderNames.StatusEndpoint);
            }

            return providers;
        }
    }

    public static MonitoringOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue<bool?>("Monitoring:enabled") ?? true;
        var options = new MonitoringOptions
        {
            Enabled = enabled,
            MetricsEnabled = configuration.GetValue<bool?>("Monitoring:metricsEnabled") ?? true,
            StructuredLogsEnabled = configuration.GetValue<bool?>("Monitoring:structuredLogsEnabled") ?? true,
            ServiceName = configuration.GetValue<string>("Monitoring:serviceName") ?? "sollatek-datasync",
            Otlp = enabled ? MonitoringOtlpOptions.FromConfiguration(configuration) : MonitoringOtlpOptions.Default,
            AzureMonitor = enabled
                ? MonitoringAzureMonitorOptions.FromConfiguration(configuration)
                : MonitoringAzureMonitorOptions.Default,
            StatusEndpoint = enabled
                ? MonitoringStatusEndpointOptions.FromConfiguration(configuration)
                : MonitoringStatusEndpointOptions.Default
        };

        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            throw new InvalidOperationException("Monitoring:serviceName is required.");
        }

        return options with { ServiceName = options.ServiceName.Trim() };
    }
}

public sealed record MonitoringOtlpOptions
{
    public static MonitoringOtlpOptions Default { get; } = new();

    public bool Enabled { get; init; }

    public bool MetricsEnabled { get; init; } = true;

    public bool LogsEnabled { get; init; }

    public Uri? Endpoint { get; init; }

    public Uri? MetricsEndpointOverride { get; init; }

    public Uri? LogsEndpointOverride { get; init; }

    public MonitoringOtlpProtocol Protocol { get; init; } = MonitoringOtlpProtocol.Grpc;

    public TimeSpan ExportInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public Uri? MetricsEndpoint => MetricsEndpointOverride ?? Endpoint;

    public Uri? LogsEndpoint => LogsEndpointOverride ?? Endpoint;

    public static MonitoringOtlpOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Monitoring:otlp");
        var options = new MonitoringOtlpOptions
        {
            Enabled = section.GetValue<bool?>("enabled") ?? false,
            MetricsEnabled = section.GetValue<bool?>("metricsEnabled") ?? true,
            LogsEnabled = section.GetValue<bool?>("logsEnabled") ?? false,
            Endpoint = GetOptionalUri(section, "endpoint", "Monitoring:otlp:endpoint"),
            MetricsEndpointOverride = GetOptionalUri(
                section,
                "metricsEndpoint",
                "Monitoring:otlp:metricsEndpoint"),
            LogsEndpointOverride = GetOptionalUri(
                section,
                "logsEndpoint",
                "Monitoring:otlp:logsEndpoint"),
            Protocol = GetProtocol(section),
            ExportInterval = GetPositiveTimeSpan(
                section,
                "exportInterval",
                "Monitoring:otlp:exportInterval",
                TimeSpan.FromMinutes(1)),
            Timeout = GetPositiveTimeSpan(
                section,
                "timeout",
                "Monitoring:otlp:timeout",
                TimeSpan.FromSeconds(10))
        };

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!MetricsEnabled && !LogsEnabled)
        {
            throw new InvalidOperationException(
                "At least one OTLP signal must be enabled when Monitoring:otlp:enabled is true.");
        }

        if (MetricsEnabled && MetricsEndpoint is null)
        {
            throw new InvalidOperationException(
                "Monitoring:otlp:endpoint or Monitoring:otlp:metricsEndpoint is required when OTLP metrics are enabled.");
        }

        if (LogsEnabled && LogsEndpoint is null)
        {
            throw new InvalidOperationException(
                "Monitoring:otlp:endpoint or Monitoring:otlp:logsEndpoint is required when OTLP logs are enabled.");
        }

        if (Protocol == MonitoringOtlpProtocol.HttpProtobuf)
        {
            if (MetricsEnabled)
            {
                RequireHttpSignalPath(
                    MetricsEndpoint!,
                    "Monitoring:otlp:metricsEndpoint",
                    "/v1/metrics");
            }

            if (LogsEnabled)
            {
                RequireHttpSignalPath(
                    LogsEndpoint!,
                    "Monitoring:otlp:logsEndpoint",
                    "/v1/logs");
            }
        }
    }

    private static Uri? GetOptionalUri(IConfiguration configuration, string key, string displayName)
    {
        var value = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Scheme) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"{displayName} must be an absolute HTTP or HTTPS URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{displayName} must use http or https.");
        }

        return uri;
    }

    private static MonitoringOtlpProtocol GetProtocol(IConfiguration configuration)
    {
        var value = configuration.GetValue<string>("protocol");
        if (string.IsNullOrWhiteSpace(value))
        {
            return MonitoringOtlpProtocol.Grpc;
        }

        return value.Trim().Replace("-", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
        {
            "grpc" => MonitoringOtlpProtocol.Grpc,
            "httpprotobuf" => MonitoringOtlpProtocol.HttpProtobuf,
            _ => throw new InvalidOperationException(
                "Monitoring:otlp:protocol must be one of: grpc, httpProtobuf.")
        };
    }

    private static TimeSpan GetPositiveTimeSpan(
        IConfiguration configuration,
        string key,
        string displayName,
        TimeSpan defaultValue)
    {
        var value = configuration.GetValue<TimeSpan?>(key) ?? defaultValue;
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{displayName} must be greater than 0.");
        }

        return value;
    }

    private static void RequireHttpSignalPath(Uri endpoint, string displayName, string expectedSuffix)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{displayName} must include the OTLP HTTP/protobuf signal path ending with {expectedSuffix}.");
        }
    }
}

public enum MonitoringOtlpProtocol
{
    Grpc,
    HttpProtobuf
}

public sealed record MonitoringAzureMonitorOptions
{
    public static MonitoringAzureMonitorOptions Default { get; } = new();

    public bool Enabled { get; init; }

    public bool MetricsEnabled { get; init; } = true;

    public bool LogsEnabled { get; init; }

    public string? ConnectionString { get; init; }

    public static MonitoringAzureMonitorOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Monitoring:azureMonitor");
        var connectionString = FirstNonEmpty(
            section.GetValue<string>("connectionString"),
            configuration.GetValue<string>("APPLICATIONINSIGHTS_CONNECTION_STRING"));
        var options = new MonitoringAzureMonitorOptions
        {
            Enabled = section.GetValue<bool?>("enabled") ?? false,
            MetricsEnabled = section.GetValue<bool?>("metricsEnabled") ?? true,
            LogsEnabled = section.GetValue<bool?>("logsEnabled") ?? false,
            ConnectionString = connectionString
        };

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!MetricsEnabled && !LogsEnabled)
        {
            throw new InvalidOperationException(
                "At least one Azure Monitor signal must be enabled when Monitoring:azureMonitor:enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "Monitoring:azureMonitor:connectionString or APPLICATIONINSIGHTS_CONNECTION_STRING is required when Azure Monitor export is enabled.");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}

public sealed record MonitoringStatusEndpointOptions
{
    public static MonitoringStatusEndpointOptions Default { get; } = new();

    public bool Enabled { get; init; }

    public string Url { get; init; } = "http://127.0.0.1:5055";

    public string StatusPath { get; init; } = "/status";

    public string HealthPath { get; init; } = "/health";

    public static MonitoringStatusEndpointOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Monitoring:statusEndpoint");
        var options = new MonitoringStatusEndpointOptions
        {
            Enabled = section.GetValue<bool?>("enabled") ?? false,
            Url = section.GetValue<string>("url") ?? "http://127.0.0.1:5055",
            StatusPath = section.GetValue<string>("statusPath") ?? "/status",
            HealthPath = section.GetValue<string>("healthPath") ?? "/health"
        };

        options.Validate();
        return options with
        {
            Url = options.Url.Trim(),
            StatusPath = NormalizePath(options.StatusPath),
            HealthPath = NormalizePath(options.HealthPath)
        };
    }

    private void Validate()
    {
        if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Scheme) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("Monitoring:statusEndpoint:url must be an absolute HTTP or HTTPS URI.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Monitoring:statusEndpoint:url must use http or https.");
        }

        RequirePath(StatusPath, "Monitoring:statusEndpoint:statusPath");
        RequirePath(HealthPath, "Monitoring:statusEndpoint:healthPath");

        if (string.Equals(
            NormalizePath(StatusPath),
            NormalizePath(HealthPath),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Monitoring status and health endpoint paths must be different.");
        }
    }

    private static void RequirePath(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.Trim().StartsWith("/", StringComparison.Ordinal) ||
            path.Contains('?', StringComparison.Ordinal) ||
            path.Contains('#', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{displayName} must start with / and must not contain ? or #.");
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim();
        return normalized.Length > 1
            ? normalized.TrimEnd('/')
            : normalized;
    }
}
