#nullable enable

namespace Sollatek.DataSync.Monitoring;

public sealed record SyncRunStatus
{
    public static SyncRunStatus Idle { get; } = new() { State = SyncRunState.Idle };

    public SyncRunState State { get; init; }

    public string? RunId { get; init; }

    public string? CurrentEntity { get; init; }

    public string? ScheduleMode { get; init; }

    public DateTimeOffset? NextRunAtUtc { get; init; }

    public DateTimeOffset? PlannedRangeEndUtc { get; init; }

    public DateTimeOffset? ExpectedCompletedRangeEndUtc { get; init; }

    public DateTimeOffset? CurrentRangeStartUtc { get; init; }

    public DateTimeOffset? CurrentRangeEndUtc { get; init; }

    public DateTimeOffset? LastCompletedRangeEndUtc { get; init; }

    public long? LagSeconds { get; init; }

    public int? LagPeriods { get; init; }

    public int PlannedEntityCount { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }

    public DateTimeOffset? LastFailureAt { get; init; }

    public string? LastError { get; init; }

    public int TryNumber { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public long RecordsProcessed { get; init; }

    public long PagesProcessed { get; init; }

    public long FilesProcessed { get; init; }

    public long CurrentEntityRecordsProcessed { get; init; }

    public long CurrentEntityPagesProcessed { get; init; }

    public long CurrentEntityFilesProcessed { get; init; }

    public long? ManagedHeapBytes { get; init; }

    public long? TotalAllocatedBytes { get; init; }

    public long? WorkingSetBytes { get; init; }

    public long? PrivateMemoryBytes { get; init; }

    public long? PeakWorkingSetBytes { get; init; }

    public int AsyncExportsPending { get; init; }

    public int AsyncExportsPolling { get; init; }

    public int AsyncExportsDownloaded { get; init; }

    public int AsyncExportsProcessing { get; init; }

    public int AsyncExportsFailed { get; init; }

    public int AsyncExportsExpired { get; init; }
}

public sealed record AsyncExportStatusSummary(
    int Pending,
    int Polling,
    int Downloaded,
    int Processing,
    int Failed,
    int Expired);
