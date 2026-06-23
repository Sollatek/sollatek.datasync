#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Document;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Execution;

public sealed class DocumentMetadataSyncRunner : ISyncJobRunner
{
    private static readonly TimeSpan MaxDifferentialRequestRange = TimeSpan.FromDays(1);

    private readonly ILogger<DocumentMetadataSyncRunner> _logger;
    private readonly IPagedApiClient _pagedApiClient;
    private readonly IAsyncExportRowSource _asyncExportRowSource;
    private readonly IDocumentSyncSink _sink;
    private readonly SyncOptions _syncOptions;
    private readonly StorageOptions _storageOptions;
    private readonly ISyncMonitor _monitor;

    public DocumentMetadataSyncRunner(
        ILogger<DocumentMetadataSyncRunner> logger,
        IPagedApiClient pagedApiClient,
        IDocumentSyncSink sink,
        SyncOptions syncOptions,
        StorageOptions storageOptions,
        ISyncMonitor monitor)
        : this(
            logger,
            pagedApiClient,
            UnconfiguredAsyncExportRowSource.Instance,
            sink,
            syncOptions,
            storageOptions,
            monitor)
    {
    }

    public DocumentMetadataSyncRunner(
        ILogger<DocumentMetadataSyncRunner> logger,
        IPagedApiClient pagedApiClient,
        IAsyncExportRowSource asyncExportRowSource,
        IDocumentSyncSink sink,
        SyncOptions syncOptions,
        StorageOptions storageOptions,
        ISyncMonitor monitor)
    {
        _logger = logger;
        _pagedApiClient = pagedApiClient;
        _asyncExportRowSource = asyncExportRowSource;
        _sink = sink;
        _syncOptions = syncOptions;
        _storageOptions = storageOptions;
        _monitor = monitor;
    }

    public async Task RunAsync(
        string runId,
        IReadOnlyList<SyncJob> jobs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(jobs);

        var manifest = SchemaManifest.Create(
            _storageOptions.Provider,
            jobs.Select(x => x.Metadata).ToArray());

        await _sink.PrepareAsync(manifest, cancellationToken);

        foreach (var job in jobs)
        {
            await SyncEntityAsync(runId, job, cancellationToken);
        }
    }

    private async Task SyncEntityAsync(
        string runId,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        var entityKey = job.Metadata.Key;
        _monitor.RecordEntityStarted(runId, entityKey);
        _monitor.RecordEntityRange(
            runId,
            entityKey,
            job.Range.Start,
            job.Range.End,
            job.ExpectedCompletedRangeEndUtc,
            job.LagPeriods);
        _logger.LogInformation(
            "Starting metadata-backed document sync for {EntityKey}.",
            entityKey);

        var requestMetadata = GetRequestMetadata(job);
        if (job.TransferMode == SyncTransferMode.AsyncExport)
        {
            await SyncAsyncExportAsync(
                runId,
                entityKey,
                job,
                requestMetadata,
                cancellationToken);
            return;
        }

        foreach (var requestRange in GetRequestRanges(job))
        {
            await SyncRangeAsync(
                runId,
                entityKey,
                requestMetadata,
                requestRange,
                cancellationToken);
        }
    }

    private async Task SyncAsyncExportAsync(
        string runId,
        string entityKey,
        SyncJob job,
        SwaggerSyncEntityMetadata requestMetadata,
        CancellationToken cancellationToken)
    {
        var requests = GetRequestRanges(job)
            .Select((range, index) => new AsyncExportRequest(index, job, requestMetadata, range))
            .ToArray();

        await foreach (var file in _asyncExportRowSource.PrepareOrderedAsync(requests, cancellationToken))
        {
            long rowsProcessed = 0;
            await _sink.WriteAsync(
                requestMetadata,
                CountRows(
                    _asyncExportRowSource.ReadRowsAsync(file, cancellationToken),
                    () => rowsProcessed++,
                    cancellationToken),
                cancellationToken);
            await _asyncExportRowSource.CompleteAsync(file, cancellationToken);
            _monitor.RecordProgress(
                runId,
                entityKey,
                recordsProcessed: rowsProcessed,
                filesProcessed: 1);
        }
    }

    private async Task SyncRangeAsync(
        string runId,
        string entityKey,
        SwaggerSyncEntityMetadata requestMetadata,
        SyncDateRange requestRange,
        CancellationToken cancellationToken)
    {
        var skip = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await _pagedApiClient.GetPageAsync(
                requestMetadata,
                requestRange,
                _syncOptions.MaxPageSize,
                skip,
                cancellationToken);

            if (page.Rows.Count == 0)
            {
                return;
            }

            await _sink.WriteAsync(
                requestMetadata,
                AsAsyncRows(page.Rows, cancellationToken),
                cancellationToken);
            _monitor.RecordProgress(
                runId,
                entityKey,
                recordsProcessed: page.Rows.Count,
                pagesProcessed: 1);

            var pageNumber = (skip / _syncOptions.MaxPageSize) + 1;
            if (pageNumber >= page.TotalPages)
            {
                return;
            }

            skip += _syncOptions.MaxPageSize;
        }
    }

    private static SwaggerSyncEntityMetadata GetRequestMetadata(SyncJob job)
    {
        return job.DataMode == SyncDataMode.Full
            ? job.Metadata with { Watermark = null }
            : job.Metadata;
    }

    private static IReadOnlyList<SyncDateRange> GetRequestRanges(SyncJob job)
    {
        if (job.DataMode == SyncDataMode.Full ||
            job.Metadata.Watermark == null)
        {
            return [job.Range];
        }

        if (!IsRawDataEndpoint(job.Metadata))
        {
            return [new SyncDateRange(job.Range.Start, job.Range.End)];
        }

        return SyncDateRangeSplitter.Split(job.Range, MaxDifferentialRequestRange);
    }

    private static bool IsRawDataEndpoint(SwaggerSyncEntityMetadata metadata)
    {
        return metadata.Operations.Any(operation =>
                   operation.Path.Contains("/RawData/", StringComparison.OrdinalIgnoreCase)) ||
               (metadata.Table?.StartsWith("raw_data_", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (metadata.Collection?.StartsWith("raw_data_", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static async IAsyncEnumerable<JsonElement> AsAsyncRows(
        IReadOnlyList<JsonElement> rows,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<JsonElement> CountRows(
        IAsyncEnumerable<JsonElement> rows,
        Action onRowRead,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onRowRead();
            yield return row;
        }
    }
}
