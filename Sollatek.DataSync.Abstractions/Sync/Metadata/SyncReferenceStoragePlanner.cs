#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public static class SyncReferenceStoragePlanner
{
    public static SyncReferenceStorageDecision Decide(
        SwaggerSyncReferenceMetadata reference,
        IEnumerable<string> selectedEntityKeys)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var selected = new HashSet<string>(selectedEntityKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var mode = selected.Contains(reference.TargetEntity)
            ? SyncReferenceStorageMode.ForeignKey
            : SyncReferenceStorageMode.FlatValue;

        return new SyncReferenceStorageDecision(
            mode,
            reference.LocalColumn,
            reference.TargetEntity,
            reference.TargetKey);
    }
}
