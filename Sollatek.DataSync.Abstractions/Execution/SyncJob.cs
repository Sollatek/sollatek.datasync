#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Execution;

public sealed record SyncJob(
    SwaggerSyncEntityMetadata Metadata,
    SyncDateRange Range,
    SyncTransferMode TransferMode,
    SyncDataMode DataMode = SyncDataMode.Differential,
    bool IsInitial = false);

public enum SyncDataMode
{
    Differential,
    Full
}
