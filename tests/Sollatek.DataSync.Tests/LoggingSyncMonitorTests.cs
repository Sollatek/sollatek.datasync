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
        Assert.Equal(5, status.CurrentEntityRecordsProcessed);
        Assert.Equal(1, status.CurrentEntityPagesProcessed);
        Assert.Equal(1, status.CurrentEntityFilesProcessed);
        Assert.True(status.ManagedHeapBytes >= 0);
        Assert.True(status.TotalAllocatedBytes >= 0);
        Assert.True(status.WorkingSetBytes > 0);
        Assert.True(status.PrivateMemoryBytes > 0);
        Assert.True(status.PeakWorkingSetBytes > 0);
    }

    [Fact]
    public void RecordEntityRange_StoresScheduleLagAndRangeStatus()
    {
        var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);
        var expectedCompleted = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

        monitor.RecordRunStarted("run-1");
        monitor.RecordRunPlanned(
            "run-1",
            scheduleMode: "daily",
            plannedRangeEndUtc: expectedCompleted,
            expectedCompletedRangeEndUtc: expectedCompleted,
            plannedEntityCount: 2);
        monitor.RecordEntityRange(
            "run-1",
            "rawDataTemperaturedata",
            rangeStartUtc: new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero),
            rangeEndUtc: new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero),
            expectedCompletedRangeEndUtc: expectedCompleted,
            lagPeriods: 2);

        var status = monitor.Current;

        Assert.Equal("daily", status.ScheduleMode);
        Assert.Equal(expectedCompleted, status.PlannedRangeEndUtc);
        Assert.Equal(expectedCompleted, status.ExpectedCompletedRangeEndUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero), status.CurrentRangeStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero), status.CurrentRangeEndUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero), status.LastCompletedRangeEndUtc);
        Assert.Equal(172800, status.LagSeconds);
        Assert.Equal(2, status.LagPeriods);
        Assert.Equal(2, status.PlannedEntityCount);
    }

    [Fact]
    public void RecordAsyncExportStatus_StoresQueueCounters()
    {
        var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);

        monitor.RecordAsyncExportStatus(new AsyncExportStatusSummary(
            Pending: 1,
            Polling: 2,
            Downloaded: 3,
            Processing: 4,
            Failed: 5,
            Expired: 6));

        var status = monitor.Current;

        Assert.Equal(1, status.AsyncExportsPending);
        Assert.Equal(2, status.AsyncExportsPolling);
        Assert.Equal(3, status.AsyncExportsDownloaded);
        Assert.Equal(4, status.AsyncExportsProcessing);
        Assert.Equal(5, status.AsyncExportsFailed);
        Assert.Equal(6, status.AsyncExportsExpired);
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
