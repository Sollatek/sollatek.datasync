using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Monitoring;

namespace Sollatek.DataSync.Tests;

public sealed class DataSyncMonitoringProviderRegistryTests
{
    [Fact]
    public void ConfigureLogging_AllowsNoProvidersWhenNoExternalMonitoringIsEnabled()
    {
        var registry = new DataSyncMonitoringProviderRegistry([]);
        using var loggerFactory = LoggerFactory.Create(builder =>
            registry.ConfigureLogging(builder, MonitoringOptions.Default));

        Assert.NotNull(loggerFactory);
    }

    [Fact]
    public void ConfigureLogging_RejectsEnabledProviderThatWasNotIncluded()
    {
        var registry = new DataSyncMonitoringProviderRegistry([]);
        var options = MonitoringOptions.Default with
        {
            Otlp = MonitoringOtlpOptions.Default with
            {
                Enabled = true,
                Endpoint = new Uri("http://collector:4317")
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LoggerFactory.Create(builder => registry.ConfigureLogging(builder, options)));

        Assert.Contains("Monitoring provider 'otlp' is enabled but is not included", exception.Message);
    }

    [Fact]
    public void AddServices_CallsOnlyEnabledProviders()
    {
        var otlp = new RecordingMonitoringProvider(DataSyncMonitoringProviderNames.Otlp);
        var status = new RecordingMonitoringProvider(DataSyncMonitoringProviderNames.StatusEndpoint);
        var registry = new DataSyncMonitoringProviderRegistry([otlp, status]);
        var services = new ServiceCollection();
        var options = MonitoringOptions.Default with
        {
            StatusEndpoint = MonitoringStatusEndpointOptions.Default with
            {
                Enabled = true
            }
        };

        registry.AddServices(services, options);

        Assert.Equal(0, otlp.AddServicesCalls);
        Assert.Equal(1, status.AddServicesCalls);
    }

    private sealed class RecordingMonitoringProvider(string name) : IDataSyncMonitoringProvider
    {
        public string Name { get; } = name;

        public int AddServicesCalls { get; private set; }

        public void ConfigureLogging(ILoggingBuilder logging, MonitoringOptions options)
        {
        }

        public void AddServices(IServiceCollection services, MonitoringOptions options)
        {
            AddServicesCalls++;
        }
    }
}
