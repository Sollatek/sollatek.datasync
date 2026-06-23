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
        monitor.RecordRunScheduled(
            scheduleMode: "daily",
            nextRunAtUtc: new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero),
            expectedCompletedRangeEndUtc: new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero));
        monitor.RecordEntityRange(
            "run-1",
            "assets",
            rangeStartUtc: new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
            rangeEndUtc: new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero),
            expectedCompletedRangeEndUtc: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
            lagPeriods: 2);
        monitor.RecordAsyncExportStatus(new AsyncExportStatusSummary(
            Pending: 0,
            Polling: 7,
            Downloaded: 3,
            Processing: 1,
            Failed: 0,
            Expired: 0));
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
            Assert.Equal("daily", document.RootElement.GetProperty("scheduleMode").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("lagPeriods").GetInt32());
            Assert.Equal(172800, document.RootElement.GetProperty("lagSeconds").GetInt64());
            Assert.Equal(7, document.RootElement.GetProperty("asyncExportsPolling").GetInt32());
            Assert.Equal(3, document.RootElement.GetProperty("asyncExportsDownloaded").GetInt32());
            Assert.True(document.RootElement.GetProperty("workingSetBytes").GetInt64() > 0);
            Assert.True(document.RootElement.GetProperty("managedHeapBytes").GetInt64() >= 0);
            Assert.True(document.RootElement.GetProperty("peakWorkingSetBytes").GetInt64() > 0);

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
