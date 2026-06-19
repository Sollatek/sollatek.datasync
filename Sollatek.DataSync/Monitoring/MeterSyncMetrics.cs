#nullable enable

using System.Diagnostics.Metrics;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Monitoring;

public sealed class MeterSyncMetrics : ISyncMetrics, IDisposable
{
    private readonly MonitoringOptions _options;
    private readonly Meter _meter;
    private readonly Counter<long> _runsStarted;
    private readonly Counter<long> _runsSucceeded;
    private readonly Counter<long> _runsFailed;
    private readonly Counter<long> _entitiesStarted;
    private readonly Counter<long> _retriesScheduled;
    private readonly object _sync = new();
    private SyncRunStatus _current = SyncRunStatus.Idle;

    public MeterSyncMetrics(MonitoringOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _meter = new Meter(_options.ServiceName);
        _runsStarted = _meter.CreateCounter<long>(
            "datasync.runs.started",
            unit: "runs",
            description: "Number of DataSync runs started.");
        _runsSucceeded = _meter.CreateCounter<long>(
            "datasync.runs.succeeded",
            unit: "runs",
            description: "Number of DataSync runs that completed successfully.");
        _runsFailed = _meter.CreateCounter<long>(
            "datasync.runs.failed",
            unit: "runs",
            description: "Number of DataSync run failures.");
        _entitiesStarted = _meter.CreateCounter<long>(
            "datasync.entities.started",
            unit: "entities",
            description: "Number of entity sync/export attempts started.");
        _retriesScheduled = _meter.CreateCounter<long>(
            "datasync.retries.scheduled",
            unit: "retries",
            description: "Number of retry waits scheduled after a failure.");
        _meter.CreateObservableGauge(
            "datasync.worker.state",
            ObserveWorkerState,
            unit: "state",
            description: "Current DataSync worker state as the SyncRunState numeric value.");
        _meter.CreateObservableGauge(
            "datasync.retry.attempt",
            ObserveRetryAttempt,
            unit: "tries",
            description: "Current retry attempt number for the active status.");
        _meter.CreateObservableGauge(
            "datasync.records.processed",
            ObserveRecordsProcessed,
            unit: "records",
            description: "Records processed in the current DataSync status.");
        _meter.CreateObservableGauge(
            "datasync.pages.processed",
            ObservePagesProcessed,
            unit: "pages",
            description: "Pages processed in the current DataSync status.");
        _meter.CreateObservableGauge(
            "datasync.files.processed",
            ObserveFilesProcessed,
            unit: "files",
            description: "Files processed in the current DataSync status.");
    }

    public void Record(
        SyncMetricEvent metricEvent,
        SyncRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!_options.Enabled || !_options.MetricsEnabled)
        {
            return;
        }

        lock (_sync)
        {
            _current = status;
        }

        switch (metricEvent)
        {
            case SyncMetricEvent.RunStarted:
                _runsStarted.Add(1, Tags(status));
                break;
            case SyncMetricEvent.EntityStarted:
                _entitiesStarted.Add(1, Tags(status));
                break;
            case SyncMetricEvent.Progress:
                break;
            case SyncMetricEvent.Failure:
                _runsFailed.Add(1, Tags(status));
                if (status.NextRetryAt.HasValue)
                {
                    _retriesScheduled.Add(1, Tags(status));
                }

                break;
            case SyncMetricEvent.Success:
                _runsSucceeded.Add(1, Tags(status));
                break;
            default:
                throw new InvalidOperationException($"Unsupported sync metric event '{metricEvent}'.");
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private Measurement<int> ObserveWorkerState()
    {
        var status = Current;
        return new Measurement<int>((int)status.State, Tags(status));
    }

    private Measurement<int> ObserveRetryAttempt()
    {
        var status = Current;
        return new Measurement<int>(status.TryNumber, Tags(status));
    }

    private Measurement<long> ObserveRecordsProcessed()
    {
        var status = Current;
        return new Measurement<long>(status.RecordsProcessed, Tags(status));
    }

    private Measurement<long> ObservePagesProcessed()
    {
        var status = Current;
        return new Measurement<long>(status.PagesProcessed, Tags(status));
    }

    private Measurement<long> ObserveFilesProcessed()
    {
        var status = Current;
        return new Measurement<long>(status.FilesProcessed, Tags(status));
    }

    private SyncRunStatus Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    private static KeyValuePair<string, object?>[] Tags(SyncRunStatus status)
    {
        return
        [
            new("state", status.State.ToString()),
            new("entity", status.CurrentEntity ?? "none")
        ];
    }
}
