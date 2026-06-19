using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Monitoring.StatusEndpoint;

namespace Sollatek.DataSync.Tests;

public sealed class StatusHttpHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ExposesStatusAndHealthEndpoints()
    {
        var port = GetFreeTcpPort();
        var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);
        monitor.RecordRunStarted("run-1", new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));
        monitor.RecordEntityStarted("run-1", "assets", new DateTimeOffset(2026, 6, 19, 12, 1, 0, TimeSpan.Zero));
        var options = MonitoringOptions.Default with
        {
            StatusEndpoint = new MonitoringStatusEndpointOptions
            {
                Enabled = true,
                Url = $"http://127.0.0.1:{port}",
                StatusPath = "/status",
                HealthPath = "/health"
            }
        };
        await using var service = new StatusHttpHostedService(
            NullLogger<StatusHttpHostedService>.Instance,
            options,
            monitor);

        await service.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient();

            var status = await client.GetStringAsync(
                $"http://127.0.0.1:{port}/status",
                CancellationToken.None);
            using var document = JsonDocument.Parse(status);
            Assert.Equal("ProcessingEntity", document.RootElement.GetProperty("state").GetString());
            Assert.Equal("run-1", document.RootElement.GetProperty("runId").GetString());
            Assert.Equal("assets", document.RootElement.GetProperty("currentEntity").GetString());

            var health = await client.GetStringAsync(
                $"http://127.0.0.1:{port}/health",
                CancellationToken.None);
            using var healthDocument = JsonDocument.Parse(health);
            Assert.Equal("ok", healthDocument.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_DoesNotListenWhenDisabled()
    {
        var port = GetFreeTcpPort();
        var options = MonitoringOptions.Default with
        {
            StatusEndpoint = new MonitoringStatusEndpointOptions
            {
                Enabled = false,
                Url = $"http://127.0.0.1:{port}"
            }
        };
        await using var service = new StatusHttpHostedService(
            NullLogger<StatusHttpHostedService>.Instance,
            options,
            new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance));

        await service.StartAsync(CancellationToken.None);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetStringAsync($"http://127.0.0.1:{port}/status", CancellationToken.None));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
