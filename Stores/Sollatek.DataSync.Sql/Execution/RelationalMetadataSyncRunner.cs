#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Relational;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Execution;

public sealed class RelationalMetadataSyncRunner : ISyncJobRunner
{
    private static readonly TimeSpan MaxDifferentialRequestRange = TimeSpan.FromDays(1);

    private readonly ILogger<RelationalMetadataSyncRunner> _logger;
    private readonly IPagedApiClient _pagedApiClient;
    private readonly IAsyncExportRowSource _asyncExportRowSource;
    private readonly IRelationalSyncSink _sink;
    private readonly SyncOptions _syncOptions;
    private readonly StorageOptions _storageOptions;
    private readonly ISyncMonitor _monitor;

    public RelationalMetadataSyncRunner(
        ILogger<RelationalMetadataSyncRunner> logger,
        IPagedApiClient pagedApiClient,
        IRelationalSyncSink sink,
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

    public RelationalMetadataSyncRunner(
        ILogger<RelationalMetadataSyncRunner> logger,
        IPagedApiClient pagedApiClient,
        IAsyncExportRowSource asyncExportRowSource,
        IRelationalSyncSink sink,
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

        var selectedEntityKeys = jobs.Select(x => x.Metadata.Key).ToArray();
        var knownEntities = jobs
            .Select(x => x.Metadata)
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var tables = jobs
            .Select(job => RelationalSchemaPlanner.Plan(job.Metadata, selectedEntityKeys, knownEntities))
            .ToArray();
        var manifest = SchemaManifest.Create(
            _storageOptions.Provider,
            jobs.Select(x => x.Metadata).ToArray());

        await _sink.PrepareAsync(manifest, tables, cancellationToken);

        for (var index = 0; index < jobs.Count; index++)
        {
            await SyncEntityAsync(runId, jobs[index], tables[index], cancellationToken);
        }
    }

    private async Task SyncEntityAsync(
        string runId,
        SyncJob job,
        RelationalTablePlan table,
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
            "Starting metadata-backed relational sync for {EntityKey}.",
            entityKey);

        var requestMetadata = GetRequestMetadata(job);
        if (job.TransferMode == SyncTransferMode.AsyncExport)
        {
            await SyncAsyncExportAsync(
                runId,
                entityKey,
                table,
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
                table,
                requestMetadata,
                requestRange,
                cancellationToken);
        }
    }

    private async Task SyncAsyncExportAsync(
        string runId,
        string entityKey,
        RelationalTablePlan table,
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
                table,
                MapRows(
                    table,
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
        RelationalTablePlan table,
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
                table,
                MapRows(table, page.Rows, cancellationToken),
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

    private static async IAsyncEnumerable<RelationalRow> MapRows(
        RelationalTablePlan table,
        IReadOnlyList<JsonElement> rows,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return RelationalRowMapper.Map(table, row);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RelationalRow> MapRows(
        RelationalTablePlan table,
        IAsyncEnumerable<JsonElement> rows,
        Action onRowMapped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            onRowMapped();
            yield return RelationalRowMapper.Map(table, row);
        }
    }
}
