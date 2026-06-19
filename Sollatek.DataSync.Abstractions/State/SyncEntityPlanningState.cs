#nullable enable

namespace Sollatek.DataSync.State;

public sealed record SyncEntityPlanningState(
    bool HasStoredData,
    DateTimeOffset? LastSuccessfulEnd);
