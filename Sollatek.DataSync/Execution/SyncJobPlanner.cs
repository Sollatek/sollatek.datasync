#nullable enable

using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Execution;

public static class SyncJobPlanner
{
    public static IReadOnlyList<SyncJob> Plan(
        SwaggerBackedSyncPlan plan,
        SyncOptions options,
        DateTimeOffset? now = null)
    {
        return Plan(
            plan,
            options,
            SyncPlanOptions.Default,
            new Dictionary<string, SyncEntityPlanningState>(StringComparer.OrdinalIgnoreCase),
            now);
    }

    public static IReadOnlyList<SyncJob> Plan(
        SwaggerBackedSyncPlan plan,
        SyncOptions options,
        IReadOnlyDictionary<string, DateTimeOffset> lastSuccessfulEnds,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(lastSuccessfulEnds);

        var planningStates = lastSuccessfulEnds.ToDictionary(
            x => x.Key,
            x => new SyncEntityPlanningState(
                HasStoredData: true,
                LastSuccessfulEnd: x.Value),
            StringComparer.OrdinalIgnoreCase);

        return Plan(
            plan,
            options,
            SyncPlanOptions.Default,
            planningStates,
            now);
    }

    public static IReadOnlyList<SyncJob> Plan(
        SwaggerBackedSyncPlan plan,
        SyncOptions options,
        SyncPlanOptions syncPlanOptions,
        IReadOnlyDictionary<string, SyncEntityPlanningState> planningStates,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(syncPlanOptions);
        ArgumentNullException.ThrowIfNull(planningStates);

        var rangeEnd = now ?? DateTimeOffset.UtcNow;

        return plan.MetadataEntities
            .Select(metadata => BuildJob(
                metadata,
                options,
                syncPlanOptions,
                planningStates,
                rangeEnd))
            .ToArray();
    }

    private static SyncJob BuildJob(
        SwaggerSyncEntityMetadata metadata,
        SyncOptions options,
        SyncPlanOptions syncPlanOptions,
        IReadOnlyDictionary<string, SyncEntityPlanningState> planningStates,
        DateTimeOffset rangeEnd)
    {
        var state = planningStates.TryGetValue(metadata.Key, out var configuredState)
            ? configuredState
            : new SyncEntityPlanningState(HasStoredData: false, LastSuccessfulEnd: null);
        var rangeStart = state.LastSuccessfulEnd is { } lastSuccessfulEnd
            ? lastSuccessfulEnd
            : options.StartFrom;
        var range = new SyncDateRange(rangeStart, rangeEnd);
        var entityOptions = syncPlanOptions.GetEntityOptions(metadata);
        var isInitial = !state.HasStoredData;
        var dataMode = isInitial && entityOptions.Initial == SyncInitialDataMode.Full
            ? SyncDataMode.Full
            : SyncDataMode.Differential;
        return new SyncJob(metadata, range, options.TransferMode, dataMode, isInitial);
    }
}
