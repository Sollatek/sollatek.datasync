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

        var status = new SyncRunStatus
        {
            State = SyncRunState.Running,
            RunId = runId,
            StartedAt = timestamp
        };
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

    public void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null)
    {
        var timestamp = startedAt ?? DateTimeOffset.UtcNow;

        var current = Current;
        var status = current with
        {
            State = SyncRunState.ProcessingEntity,
            RunId = runId,
            CurrentEntity = entityKey,
            StartedAt = current.StartedAt ?? timestamp
        };
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

        var status = Current with
        {
            State = state,
            RunId = runId,
            CurrentEntity = entityKey,
            LastFailureAt = timestamp,
            LastError = error,
            TryNumber = tryNumber,
            NextRetryAt = nextRetryAt
        };
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
            status = _current with
            {
                RunId = runId,
                CurrentEntity = entityKey,
                RecordsProcessed = _current.RecordsProcessed + recordsProcessed,
                PagesProcessed = _current.PagesProcessed + pagesProcessed,
                FilesProcessed = _current.FilesProcessed + filesProcessed
            };
            _current = status;
        }

        _metrics.Record(SyncMetricEvent.Progress, status);

        if (ShouldLog)
        {
            _logger.LogInformation(
                "Sync run {RunId} progress for entity {EntityKey}: records {RecordsProcessed}, pages {PagesProcessed}, files {FilesProcessed}.",
                runId,
                entityKey,
                status.RecordsProcessed,
                status.PagesProcessed,
                status.FilesProcessed);
        }
    }

    public void RecordSuccess(string runId, DateTimeOffset finishedAt)
    {
        var status = Current with
        {
            State = SyncRunState.Succeeded,
            RunId = runId,
            CurrentEntity = null,
            LastSuccessAt = finishedAt,
            LastError = null,
            TryNumber = 0,
            NextRetryAt = null
        };
        Update(status);
        _metrics.Record(SyncMetricEvent.Success, status);

        if (ShouldLog)
        {
            _logger.LogInformation(
                "Sync run {RunId} succeeded at {FinishedAt:O}.",
                runId,
                finishedAt);
        }
    }

    private bool ShouldLog => _options.Enabled && _options.StructuredLogsEnabled;

    private void Update(SyncRunStatus status)
    {
        lock (_sync)
        {
            _current = status;
        }
    }
}
