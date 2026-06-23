#nullable enable

using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring;

public sealed class LoggingSyncMonitor : ISyncMonitor
{
    private readonly ILogger<LoggingSyncMonitor> _logger;
    private readonly MonitoringOptions _options;
    private readonly ISyncMetrics _metrics;
    private readonly object _sync = new();
    private SyncRunStatus _current = SyncRunStatus.Idle;

    public LoggingSyncMonitor(ILogger<LoggingSyncMonitor> logger)
        : this(logger, MonitoringOptions.Default, NoopSyncMetrics.Instance)
    {
    }

    public LoggingSyncMonitor(ILogger<LoggingSyncMonitor> logger, MonitoringOptions options)
        : this(logger, options, NoopSyncMetrics.Instance)
    {
    }

    public LoggingSyncMonitor(
        ILogger<LoggingSyncMonitor> logger,
        MonitoringOptions options,
        ISyncMetrics metrics)
    {
        _logger = logger;
        _options = options;
        _metrics = metrics;
    }

    public SyncRunStatus Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void RecordRunStarted(string runId, DateTimeOffset? startedAt = null)
    {
        var timestamp = startedAt ?? DateTimeOffset.UtcNow;
        var current = Current;

        var status = RuntimeMemorySnapshot.Capture(current with
        {
            State = SyncRunState.Running,
            RunId = runId,
            StartedAt = timestamp,
            RecordsProcessed = 0,
            PagesProcessed = 0,
            FilesProcessed = 0,
            CurrentEntityRecordsProcessed = 0,
            CurrentEntityPagesProcessed = 0,
            CurrentEntityFilesProcessed = 0
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.RunStarted, status);

        if (ShouldLog)
        {
            _logger.LogInformation(
                "Sync run {RunId} started at {StartedAt:O}.",
                runId,
                timestamp);
        }
    }

    public void RecordRunScheduled(
        string scheduleMode,
        DateTimeOffset nextRunAtUtc,
        DateTimeOffset expectedCompletedRangeEndUtc)
    {
        var expected = expectedCompletedRangeEndUtc.ToUniversalTime();
        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            ScheduleMode = scheduleMode,
            NextRunAtUtc = nextRunAtUtc.ToUniversalTime(),
            ExpectedCompletedRangeEndUtc = expected
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Progress, status);
    }

    public void RecordRunPlanned(
        string runId,
        string scheduleMode,
        DateTimeOffset plannedRangeEndUtc,
        DateTimeOffset expectedCompletedRangeEndUtc,
        int plannedEntityCount)
    {
        var expected = expectedCompletedRangeEndUtc.ToUniversalTime();
        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            RunId = runId,
            ScheduleMode = scheduleMode,
            PlannedRangeEndUtc = plannedRangeEndUtc.ToUniversalTime(),
            ExpectedCompletedRangeEndUtc = expected,
            PlannedEntityCount = plannedEntityCount
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Progress, status);
    }

    public void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null)
    {
        var timestamp = startedAt ?? DateTimeOffset.UtcNow;

        var current = Current;
        var status = RuntimeMemorySnapshot.Capture(current with
        {
            State = SyncRunState.ProcessingEntity,
            RunId = runId,
            CurrentEntity = entityKey,
            StartedAt = current.StartedAt ?? timestamp,
            CurrentEntityRecordsProcessed = 0,
            CurrentEntityPagesProcessed = 0,
            CurrentEntityFilesProcessed = 0
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.EntityStarted, status);

        if (ShouldLog)
        {
            _logger.LogInformation(
                "Sync run {RunId} started entity {EntityKey} at {StartedAt:O}.",
                runId,
                entityKey,
                timestamp);
        }
    }

    public void RecordEntityRange(
        string runId,
        string entityKey,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        DateTimeOffset? expectedCompletedRangeEndUtc = null,
        int? lagPeriods = null)
    {
        var rangeStart = rangeStartUtc.ToUniversalTime();
        var rangeEnd = rangeEndUtc.ToUniversalTime();
        var expected = (expectedCompletedRangeEndUtc ?? Current.ExpectedCompletedRangeEndUtc ?? rangeEnd)
            .ToUniversalTime();
        var lagSeconds = GetLagSeconds(rangeStart, expected);

        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            RunId = runId,
            CurrentEntity = entityKey,
            CurrentRangeStartUtc = rangeStart,
            CurrentRangeEndUtc = rangeEnd,
            LastCompletedRangeEndUtc = rangeStart,
            ExpectedCompletedRangeEndUtc = expected,
            LagSeconds = lagSeconds,
            LagPeriods = lagPeriods
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Progress, status);
    }

    public void RecordFailure(
        string runId,
        string? entityKey,
        string error,
        int tryNumber,
        DateTimeOffset? nextRetryAt,
        DateTimeOffset? failedAt = null)
    {
        var timestamp = failedAt ?? DateTimeOffset.UtcNow;
        var state = nextRetryAt.HasValue ? SyncRunState.WaitingToRetry : SyncRunState.Failed;

        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            State = state,
            RunId = runId,
            CurrentEntity = entityKey,
            LastFailureAt = timestamp,
            LastError = error,
            TryNumber = tryNumber,
            NextRetryAt = nextRetryAt
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Failure, status);

        if (ShouldLog)
        {
            _logger.LogWarning(
                "Sync run {RunId} failed for entity {EntityKey} on try {TryNumber} at {FailedAt:O}. Next retry: {NextRetryAt:O}. Error: {Error}",
                runId,
                entityKey,
                tryNumber,
                timestamp,
                nextRetryAt,
                error);
        }
    }

    public void RecordProgress(
        string runId,
        string entityKey,
        long recordsProcessed = 0,
        long pagesProcessed = 0,
        long filesProcessed = 0)
    {
        SyncRunStatus status;
        lock (_sync)
        {
            status = RuntimeMemorySnapshot.Capture(_current with
            {
                RunId = runId,
                CurrentEntity = entityKey,
                RecordsProcessed = _current.RecordsProcessed + recordsProcessed,
                PagesProcessed = _current.PagesProcessed + pagesProcessed,
                FilesProcessed = _current.FilesProcessed + filesProcessed,
                CurrentEntityRecordsProcessed = _current.CurrentEntityRecordsProcessed + recordsProcessed,
                CurrentEntityPagesProcessed = _current.CurrentEntityPagesProcessed + pagesProcessed,
                CurrentEntityFilesProcessed = _current.CurrentEntityFilesProcessed + filesProcessed
            });
            _current = status;
        }

        _metrics.Record(SyncMetricEvent.Progress, status);

        if (ShouldLog)
        {
            _logger.LogInformation(
                "Sync run {RunId} progress for entity {EntityKey}: entity records {CurrentEntityRecordsProcessed}, entity pages {CurrentEntityPagesProcessed}, entity files {CurrentEntityFilesProcessed}; run records {RecordsProcessed}, run pages {PagesProcessed}, run files {FilesProcessed}; working set {WorkingSetBytes} bytes, managed heap {ManagedHeapBytes} bytes, peak working set {PeakWorkingSetBytes} bytes.",
                runId,
                entityKey,
                status.CurrentEntityRecordsProcessed,
                status.CurrentEntityPagesProcessed,
                status.CurrentEntityFilesProcessed,
                status.RecordsProcessed,
                status.PagesProcessed,
                status.FilesProcessed,
                status.WorkingSetBytes,
                status.ManagedHeapBytes,
                status.PeakWorkingSetBytes);
        }
    }

    public void RecordSuccess(string runId, DateTimeOffset finishedAt)
    {
        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            State = SyncRunState.Succeeded,
            RunId = runId,
            CurrentEntity = null,
            LastSuccessAt = finishedAt,
            LastError = null,
            TryNumber = 0,
            NextRetryAt = null,
            CurrentEntityRecordsProcessed = 0,
            CurrentEntityPagesProcessed = 0,
            CurrentEntityFilesProcessed = 0
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Success, status);

        if (ShouldLog)
        {
            _logger.LogInformation(
                "Sync run {RunId} succeeded at {FinishedAt:O}. Working set {WorkingSetBytes} bytes, managed heap {ManagedHeapBytes} bytes, peak working set {PeakWorkingSetBytes} bytes.",
                runId,
                finishedAt,
                status.WorkingSetBytes,
                status.ManagedHeapBytes,
                status.PeakWorkingSetBytes);
        }
    }

    public void RecordRunCompletedRange(
        DateTimeOffset completedRangeEndUtc,
        DateTimeOffset expectedCompletedRangeEndUtc,
        int lagPeriods)
    {
        var completed = completedRangeEndUtc.ToUniversalTime();
        var expected = expectedCompletedRangeEndUtc.ToUniversalTime();
        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            LastCompletedRangeEndUtc = completed,
            ExpectedCompletedRangeEndUtc = expected,
            CurrentRangeStartUtc = null,
            CurrentRangeEndUtc = null,
            LagSeconds = GetLagSeconds(completed, expected),
            LagPeriods = lagPeriods
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Progress, status);
    }

    public void RecordAsyncExportStatus(AsyncExportStatusSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var status = RuntimeMemorySnapshot.Capture(Current with
        {
            AsyncExportsPending = summary.Pending,
            AsyncExportsPolling = summary.Polling,
            AsyncExportsDownloaded = summary.Downloaded,
            AsyncExportsProcessing = summary.Processing,
            AsyncExportsFailed = summary.Failed,
            AsyncExportsExpired = summary.Expired
        });
        Update(status);
        _metrics.Record(SyncMetricEvent.Progress, status);
    }

    private bool ShouldLog => _options.Enabled && _options.StructuredLogsEnabled;

    private static long GetLagSeconds(DateTimeOffset fromUtc, DateTimeOffset expectedUtc)
    {
        var lag = expectedUtc - fromUtc;
        return lag <= TimeSpan.Zero ? 0 : Convert.ToInt64(Math.Ceiling(lag.TotalSeconds));
    }

    private void Update(SyncRunStatus status)
    {
        lock (_sync)
        {
            _current = status;
        }
    }
}
