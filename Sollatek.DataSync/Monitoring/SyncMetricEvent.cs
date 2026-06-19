#nullable enable

namespace Sollatek.DataSync.Monitoring;

public enum SyncMetricEvent
{
    RunStarted,
    EntityStarted,
    Progress,
    Failure,
    Success
}
