#nullable enable

namespace Sollatek.DataSync.Monitoring;

public interface ISyncMetrics
{
    void Record(
        SyncMetricEvent metricEvent,
        SyncRunStatus status);
}
