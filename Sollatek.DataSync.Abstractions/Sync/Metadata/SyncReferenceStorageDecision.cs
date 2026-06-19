#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public enum SyncReferenceStorageMode
{
    FlatValue,
    ForeignKey
}

public sealed record SyncReferenceStorageDecision(
    SyncReferenceStorageMode Mode,
    string LocalColumn,
    string TargetEntity,
    string TargetKey);
