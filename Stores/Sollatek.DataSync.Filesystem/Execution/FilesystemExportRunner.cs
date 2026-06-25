#nullable enable

using System.Globalization;
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
    private readonly IAsyncExportRowSource _asyncExportRowSource;
    private readonly IFileExportObjectSink _objectSink;
    private readonly FileExportOptions _fileExportOptions;
    private readonly IExportDateProvider _exportDateProvider;
    private readonly SyncOptions _syncOptions;
    private readonly ISyncMonitor _monitor;

    public FilesystemExportRunner(
        ILogger<FilesystemExportRunner> logger,
        IPagedApiClient pagedApiClient,
        IFileExportObjectSink objectSink,
        FileExportOptions fileExportOptions,
        IExportDateProvider exportDateProvider,
        SyncOptions syncOptions,
        ISyncMonitor monitor)
        : this(
            logger,
            pagedApiClient,
            UnconfiguredAsyncExportRowSource.Instance,
            objectSink,
            fileExportOptions,
            exportDateProvider,
            syncOptions,
            monitor)
    {
    }

    public FilesystemExportRunner(
        ILogger<FilesystemExportRunner> logger,
        IPagedApiClient pagedApiClient,
        IAsyncExportRowSource asyncExportRowSource,
        IFileExportObjectSink objectSink,
        FileExportOptions fileExportOptions,
        IExportDateProvider exportDateProvider,
        SyncOptions syncOptions,
        ISyncMonitor monitor)
    {
        _logger = logger;
        _pagedApiClient = pagedApiClient;
        _asyncExportRowSource = asyncExportRowSource;
        _objectSink = objectSink;
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

        var workItems = new List<DailyExportWorkItem>();
        foreach (var (job, jobIndex) in jobs.Select((job, index) => (job, index)))
        {
            workItems.AddRange(GetDailyWorkItems(job, jobIndex));
        }

        foreach (var period in workItems
                     .GroupBy(x => x.DailyRange.Day)
                     .OrderBy(x => x.Key))
        {
            await ExportDailyPeriodAsync(
                runId,
                period.OrderBy(x => x.JobIndex).ToArray(),
                cancellationToken);
        }
    }

    private IReadOnlyList<DailyExportWorkItem> GetDailyWorkItems(
        SyncJob job,
        int jobIndex)
    {
        var entityKey = job.Metadata.Key;
        var isFullExport = IsFullExport(job);
        if (job.Metadata.Watermark == null && !isFullExport)
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
            return [];
        }

        _logger.LogInformation(
            "Starting filesystem {DataMode} {TransferMode} export for {EntityKey} across {DayCount} day partitions.",
            isFullExport ? "full" : "differential",
            job.TransferMode,
            entityKey,
            dailyRanges.Count);

        return dailyRanges
            .Select((dailyRange, index) => new DailyExportWorkItem(
                jobIndex,
                index,
                job,
                entityKey,
                isFullExport,
                dailyRange))
            .ToArray();
    }

    private async Task ExportDailyPeriodAsync(
        string runId,
        IReadOnlyList<DailyExportWorkItem> workItems,
        CancellationToken cancellationToken)
    {
        var asyncRequests = new List<AsyncDailyExportRequest>();
        foreach (var workItem in workItems)
        {
            RecordDailyWorkStarted(runId, workItem);
            if (workItem.Job.TransferMode == SyncTransferMode.AsyncExport)
            {
                var descriptor = await CreateAsyncDailyExportRequestAsync(workItem, cancellationToken);
                if (descriptor is not null)
                {
                    asyncRequests.Add(descriptor);
                }

                continue;
            }

            await ExportPagedDailyRangeAsync(
                runId,
                workItem.EntityKey,
                workItem.Job,
                workItem.DailyRange,
                cancellationToken);
        }

        if (asyncRequests.Count > 0)
        {
            await ExportAsyncDailyRequestsAsync(runId, asyncRequests, cancellationToken);
        }
    }

    private void RecordDailyWorkStarted(
        string runId,
        DailyExportWorkItem workItem)
    {
        _monitor.RecordEntityStarted(runId, workItem.EntityKey);
        _monitor.RecordEntityRange(
            runId,
            workItem.EntityKey,
            workItem.DailyRange.Range.Start,
            workItem.DailyRange.Range.End,
            workItem.Job.ExpectedCompletedRangeEndUtc,
            workItem.Job.LagPeriods);
        _logger.LogInformation(
            "Starting filesystem {DataMode} {TransferMode} export for {EntityKey} day {Day}.",
            workItem.IsFullExport ? "full" : "differential",
            workItem.Job.TransferMode,
            workItem.EntityKey,
            workItem.DailyRange.Day);
    }

    private async Task<AsyncDailyExportRequest?> CreateAsyncDailyExportRequestAsync(
        DailyExportWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var outputDay = GetOutputDayForDailyRequest(workItem.Job, workItem.DailyRange);
        var partNumber = outputDay == workItem.DailyRange.Day ? 0 : workItem.DailyRangeIndex;
        var descriptor = new AsyncDailyExportRequest(
            new AsyncExportRequest(
                workItem.DailyRangeIndex,
                workItem.Job,
                GetRequestMetadata(workItem.Job),
                workItem.DailyRange.Range),
            workItem.EntityKey,
            outputDay,
            partNumber);

        if (_fileExportOptions.ReplaceExisting ||
            !await _objectSink.ExistsAsync(
                    workItem.EntityKey,
                    descriptor.OutputDay,
                    descriptor.PartNumber,
                    cancellationToken))
        {
            return descriptor;
        }

        _logger.LogInformation(
            "Skipping filesystem async export request for {EntityKey} day {Day} part {PartNumber} because the output file already exists.",
            workItem.EntityKey,
            descriptor.OutputDay,
            descriptor.PartNumber);
        return null;
    }

    private async Task ExportAsyncDailyRequestsAsync(
        string runId,
        IReadOnlyList<AsyncDailyExportRequest> descriptors,
        CancellationToken cancellationToken)
    {
        var descriptorsByRequestKey = descriptors.ToDictionary(x => GetAsyncDailyRequestKey(x.Request));
        var requests = descriptors.Select(x => x.Request).ToArray();

        await foreach (var file in _asyncExportRowSource.PrepareUnorderedAsync(requests, cancellationToken))
        {
            var descriptor = descriptorsByRequestKey[GetAsyncDailyRequestKey(file.Request)];
            var path = await _objectSink.CopyAsync(
                descriptor.EntityKey,
                descriptor.OutputDay,
                file.Path,
                descriptor.PartNumber,
                cancellationToken);
            await _asyncExportRowSource.CompleteAsync(file, cancellationToken);
            _logger.LogInformation(
                "Saved filesystem async export for {EntityKey} day {Day} part {PartNumber} to {Path}.",
                descriptor.EntityKey,
                descriptor.OutputDay,
                descriptor.PartNumber,
                path);
            _monitor.RecordProgress(
                runId,
                descriptor.EntityKey,
                filesProcessed: 1);
        }
    }

    private string GetAsyncDailyRequestKey(AsyncExportRequest request)
    {
        return string.Join(
            "|",
            request.Job.Metadata.Key,
            request.Sequence.ToString(CultureInfo.InvariantCulture),
            request.RequestMetadata.Operations.FirstOrDefault()?.Path ?? "",
            request.Range.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            request.Range.End.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            request.Range.IncludeEndFilter.ToString(CultureInfo.InvariantCulture),
            request.Job.DataMode.ToString(),
            _fileExportOptions.Format,
            request.Job.IsInitial.ToString(CultureInfo.InvariantCulture));
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
            var path = await _objectSink.WriteAsync(
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

    private DateOnly GetOutputDayForDailyRequest(
        SyncJob job,
        DailyExportRange dailyRange)
    {
        return IsFullExport(job)
            ? dailyRange.Day
            : GetOutputDay(job.Metadata, dailyRange);
    }

    private DateOnly GetOutputDay(SyncJob job, JsonElement row)
    {
        var options = _fileExportOptions.GetEntityOptions(job.Metadata);
        if (IsFullExport(job) ||
            options.PartitionDate == FileExportPartitionDateMode.ExportRunDay)
        {
            return _exportDateProvider.UtcToday;
        }

        if (job.Metadata.Watermark == null)
        {
            throw new InvalidOperationException(
                $"Sync entity '{job.Metadata.Key}' cannot be partitioned from async export rows because it has no watermark metadata.");
        }

        var watermarkValue = GetRequiredProperty(row, job.Metadata.Watermark.Field);
        if (watermarkValue.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                watermarkValue.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidOperationException(
                $"Sync entity '{job.Metadata.Key}' async export row watermark '{job.Metadata.Watermark.Field}' is missing or is not a valid date/time value.");
        }

        return DateOnly.FromDateTime(timestamp.UtcDateTime);
    }

    private static JsonElement GetRequiredProperty(JsonElement row, string propertyPath)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Filesystem export rows must be JSON objects.");
        }

        var current = row;
        foreach (var segment in propertyPath.Split(
                     '.',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return default;
            }
        }

        return current;
    }

    private SwaggerSyncEntityMetadata GetRequestMetadata(SyncJob job)
    {
        return IsFullExport(job)
            ? job.Metadata with { Watermark = null }
            : job.Metadata;
    }

    private bool IsFullExport(SyncJob job)
    {
        var options = _fileExportOptions.GetEntityOptions(job.Metadata);
        return job.DataMode == SyncDataMode.Full || options.DataMode == FileExportDataMode.Full;
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

    private sealed record AsyncDailyExportRequest(
        AsyncExportRequest Request,
        string EntityKey,
        DateOnly OutputDay,
        int PartNumber);

    private sealed record DailyExportWorkItem(
        int JobIndex,
        int DailyRangeIndex,
        SyncJob Job,
        string EntityKey,
        bool IsFullExport,
        DailyExportRange DailyRange);
}
