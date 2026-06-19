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
    private readonly ILogger<DocumentMetadataSyncRunner> _logger;
    private readonly IPagedApiClient _pagedApiClient;
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
    {
        _logger = logger;
        _pagedApiClient = pagedApiClient;
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
        _logger.LogInformation(
            "Starting metadata-backed document sync for {EntityKey}.",
            entityKey);

        var skip = 0;
        var requestMetadata = GetRequestMetadata(job);
        var requestRange = GetRequestRange(job);
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
                job.Metadata,
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

    private static SyncDateRange GetRequestRange(SyncJob job)
    {
        return job.DataMode == SyncDataMode.Full
            ? job.Range
            : new SyncDateRange(job.Range.Start, job.Range.End, IncludeEndFilter: false);
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
}
