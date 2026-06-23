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
        _meter.CreateObservableGauge(
            "datasync.entity.records.processed",
            ObserveCurrentEntityRecordsProcessed,
            unit: "records",
            description: "Records processed for the active DataSync entity.");
        _meter.CreateObservableGauge(
            "datasync.entity.pages.processed",
            ObserveCurrentEntityPagesProcessed,
            unit: "pages",
            description: "Pages processed for the active DataSync entity.");
        _meter.CreateObservableGauge(
            "datasync.entity.files.processed",
            ObserveCurrentEntityFilesProcessed,
            unit: "files",
            description: "Files processed for the active DataSync entity.");
        _meter.CreateObservableGauge(
            "datasync.memory.managed_heap",
            ObserveManagedHeapBytes,
            unit: "By",
            description: "Current managed heap size sampled from the DataSync worker process.");
        _meter.CreateObservableGauge(
            "datasync.memory.total_allocated",
            ObserveTotalAllocatedBytes,
            unit: "By",
            description: "Total allocated bytes sampled from the DataSync worker process.");
        _meter.CreateObservableGauge(
            "datasync.memory.working_set",
            ObserveWorkingSetBytes,
            unit: "By",
            description: "Current working set sampled from the DataSync worker process.");
        _meter.CreateObservableGauge(
            "datasync.memory.private",
            ObservePrivateMemoryBytes,
            unit: "By",
            description: "Current private memory sampled from the DataSync worker process.");
        _meter.CreateObservableGauge(
            "datasync.memory.peak_working_set",
            ObservePeakWorkingSetBytes,
            unit: "By",
            description: "Peak working set sampled from the DataSync worker process.");
        _meter.CreateObservableGauge(
            "datasync.lag.seconds",
            ObserveLagSeconds,
            unit: "s",
            description: "Seconds between the latest completed range and the expected completed schedule boundary.");
        _meter.CreateObservableGauge(
            "datasync.lag.periods",
            ObserveLagPeriods,
            unit: "periods",
            description: "Schedule periods between the latest completed range and the expected completed schedule boundary.");
        _meter.CreateObservableGauge(
            "datasync.async_exports.pending",
            ObserveAsyncExportsPending,
            unit: "requests",
            description: "Async export requests currently pending local submission/polling.");
        _meter.CreateObservableGauge(
            "datasync.async_exports.polling",
            ObserveAsyncExportsPolling,
            unit: "requests",
            description: "Async export requests currently waiting for portal completion.");
        _meter.CreateObservableGauge(
            "datasync.async_exports.downloaded",
            ObserveAsyncExportsDownloaded,
            unit: "requests",
            description: "Async export requests downloaded and waiting for local processing.");
        _meter.CreateObservableGauge(
            "datasync.async_exports.processing",
            ObserveAsyncExportsProcessing,
            unit: "requests",
            description: "Async export requests currently being read into the target.");
        _meter.CreateObservableGauge(
            "datasync.async_exports.failed",
            ObserveAsyncExportsFailed,
            unit: "requests",
            description: "Async export requests in failed local state.");
        _meter.CreateObservableGauge(
            "datasync.async_exports.expired",
            ObserveAsyncExportsExpired,
            unit: "requests",
            description: "Async export requests in expired local state.");
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

    private Measurement<long> ObserveCurrentEntityRecordsProcessed()
    {
        var status = Current;
        return new Measurement<long>(status.CurrentEntityRecordsProcessed, Tags(status));
    }

    private Measurement<long> ObserveCurrentEntityPagesProcessed()
    {
        var status = Current;
        return new Measurement<long>(status.CurrentEntityPagesProcessed, Tags(status));
    }

    private Measurement<long> ObserveCurrentEntityFilesProcessed()
    {
        var status = Current;
        return new Measurement<long>(status.CurrentEntityFilesProcessed, Tags(status));
    }

    private Measurement<long> ObserveManagedHeapBytes()
    {
        var status = Current;
        return new Measurement<long>(status.ManagedHeapBytes ?? 0, Tags(status));
    }

    private Measurement<long> ObserveTotalAllocatedBytes()
    {
        var status = Current;
        return new Measurement<long>(status.TotalAllocatedBytes ?? 0, Tags(status));
    }

    private Measurement<long> ObserveWorkingSetBytes()
    {
        var status = Current;
        return new Measurement<long>(status.WorkingSetBytes ?? 0, Tags(status));
    }

    private Measurement<long> ObservePrivateMemoryBytes()
    {
        var status = Current;
        return new Measurement<long>(status.PrivateMemoryBytes ?? 0, Tags(status));
    }

    private Measurement<long> ObservePeakWorkingSetBytes()
    {
        var status = Current;
        return new Measurement<long>(status.PeakWorkingSetBytes ?? 0, Tags(status));
    }

    private Measurement<long> ObserveLagSeconds()
    {
        var status = Current;
        return new Measurement<long>(status.LagSeconds ?? 0, Tags(status));
    }

    private Measurement<int> ObserveLagPeriods()
    {
        var status = Current;
        return new Measurement<int>(status.LagPeriods ?? 0, Tags(status));
    }

    private Measurement<int> ObserveAsyncExportsPending()
    {
        var status = Current;
        return new Measurement<int>(status.AsyncExportsPending, Tags(status));
    }

    private Measurement<int> ObserveAsyncExportsPolling()
    {
        var status = Current;
        return new Measurement<int>(status.AsyncExportsPolling, Tags(status));
    }

    private Measurement<int> ObserveAsyncExportsDownloaded()
    {
        var status = Current;
        return new Measurement<int>(status.AsyncExportsDownloaded, Tags(status));
    }

    private Measurement<int> ObserveAsyncExportsProcessing()
    {
        var status = Current;
        return new Measurement<int>(status.AsyncExportsProcessing, Tags(status));
    }

    private Measurement<int> ObserveAsyncExportsFailed()
    {
        var status = Current;
        return new Measurement<int>(status.AsyncExportsFailed, Tags(status));
    }

    private Measurement<int> ObserveAsyncExportsExpired()
    {
        var status = Current;
        return new Measurement<int>(status.AsyncExportsExpired, Tags(status));
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
