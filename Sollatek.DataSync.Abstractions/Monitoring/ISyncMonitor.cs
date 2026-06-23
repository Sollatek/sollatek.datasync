#nullable enable

namespace Sollatek.DataSync.Monitoring;

public interface ISyncMonitor
{
    SyncRunStatus Current { get; }

    void RecordRunStarted(string runId, DateTimeOffset? startedAt = null);

    void RecordRunScheduled(
        string scheduleMode,
        DateTimeOffset nextRunAtUtc,
        DateTimeOffset expectedCompletedRangeEndUtc)
    {
    }

    void RecordRunPlanned(
        string runId,
        string scheduleMode,
        DateTimeOffset plannedRangeEndUtc,
        DateTimeOffset expectedCompletedRangeEndUtc,
        int plannedEntityCount)
    {
    }

    void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null);

    void RecordEntityRange(
        string runId,
        string entityKey,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        DateTimeOffset? expectedCompletedRangeEndUtc = null,
        int? lagPeriods = null)
    {
    }

    void RecordProgress(
        string runId,
        string entityKey,
        long recordsProcessed = 0,
        long pagesProcessed = 0,
        long filesProcessed = 0);

    void RecordFailure(
        string runId,
        string? entityKey,
        string error,
        int tryNumber,
        DateTimeOffset? nextRetryAt,
        DateTimeOffset? failedAt = null);

    void RecordSuccess(string runId, DateTimeOffset finishedAt);

    void RecordRunCompletedRange(
        DateTimeOffset completedRangeEndUtc,
        DateTimeOffset expectedCompletedRangeEndUtc,
        int lagPeriods)
    {
    }

    void RecordAsyncExportStatus(AsyncExportStatusSummary summary)
    {
    }
}
