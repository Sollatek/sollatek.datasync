#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Export;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Execution;

public sealed class FilesystemExportRunner : ISyncJobRunner
{
    private readonly ILogger<FilesystemExportRunner> _logger;
    private readonly IPagedApiClient _pagedApiClient;
    private readonly ParquetFileExportSink _parquetSink;
    private readonly FileExportOptions _fileExportOptions;
    private readonly IExportDateProvider _exportDateProvider;
    private readonly SyncOptions _syncOptions;
    private readonly ISyncMonitor _monitor;

    public FilesystemExportRunner(
        ILogger<FilesystemExportRunner> logger,
        IPagedApiClient pagedApiClient,
        ParquetFileExportSink parquetSink,
        FileExportOptions fileExportOptions,
        IExportDateProvider exportDateProvider,
        SyncOptions syncOptions,
        ISyncMonitor monitor)
    {
        _logger = logger;
        _pagedApiClient = pagedApiClient;
        _parquetSink = parquetSink;
        _fileExportOptions = fileExportOptions;
        _exportDateProvider = exportDateProvider;
        _syncOptions = syncOptions;
        _monitor = monitor;
    }

    public async Task RunAsync(
        string runId,
        IReadOnlyList<SyncJob> jobs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(jobs);

        foreach (var job in jobs)
        {
            await ExportEntityAsync(runId, job, cancellationToken);
        }
    }

    private async Task ExportEntityAsync(
        string runId,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        var entityKey = job.Metadata.Key;
        if (job.Metadata.Watermark == null)
        {
            throw new InvalidOperationException(
                $"Sync entity '{entityKey}' cannot be exported to daily filesystem partitions because it has no watermark metadata.");
        }

        var dailyRanges = DailyExportRangePlanner.Split(job.Range);
        if (dailyRanges.Count == 0)
        {
            _logger.LogInformation(
                "Skipping filesystem export for {EntityKey} because the sync date range is empty.",
                entityKey);
            return;
        }

        _monitor.RecordEntityStarted(runId, entityKey);
        _logger.LogInformation(
            "Starting filesystem paged export for {EntityKey} across {DayCount} day partitions.",
            entityKey,
            dailyRanges.Count);

        foreach (var dailyRange in dailyRanges)
        {
            await ExportPagedDailyRangeAsync(runId, entityKey, job, dailyRange, cancellationToken);
        }
    }

    private async Task ExportPagedDailyRangeAsync(
        string runId,
        string entityKey,
        SyncJob job,
        DailyExportRange dailyRange,
        CancellationToken cancellationToken)
    {
        var skip = 0;
        var partNumber = 0;
        var requestMetadata = GetRequestMetadata(job);

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await _pagedApiClient.GetPageAsync(
                requestMetadata,
                dailyRange.Range,
                _syncOptions.MaxPageSize,
                skip,
                cancellationToken);

            if (page.Rows.Count == 0)
            {
                return;
            }

            var rows = page.Rows.Select(ToParquetRow).ToArray();
            var outputDay = GetOutputDay(job.Metadata, dailyRange);
            var path = await _parquetSink.WriteAsync(
                entityKey,
                outputDay,
                rows,
                partNumber,
                cancellationToken);
            _monitor.RecordProgress(
                runId,
                entityKey,
                recordsProcessed: page.Rows.Count,
                pagesProcessed: 1,
                filesProcessed: path is null ? 0 : 1);

            if (path is not null)
            {
                _logger.LogInformation(
                    "Saved filesystem paged Parquet export for {EntityKey} day {Day} part {PartNumber} to {Path}.",
                    entityKey,
                    outputDay,
                    partNumber,
                    path);
            }

            var pageNumber = (skip / _syncOptions.MaxPageSize) + 1;
            if (pageNumber >= page.TotalPages)
            {
                return;
            }

            skip += _syncOptions.MaxPageSize;
            partNumber++;
        }
    }

    private DateOnly GetOutputDay(
        SwaggerSyncEntityMetadata metadata,
        DailyExportRange dailyRange)
    {
        var options = _fileExportOptions.GetEntityOptions(metadata);
        return options.PartitionDate == FileExportPartitionDateMode.ExportRunDay
            ? _exportDateProvider.UtcToday
            : dailyRange.Day;
    }

    private SwaggerSyncEntityMetadata GetRequestMetadata(SyncJob job)
    {
        var options = _fileExportOptions.GetEntityOptions(job.Metadata);
        return job.DataMode == SyncDataMode.Full || options.DataMode == FileExportDataMode.Full
            ? job.Metadata with { Watermark = null }
            : job.Metadata;
    }

    private static IReadOnlyDictionary<string, object?> ToParquetRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Paged filesystem export rows must be JSON objects.");
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in row.EnumerateObject())
        {
            values[property.Name] = ToParquetValue(property.Value);
        }

        return values;
    }

    private static object? ToParquetValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.String when value.TryGetDateTime(out var dateTime) => dateTime,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

}
