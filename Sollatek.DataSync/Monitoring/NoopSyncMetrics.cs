#nullable enable

namespace Sollatek.DataSync.Monitoring;

public sealed class NoopSyncMetrics : ISyncMetrics
{
    public static NoopSyncMetrics Instance { get; } = new();

    private NoopSyncMetrics()
    {
    }

    public void Record(
        SyncMetricEvent metricEvent,
        SyncRunStatus status)
    {
    }
}
