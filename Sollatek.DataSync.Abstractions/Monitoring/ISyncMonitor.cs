#nullable enable

namespace Sollatek.DataSync.Monitoring;

public interface ISyncMonitor
{
    SyncRunStatus Current { get; }

    void RecordRunStarted(string runId, DateTimeOffset? startedAt = null);

    void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null);

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
}
