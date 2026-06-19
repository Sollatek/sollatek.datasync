using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Monitoring;

namespace Sollatek.DataSync.Tests;

public sealed class LoggingSyncMonitorTests
{
    [Fact]
    public void RecordFailure_StoresCurrentRetryStatus()
    {
        var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);
        var nextRetry = new DateTimeOffset(2026, 6, 18, 12, 5, 0, TimeSpan.Zero);

        monitor.RecordRunStarted("run-1");
        monitor.RecordEntityStarted("run-1", "assets");
        monitor.RecordFailure("run-1", "assets", "API timeout", tryNumber: 2, nextRetry);

        var status = monitor.Current;

        Assert.Equal("run-1", status.RunId);
        Assert.Equal(SyncRunState.WaitingToRetry, status.State);
        Assert.Equal("assets", status.CurrentEntity);
        Assert.Equal(2, status.TryNumber);
        Assert.Equal(nextRetry, status.NextRetryAt);
        Assert.Contains("API timeout", status.LastError);
    }

    [Fact]
    public void RecordSuccess_StoresLastSuccessfulRun()
    {
        var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);
        var finishedAt = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

        monitor.RecordRunStarted("run-1");
        monitor.RecordSuccess("run-1", finishedAt);

        var status = monitor.Current;

        Assert.Equal(SyncRunState.Succeeded, status.State);
        Assert.Equal(finishedAt, status.LastSuccessAt);
        Assert.Null(status.CurrentEntity);
        Assert.Null(status.NextRetryAt);
    }

    [Fact]
    public void RecordFailure_EmitsStatusMetrics()
    {
        var metrics = new RecordingSyncMetrics();
        var monitor = new LoggingSyncMonitor(
            NullLogger<LoggingSyncMonitor>.Instance,
            MonitoringOptions.Default,
            metrics);
        var nextRetry = new DateTimeOffset(2026, 6, 18, 12, 5, 0, TimeSpan.Zero);

        monitor.RecordRunStarted("run-1");
        monitor.RecordEntityStarted("run-1", "assets");
        monitor.RecordFailure("run-1", "assets", "API timeout", tryNumber: 2, nextRetry);

        Assert.Equal(
            [SyncMetricEvent.RunStarted, SyncMetricEvent.EntityStarted, SyncMetricEvent.Failure],
            metrics.Events.Select(x => x.Event).ToArray());
        var failure = metrics.Events.Last().Status;
        Assert.Equal(SyncRunState.WaitingToRetry, failure.State);
        Assert.Equal("assets", failure.CurrentEntity);
        Assert.Equal(2, failure.TryNumber);
    }

    [Fact]
    public void RecordProgress_AccumulatesProcessedCounts()
    {
        var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);

        monitor.RecordRunStarted("run-1");
        monitor.RecordEntityStarted("run-1", "assets");
        monitor.RecordProgress("run-1", "assets", recordsProcessed: 5, pagesProcessed: 1);
        monitor.RecordProgress(
            "run-1",
            "assets",
            filesProcessed: 1);

        var status = monitor.Current;

        Assert.Equal(5, status.RecordsProcessed);
        Assert.Equal(1, status.PagesProcessed);
        Assert.Equal(1, status.FilesProcessed);
    }

    private sealed class RecordingSyncMetrics : ISyncMetrics
    {
        public List<(SyncMetricEvent Event, SyncRunStatus Status)> Events { get; } = [];

        public void Record(SyncMetricEvent metricEvent, SyncRunStatus status)
        {
            Events.Add((metricEvent, status));
        }
    }
}
