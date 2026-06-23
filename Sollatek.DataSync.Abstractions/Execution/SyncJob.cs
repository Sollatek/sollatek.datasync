#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Execution;

public sealed record SyncJob(
    SwaggerSyncEntityMetadata Metadata,
    SyncDateRange Range,
    SyncTransferMode TransferMode,
    SyncDataMode DataMode = SyncDataMode.Differential,
    bool IsInitial = false,
    DateTimeOffset? ExpectedCompletedRangeEndUtc = null,
    int? LagPeriods = null);

public enum SyncDataMode
{
    Differential,
    Full
}
