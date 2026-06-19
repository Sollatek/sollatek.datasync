#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.State;

public interface ISyncTargetDataStore
{
    Task<bool> HasStoredDataAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken);

    Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken);
}
