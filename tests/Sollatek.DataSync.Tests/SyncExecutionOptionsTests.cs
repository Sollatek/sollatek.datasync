using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Monitoring;

namespace Sollatek.DataSync.Tests;

public sealed class SyncExecutionOptionsTests
{
    [Fact]
    public void SyncOptions_DefaultsStartFromToStartOfCurrentYear()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = SyncOptions.FromConfiguration(
            configuration,
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), options.StartFrom);
    }

    [Fact]
    public void SyncOptions_DefaultsRunIntervalToSixHours()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = SyncOptions.FromConfiguration(
            configuration,
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromHours(6), options.RunInterval);
    }

    [Fact]
    public void SyncOptions_DefaultsToRunOnStartupWithoutStopping()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = SyncOptions.FromConfiguration(
            configuration,
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        Assert.True(options.RunOnStartup);
        Assert.False(options.StopWhenFinished);
    }

    [Fact]
    public void SyncOptions_ReadsConfiguredValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:startFrom"] = "2025-02-03T04:05:06Z",
                ["Sync:runOnStartup"] = "false",
                ["Sync:stopWhenFinished"] = "true",
                ["Sync:runInterval"] = "01:30:00",
                ["Sync:maxPageSize"] = "250"
            })
            .Build();

        var options = SyncOptions.FromConfiguration(
            configuration,
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero), options.StartFrom);
        Assert.False(options.RunOnStartup);
        Assert.True(options.StopWhenFinished);
        Assert.Equal(TimeSpan.FromMinutes(90), options.RunInterval);
        Assert.Equal(250, options.MaxPageSize);
    }

    [Theory]
    [InlineData("Sync:maxPageSize")]
    public void SyncOptions_RejectsNonPositiveNumbers(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "0"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SyncOptions.FromConfiguration(configuration, DateTimeOffset.UtcNow));

        Assert.Contains($"{key} must be greater than 0", exception.Message);
    }

    [Theory]
    [InlineData("Sync:runOnStartup")]
    [InlineData("Sync:stopWhenFinished")]
    public void SyncOptions_RejectsInvalidBooleanValues(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "yes"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SyncOptions.FromConfiguration(configuration, DateTimeOffset.UtcNow));

        Assert.Contains($"{key} must be true or false", exception.Message);
    }

    [Theory]
    [InlineData("Sync:runInterval")]
    public void SyncOptions_RejectsNonPositiveTimeSpans(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = "00:00:00"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SyncOptions.FromConfiguration(configuration, DateTimeOffset.UtcNow));

        Assert.Contains($"{key} must be greater than 0", exception.Message);
    }

    [Fact]
    public void RetryOptions_DefaultsToThreeLinearTriesWithFiveMinutePeriod()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = RetryOptions.FromConfiguration(configuration);

        Assert.Equal(3, options.MaxTries);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Period);
        Assert.Equal(RetryDelayFunction.Linear, options.DelayFunction);
    }

    [Fact]
    public void RetryOptions_ReadsConfiguredValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retry:maxTries"] = "5",
                ["Retry:period"] = "00:10:00",
                ["Retry:delayFunction"] = "fixed"
            })
            .Build();

        var options = RetryOptions.FromConfiguration(configuration);

        Assert.Equal(5, options.MaxTries);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Period);
        Assert.Equal(RetryDelayFunction.Fixed, options.DelayFunction);
    }

    [Fact]
    public void RetryOptions_RejectsUnknownDelayFunction()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retry:delayFunction"] = "exponential"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RetryOptions.FromConfiguration(configuration));

        Assert.Contains("Retry:delayFunction must be one of: fixed, linear", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_DefaultsToStructuredLogsAndMetrics()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.True(options.MetricsEnabled);
        Assert.True(options.StructuredLogsEnabled);
        Assert.Equal("sollatek-datasync", options.ServiceName);
        Assert.False(options.Otlp.Enabled);
        Assert.True(options.Otlp.MetricsEnabled);
        Assert.False(options.Otlp.LogsEnabled);
        Assert.Equal(MonitoringOtlpProtocol.Grpc, options.Otlp.Protocol);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Otlp.ExportInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Otlp.Timeout);
        Assert.False(options.AzureMonitor.Enabled);
        Assert.True(options.AzureMonitor.MetricsEnabled);
        Assert.False(options.AzureMonitor.LogsEnabled);
        Assert.Null(options.AzureMonitor.ConnectionString);
        Assert.False(options.StatusEndpoint.Enabled);
        Assert.Equal("http://127.0.0.1:5055", options.StatusEndpoint.Url);
        Assert.Equal("/status", options.StatusEndpoint.StatusPath);
        Assert.Equal("/health", options.StatusEndpoint.HealthPath);
    }

    [Fact]
    public void MonitoringOptions_ReadsOtlpExporterSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:otlp:enabled"] = "true",
                ["Monitoring:otlp:metricsEnabled"] = "true",
                ["Monitoring:otlp:logsEnabled"] = "true",
                ["Monitoring:otlp:endpoint"] = "http://collector:4317",
                ["Monitoring:otlp:logsEndpoint"] = "http://collector:4317",
                ["Monitoring:otlp:protocol"] = "grpc",
                ["Monitoring:otlp:exportInterval"] = "00:00:30",
                ["Monitoring:otlp:timeout"] = "00:00:05"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.True(options.Otlp.Enabled);
        Assert.True(options.Otlp.MetricsEnabled);
        Assert.True(options.Otlp.LogsEnabled);
        Assert.Equal(new Uri("http://collector:4317"), options.Otlp.MetricsEndpoint);
        Assert.Equal(new Uri("http://collector:4317"), options.Otlp.LogsEndpoint);
        Assert.Equal(MonitoringOtlpProtocol.Grpc, options.Otlp.Protocol);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Otlp.ExportInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Otlp.Timeout);
    }

    [Fact]
    public void MonitoringOptions_RejectsEnabledOtlpWithoutEndpoint()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:otlp:enabled"] = "true"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("Monitoring:otlp:endpoint", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_RejectsEnabledOtlpWithoutSignals()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:otlp:enabled"] = "true",
                ["Monitoring:otlp:endpoint"] = "http://collector:4317",
                ["Monitoring:otlp:metricsEnabled"] = "false",
                ["Monitoring:otlp:logsEnabled"] = "false"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("At least one OTLP signal must be enabled", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_RejectsUnknownOtlpProtocol()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:otlp:enabled"] = "true",
                ["Monitoring:otlp:endpoint"] = "http://collector:4317",
                ["Monitoring:otlp:protocol"] = "udp"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("Monitoring:otlp:protocol", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_RejectsHttpProtobufEndpointWithoutSignalPath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:otlp:enabled"] = "true",
                ["Monitoring:otlp:endpoint"] = "http://collector:4318",
                ["Monitoring:otlp:protocol"] = "httpProtobuf"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("Monitoring:otlp:metricsEndpoint", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_ReadsAzureMonitorSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:azureMonitor:enabled"] = "true",
                ["Monitoring:azureMonitor:metricsEnabled"] = "true",
                ["Monitoring:azureMonitor:logsEnabled"] = "true",
                ["Monitoring:azureMonitor:connectionString"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.True(options.AzureMonitor.Enabled);
        Assert.True(options.AzureMonitor.MetricsEnabled);
        Assert.True(options.AzureMonitor.LogsEnabled);
        Assert.Equal(
            "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            options.AzureMonitor.ConnectionString);
    }

    [Fact]
    public void MonitoringOptions_DoesNotEnableAzureMonitorWhenOnlyApplicationInsightsConnectionStringExists()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.False(options.AzureMonitor.Enabled);
        Assert.Equal(
            "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            options.AzureMonitor.ConnectionString);
        Assert.Empty(options.RequestedProviderNames);
    }

    [Fact]
    public void MonitoringOptions_ReadsApplicationInsightsConnectionStringWhenAzureMonitorIsExplicitlyEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:azureMonitor:enabled"] = "true",
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.True(options.AzureMonitor.Enabled);
        Assert.Equal(
            "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            options.AzureMonitor.ConnectionString);
        Assert.Contains(DataSyncMonitoringProviderNames.AzureMonitor, options.RequestedProviderNames);
    }

    [Fact]
    public void MonitoringOptions_DoesNotRequestOtlpProviderWhenGlobalSignalsAreDisabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:metricsEnabled"] = "false",
                ["Monitoring:structuredLogsEnabled"] = "false",
                ["Monitoring:otlp:enabled"] = "true",
                ["Monitoring:otlp:endpoint"] = "http://collector:4317"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.Empty(options.RequestedProviderNames);
    }

    [Fact]
    public void MonitoringOptions_DisabledMonitoringDoesNotValidateExternalProviderSections()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:enabled"] = "false",
                ["Monitoring:otlp:enabled"] = "true",
                ["Monitoring:statusEndpoint:url"] = "not-a-url"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Empty(options.RequestedProviderNames);
    }

    [Fact]
    public void MonitoringOptions_RejectsEnabledAzureMonitorWithoutConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:azureMonitor:enabled"] = "true"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("Monitoring:azureMonitor:connectionString", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_RejectsEnabledAzureMonitorWithoutSignals()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:azureMonitor:enabled"] = "true",
                ["Monitoring:azureMonitor:connectionString"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
                ["Monitoring:azureMonitor:metricsEnabled"] = "false",
                ["Monitoring:azureMonitor:logsEnabled"] = "false"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("At least one Azure Monitor signal must be enabled", exception.Message);
    }

    [Fact]
    public void MonitoringOptions_ReadsStatusEndpointSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:statusEndpoint:enabled"] = "true",
                ["Monitoring:statusEndpoint:url"] = "http://127.0.0.1:6060",
                ["Monitoring:statusEndpoint:statusPath"] = "/sync-status",
                ["Monitoring:statusEndpoint:healthPath"] = "/healthz"
            })
            .Build();

        var options = MonitoringOptions.FromConfiguration(configuration);

        Assert.True(options.StatusEndpoint.Enabled);
        Assert.Equal("http://127.0.0.1:6060", options.StatusEndpoint.Url);
        Assert.Equal("/sync-status", options.StatusEndpoint.StatusPath);
        Assert.Equal("/healthz", options.StatusEndpoint.HealthPath);
    }

    [Fact]
    public void MonitoringOptions_RejectsStatusEndpointPathWithoutLeadingSlash()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:statusEndpoint:enabled"] = "true",
                ["Monitoring:statusEndpoint:statusPath"] = "status"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MonitoringOptions.FromConfiguration(configuration));

        Assert.Contains("Monitoring:statusEndpoint:statusPath", exception.Message);
    }
}
