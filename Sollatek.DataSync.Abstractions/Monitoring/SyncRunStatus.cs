#nullable enable

namespace Sollatek.DataSync.Monitoring;

public sealed record SyncRunStatus
{
    public static SyncRunStatus Idle { get; } = new() { State = SyncRunState.Idle };

    public SyncRunState State { get; init; }

    public string? RunId { get; init; }

    public string? CurrentEntity { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }

    public DateTimeOffset? LastFailureAt { get; init; }

    public string? LastError { get; init; }

    public int TryNumber { get; init; }

    public DateTimeOffset? NextRetryAt { get; init; }

    public long RecordsProcessed { get; init; }

    public long PagesProcessed { get; init; }

    public long FilesProcessed { get; init; }
}
