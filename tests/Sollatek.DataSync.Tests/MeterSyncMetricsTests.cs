using System.Diagnostics.Metrics;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Monitoring;

namespace Sollatek.DataSync.Tests;

public sealed class MeterSyncMetricsTests
{
    [Fact]
    public void Record_PublishesRunAndFailureCounters()
    {
        var serviceName = $"datasync-test-{Guid.NewGuid():N}";
        using var metrics = new MeterSyncMetrics(new MonitoringOptions { ServiceName = serviceName });
        using var listener = new MeterListener();
        var measurements = new List<(string Instrument, long Value)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == serviceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        metrics.Record(SyncMetricEvent.RunStarted, Status(SyncRunState.Running));
        metrics.Record(SyncMetricEvent.Failure, Status(
            SyncRunState.WaitingToRetry,
            tryNumber: 2,
            nextRetryAt: new DateTimeOffset(2026, 6, 18, 12, 5, 0, TimeSpan.Zero)));

        Assert.Contains(measurements, x => x.Instrument == "datasync.runs.started" && x.Value == 1);
        Assert.Contains(measurements, x => x.Instrument == "datasync.runs.failed" && x.Value == 1);
        Assert.Contains(measurements, x => x.Instrument == "datasync.retries.scheduled" && x.Value == 1);
    }

    [Fact]
    public void Record_UpdatesObservableStateGauge()
    {
        var serviceName = $"datasync-test-{Guid.NewGuid():N}";
        using var metrics = new MeterSyncMetrics(new MonitoringOptions { ServiceName = serviceName });
        using var listener = new MeterListener();
        var measurements = new List<(string Instrument, int Value)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == serviceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        metrics.Record(SyncMetricEvent.EntityStarted, Status(SyncRunState.ProcessingEntity, currentEntity: "assets"));
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, x =>
            x.Instrument == "datasync.worker.state" &&
            x.Value == (int)SyncRunState.ProcessingEntity);
    }

    [Fact]
    public void Record_UpdatesObservableProgressGauges()
    {
        var serviceName = $"datasync-test-{Guid.NewGuid():N}";
        using var metrics = new MeterSyncMetrics(new MonitoringOptions { ServiceName = serviceName });
        using var listener = new MeterListener();
        var measurements = new List<(string Instrument, long Value)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == serviceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        metrics.Record(SyncMetricEvent.Progress, Status(
            SyncRunState.ProcessingEntity,
            currentEntity: "assets",
            recordsProcessed: 7,
            pagesProcessed: 2,
            filesProcessed: 1));
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, x => x.Instrument == "datasync.records.processed" && x.Value == 7);
        Assert.Contains(measurements, x => x.Instrument == "datasync.pages.processed" && x.Value == 2);
        Assert.Contains(measurements, x => x.Instrument == "datasync.files.processed" && x.Value == 1);
    }

    [Fact]
    public void Record_UpdatesObservableMemoryGauges()
    {
        var serviceName = $"datasync-test-{Guid.NewGuid():N}";
        using var metrics = new MeterSyncMetrics(new MonitoringOptions { ServiceName = serviceName });
        using var listener = new MeterListener();
        var measurements = new List<(string Instrument, long Value)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == serviceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        metrics.Record(SyncMetricEvent.Progress, Status(
            SyncRunState.ProcessingEntity,
            currentEntity: "assets",
            managedHeapBytes: 11,
            totalAllocatedBytes: 22,
            workingSetBytes: 33,
            privateMemoryBytes: 44,
            peakWorkingSetBytes: 55));
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, x => x.Instrument == "datasync.memory.managed_heap" && x.Value == 11);
        Assert.Contains(measurements, x => x.Instrument == "datasync.memory.total_allocated" && x.Value == 22);
        Assert.Contains(measurements, x => x.Instrument == "datasync.memory.working_set" && x.Value == 33);
        Assert.Contains(measurements, x => x.Instrument == "datasync.memory.private" && x.Value == 44);
        Assert.Contains(measurements, x => x.Instrument == "datasync.memory.peak_working_set" && x.Value == 55);
    }

    [Fact]
    public void Record_UpdatesObservableLagAndAsyncExportGauges()
    {
        var serviceName = $"datasync-test-{Guid.NewGuid():N}";
        using var metrics = new MeterSyncMetrics(new MonitoringOptions { ServiceName = serviceName });
        using var listener = new MeterListener();
        var longMeasurements = new List<(string Instrument, long Value)>();
        var intMeasurements = new List<(string Instrument, int Value)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == serviceName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            longMeasurements.Add((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
            intMeasurements.Add((instrument.Name, measurement)));
        listener.Start();

        metrics.Record(SyncMetricEvent.Progress, Status(
            SyncRunState.ProcessingEntity,
            currentEntity: "assets",
            lagSeconds: 172800,
            lagPeriods: 2,
            asyncExportsPolling: 7,
            asyncExportsDownloaded: 3));
        listener.RecordObservableInstruments();

        Assert.Contains(longMeasurements, x => x.Instrument == "datasync.lag.seconds" && x.Value == 172800);
        Assert.Contains(intMeasurements, x => x.Instrument == "datasync.lag.periods" && x.Value == 2);
        Assert.Contains(intMeasurements, x => x.Instrument == "datasync.async_exports.polling" && x.Value == 7);
        Assert.Contains(intMeasurements, x => x.Instrument == "datasync.async_exports.downloaded" && x.Value == 3);
    }

    private static SyncRunStatus Status(
        SyncRunState state,
        string? currentEntity = null,
        int tryNumber = 0,
        DateTimeOffset? nextRetryAt = null,
        long recordsProcessed = 0,
        long pagesProcessed = 0,
        long filesProcessed = 0,
        long? managedHeapBytes = null,
        long? totalAllocatedBytes = null,
        long? workingSetBytes = null,
        long? privateMemoryBytes = null,
        long? peakWorkingSetBytes = null,
        long? lagSeconds = null,
        int? lagPeriods = null,
        int asyncExportsPolling = 0,
        int asyncExportsDownloaded = 0)
    {
        return new SyncRunStatus
        {
            State = state,
            RunId = "run-1",
            CurrentEntity = currentEntity,
            TryNumber = tryNumber,
            NextRetryAt = nextRetryAt,
            RecordsProcessed = recordsProcessed,
            PagesProcessed = pagesProcessed,
            FilesProcessed = filesProcessed,
            ManagedHeapBytes = managedHeapBytes,
            TotalAllocatedBytes = totalAllocatedBytes,
            WorkingSetBytes = workingSetBytes,
            PrivateMemoryBytes = privateMemoryBytes,
            PeakWorkingSetBytes = peakWorkingSetBytes,
            LagSeconds = lagSeconds,
            LagPeriods = lagPeriods,
            AsyncExportsPolling = asyncExportsPolling,
            AsyncExportsDownloaded = asyncExportsDownloaded
        };
    }
}
