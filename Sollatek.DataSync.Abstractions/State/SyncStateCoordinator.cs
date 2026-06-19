#nullable enable

using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.State;

public static class SyncStateCoordinator
{
    public static async Task<IReadOnlyDictionary<string, DateTimeOffset>> LoadLastSuccessfulEndsAsync(
        ISyncStateStore stateStore,
        ISyncTargetDataStore targetDataStore,
        IEnumerable<SwaggerSyncEntityMetadata> metadataEntities,
        CancellationToken cancellationToken)
    {
        var planningStates = await LoadPlanningStatesAsync(
            stateStore,
            targetDataStore,
            metadataEntities,
            cancellationToken);

        return planningStates
            .Where(x => x.Value.LastSuccessfulEnd.HasValue)
            .ToDictionary(
                x => x.Key,
                x => x.Value.LastSuccessfulEnd!.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyDictionary<string, SyncEntityPlanningState>> LoadPlanningStatesAsync(
        ISyncStateStore stateStore,
        ISyncTargetDataStore targetDataStore,
        IEnumerable<SwaggerSyncEntityMetadata> metadataEntities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(targetDataStore);
        ArgumentNullException.ThrowIfNull(metadataEntities);

        var states = new Dictionary<string, SyncEntityPlanningState>(StringComparer.OrdinalIgnoreCase);
        foreach (var metadata in metadataEntities)
        {
            var hasStoredData = await targetDataStore.HasStoredDataAsync(metadata, cancellationToken);
            if (!hasStoredData)
            {
                states[metadata.Key] = new SyncEntityPlanningState(
                    HasStoredData: false,
                    LastSuccessfulEnd: null);
                continue;
            }

            var end = await targetDataStore.GetLatestStoredWatermarkAsync(metadata, cancellationToken)
                      ?? await stateStore.GetLastSuccessfulEndAsync(metadata.Key, cancellationToken);
            states[metadata.Key] = new SyncEntityPlanningState(
                HasStoredData: true,
                LastSuccessfulEnd: end);
        }

        return states;
    }

    public static async Task SaveSuccessfulEndsAsync(
        ISyncStateStore stateStore,
        IEnumerable<SyncJob> jobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(jobs);

        foreach (var job in jobs)
        {
            await stateStore.SaveSuccessfulEndAsync(
                job.Metadata.Key,
                job.Range.End,
                cancellationToken);
        }
    }
}
