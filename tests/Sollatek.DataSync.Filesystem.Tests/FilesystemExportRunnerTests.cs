using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Export;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class FilesystemExportRunnerTests
{
    [Fact]
    public async Task RunAsync_ExportsEachUtcDayUsingPagedRequestsAndLocalParquet()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pagedClient = new RecordingPagedApiClient(
                Page("""[{ "id": 1, "serial": "A-001" }]""", totalPages: 1),
                Page("""[{ "id": 2, "serial": "A-002" }]""", totalPages: 1));
            var monitor = new RecordingSyncMonitor();
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                new ParquetFileExportSink(new FileExportOptions { RootPath = directory }),
                new FileExportOptions { RootPath = directory },
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                monitor);
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)));

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            Assert.Equal(2, pagedClient.RequestedRanges.Count);
            Assert.Equal(
                new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
                pagedClient.RequestedRanges[0].Start);
            Assert.Equal(
                new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero),
                pagedClient.RequestedRanges[0].End);
            Assert.Equal(
                new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero),
                pagedClient.RequestedRanges[1].Start);
            Assert.Equal(
                new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero),
                pagedClient.RequestedRanges[1].End);

            var firstDay = Path.Combine(directory, "assets", "year=2026", "month=06", "day=18", "part-000000.parquet");
            var secondDay = Path.Combine(directory, "assets", "year=2026", "month=06", "day=19", "part-000000.parquet");
            Assert.True(File.Exists(firstDay));
            Assert.True(File.Exists(secondDay));
            Assert.Equal(2, monitor.Current.RecordsProcessed);
            Assert.Equal(2, monitor.Current.PagesProcessed);
            Assert.Equal(2, monitor.Current.FilesProcessed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsEntityWithoutWatermark()
    {
        var directory = CreateTempDirectory();
        try
        {
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                new RecordingPagedApiClient(),
                new ParquetFileExportSink(new FileExportOptions { RootPath = directory }),
                new FileExportOptions { RootPath = directory },
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero)
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero)),
                includeWatermark: false);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                runner.RunAsync("run-1", [job], CancellationToken.None));

            Assert.Contains("watermark metadata", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WritesPagedApiRowsToDailyParquetFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pagedClient = new RecordingPagedApiClient(
                Page("""[{ "id": 1, "serial": "A-001", "nested": { "value": 42 } }]""", totalPages: 1));
            var monitor = new RecordingSyncMonitor();
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                new ParquetFileExportSink(new FileExportOptions { RootPath = directory }),
                new FileExportOptions { RootPath = directory },
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                monitor);
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.PagedApi);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var path = Path.Combine(directory, "assets", "year=2026", "month=06", "day=18", "part-000000.parquet");
            Assert.True(File.Exists(path));
            Assert.Equal([(500, 0)], pagedClient.Requests.Select(x => (x.Top, x.Skip)).ToArray());
            Assert.Equal(1, monitor.Current.RecordsProcessed);
            Assert.Equal(1, monitor.Current.PagesProcessed);
            Assert.Equal(1, monitor.Current.FilesProcessed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CopiesAsyncExportFilesForEachUtcDayWhenConfigured()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pagedClient = new RecordingPagedApiClient();
            var asyncExports = new RecordingAsyncExportRowSource(
                Rows("""
                    [
                      { "id": 1, "serial": "A-001", "modification": { "dateTime": "2026-06-18T12:00:00Z" } }
                    ]
                    """),
                Rows("""
                    [
                      { "id": 2, "serial": "A-002", "modification": { "dateTime": "2026-06-19T12:00:00Z" } }
                    ]
                    """));
            var monitor = new RecordingSyncMonitor();
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                asyncExports,
                new ParquetFileExportSink(new FileExportOptions { RootPath = directory }),
                new FileExportOptions { RootPath = directory },
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                monitor);
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.AsyncExport);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var firstDay = Path.Combine(directory, "assets", "year=2026", "month=06", "day=18", "part-000000.parquet");
            var secondDay = Path.Combine(directory, "assets", "year=2026", "month=06", "day=19", "part-000000.parquet");
            Assert.True(File.Exists(firstDay));
            Assert.True(File.Exists(secondDay));
            Assert.Empty(pagedClient.Requests);
            var requests = asyncExports.PreparedRequests.Single();
            Assert.Equal(2, requests.Count);
            Assert.Equal("assets", requests[0].Job.Metadata.Key);
            Assert.Equal(0, requests[0].Sequence);
            Assert.Equal(new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero), requests[0].Range.Start);
            Assert.Equal(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), requests[0].Range.End);
            Assert.Equal(1, requests[1].Sequence);
            Assert.Equal(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), requests[1].Range.Start);
            Assert.Equal(new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), requests[1].Range.End);
            Assert.Equal(2, asyncExports.CompletedFiles.Count);
            Assert.Equal(0, monitor.Current.RecordsProcessed);
            Assert.Equal(0, monitor.Current.PagesProcessed);
            Assert.Equal(2, monitor.Current.FilesProcessed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CopiesAsyncExportFilesThroughConfiguredObjectSink()
    {
        var directory = CreateTempDirectory();
        try
        {
            var asyncExports = new RecordingAsyncExportRowSource(
                Rows("""[{ "id": 1, "modification": { "dateTime": "2026-06-18T12:00:00Z" } }]"""));
            var sink = new RecordingFileExportObjectSink();
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                new RecordingPagedApiClient(),
                asyncExports,
                sink,
                new FileExportOptions { RootPath = directory },
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.AsyncExport);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var copy = Assert.Single(sink.Copies);
            Assert.Equal("assets", copy.EntityKey);
            Assert.Equal(new DateOnly(2026, 6, 18), copy.Day);
            Assert.Equal(0, copy.PartNumber);
            Assert.Single(asyncExports.CompletedFiles);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_CopiesAsyncExportFilesAsTheyCompleteOutOfOrder()
    {
        var directory = CreateTempDirectory();
        try
        {
            var asyncExports = new OutOfOrderAsyncExportRowSource();
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                new RecordingPagedApiClient(),
                asyncExports,
                new ParquetFileExportSink(new FileExportOptions { RootPath = directory }),
                new FileExportOptions { RootPath = directory },
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.AsyncExport);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            Assert.Equal([1, 0], asyncExports.CompletedFiles.Select(x => x.Request.Sequence).ToArray());
            Assert.True(File.Exists(Path.Combine(directory, "assets", "year=2026", "month=06", "day=18", "part-000000.parquet")));
            Assert.True(File.Exists(Path.Combine(directory, "assets", "year=2026", "month=06", "day=19", "part-000000.parquet")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_SkipsExistingDailyAsyncExportFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            var fileExportOptions = new FileExportOptions
            {
                RootPath = directory,
                FolderFormat = "yyyyMM",
                FileNameFormat = "{entity}_{date:yyyyMMdd}.{format}",
                Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["assets"] = new()
                    {
                        OutputName = "Assets"
                    }
                }
            };
            var existingPath = DailyExportPath.Build(
                fileExportOptions,
                "assets",
                new DateOnly(2026, 6, 18),
                partNumber: 0);
            Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
            await File.WriteAllTextAsync(existingPath, "existing");

            var asyncExports = new RecordingAsyncExportRowSource(
                Rows("""[{ "id": 1, "modification": { "dateTime": "2026-06-18T12:00:00Z" } }]"""),
                Rows("""[{ "id": 2, "modification": { "dateTime": "2026-06-19T12:00:00Z" } }]"""));
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                new RecordingPagedApiClient(),
                asyncExports,
                new ParquetFileExportSink(fileExportOptions),
                fileExportOptions,
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.AsyncExport);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var request = Assert.Single(asyncExports.PreparedRequests.Single());
            Assert.Equal(1, request.Sequence);
            Assert.Equal(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), request.Range.Start);
            Assert.Equal("existing", await File.ReadAllTextAsync(existingPath));
            Assert.True(File.Exists(Path.Combine(directory, "202606", "Assets_20260619.parquet")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WritesPagedApiRowsToExportRunDayWhenConfigured()
    {
        var directory = CreateTempDirectory();
        try
        {
            var fileExportOptions = new FileExportOptions
            {
                RootPath = directory,
                Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rawDataLocationdata"] = new()
                    {
                        PartitionDate = FileExportPartitionDateMode.ExportRunDay
                    }
                }
            };
            var pagedClient = new RecordingPagedApiClient(
                Page("""[{ "id": 1, "eventDate": "2026-06-12T09:30:00Z" }]""", totalPages: 1));
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                new ParquetFileExportSink(fileExportOptions),
                fileExportOptions,
                new FixedExportDateProvider(new DateOnly(2026, 6, 15)),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.PagedApi,
                entityKey: "rawDataLocationdata");

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var exportRunDayPath = Path.Combine(
                directory,
                "rawDataLocationdata",
                "year=2026",
                "month=06",
                "day=15",
                "part-000000.parquet");
            var watermarkDayPath = Path.Combine(
                directory,
                "rawDataLocationdata",
                "year=2026",
                "month=06",
                "day=12",
                "part-000000.parquet");
            Assert.True(File.Exists(exportRunDayPath));
            Assert.False(File.Exists(watermarkDayPath));
            Assert.Equal(
                new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero),
                pagedClient.RequestedRanges.Single().Start);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_UsesSyncPlanPolicyAliasForFilesystemExport()
    {
        var directory = CreateTempDirectory();
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileExport:rootPath"] = directory,
                    ["SyncPlan:0:rawdata/locationdata:dataMode"] = "differential",
                    ["SyncPlan:0:rawdata/locationdata:partitionDate"] = "exportRunDay"
                })
                .Build();
            var fileExportOptions = FileExportOptions.FromConfiguration(configuration);
            var pagedClient = new RecordingPagedApiClient(
                Page("""[{ "id": 1, "eventDate": "2026-06-12T09:30:00Z" }]""", totalPages: 1));
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                new ParquetFileExportSink(fileExportOptions),
                fileExportOptions,
                new FixedExportDateProvider(new DateOnly(2026, 6, 15)),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.PagedApi,
                entityKey: "rawDataLocationdata");

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var exportRunDayPath = Path.Combine(
                directory,
                "rawDataLocationdata",
                "year=2026",
                "month=06",
                "day=15",
                "part-000000.parquet");
            Assert.True(File.Exists(exportRunDayPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_UsesUnfilteredPagedRequestsForFullDataMode()
    {
        var directory = CreateTempDirectory();
        try
        {
            var fileExportOptions = new FileExportOptions
            {
                RootPath = directory,
                Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["assets"] = new()
                    {
                        DataMode = FileExportDataMode.Full
                    }
                }
            };
            var pagedClient = new RecordingPagedApiClient(
                Page("""[{ "id": 1, "serial": "A-001" }]""", totalPages: 1));
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                new ParquetFileExportSink(fileExportOptions),
                fileExportOptions,
                new SystemExportDateProvider(),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.PagedApi);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            Assert.False(pagedClient.ReceivedWatermarkMetadata.Single());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_UsesUnfilteredPagedRequestsForInitialFullJob()
    {
        var directory = CreateTempDirectory();
        try
        {
            var fileExportOptions = new FileExportOptions { RootPath = directory };
            var pagedClient = new RecordingPagedApiClient(
                Page("""[{ "id": 1, "serial": "A-001" }]""", totalPages: 1),
                Page("""[{ "id": 1, "serial": "A-001" }]""", totalPages: 1));
            var runner = new FilesystemExportRunner(
                NullLogger<FilesystemExportRunner>.Instance,
                pagedClient,
                new ParquetFileExportSink(fileExportOptions),
                fileExportOptions,
                new FixedExportDateProvider(new DateOnly(2026, 6, 20)),
                new SyncOptions
                {
                    StartFrom = new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    MaxPageSize = 500
                },
                new RecordingSyncMonitor());
            var job = CreateJob(
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
                transferMode: SyncTransferMode.PagedApi,
                dataMode: SyncDataMode.Full,
                isInitial: true);

            await runner.RunAsync("run-1", [job], CancellationToken.None);

            var firstRangeDayPath = Path.Combine(directory, "assets", "year=2026", "month=06", "day=18", "part-000000.parquet");
            var secondRangeDayPath = Path.Combine(directory, "assets", "year=2026", "month=06", "day=19", "part-000000.parquet");
            Assert.True(File.Exists(firstRangeDayPath));
            Assert.True(File.Exists(secondRangeDayPath));
            Assert.Equal(2, pagedClient.Requests.Count);
            Assert.All(pagedClient.ReceivedWatermarkMetadata, Assert.False);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SyncJob CreateJob(
        SyncDateRange range,
        bool includeWatermark = true,
        SyncTransferMode transferMode = SyncTransferMode.PagedApi,
        string entityKey = "assets",
        SyncDataMode dataMode = SyncDataMode.Differential,
        bool isInitial = false)
    {
        return new SyncJob(
            new SwaggerSyncEntityMetadata
            {
                Key = entityKey,
                OperationIds = ["Assets_Get"],
                PrimaryKey = ["id"],
                Watermark = includeWatermark
                    ? new SwaggerSyncWatermarkMetadata
                    {
                        Field = "modification.dateTime",
                        TieBreakers = ["id"]
                    }
                    : null,
                References = [],
                DocumentNames = ["data-v1"]
            },
            range,
            transferMode,
            dataMode,
            isInitial);
    }

    private static PagedApiPage Page(string json, int totalPages)
    {
        var rows = Rows(json);
        return new PagedApiPage(rows, TotalCount: rows.Count, totalPages);
    }

    private static IReadOnlyList<JsonElement> Rows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sollatek-datasync-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingSyncMonitor : ISyncMonitor
    {
        public SyncRunStatus Current { get; private set; } = SyncRunStatus.Idle;

        public void RecordRunStarted(string runId, DateTimeOffset? startedAt = null)
        {
            Current = Current with { RunId = runId, State = SyncRunState.Running };
        }

        public void RecordEntityStarted(string runId, string entityKey, DateTimeOffset? startedAt = null)
        {
            Current = Current with
            {
                RunId = runId,
                CurrentEntity = entityKey,
                State = SyncRunState.ProcessingEntity
            };
        }

        public void RecordProgress(
            string runId,
            string entityKey,
            long recordsProcessed = 0,
            long pagesProcessed = 0,
            long filesProcessed = 0)
        {
            Current = Current with
            {
                RunId = runId,
                CurrentEntity = entityKey,
                RecordsProcessed = Current.RecordsProcessed + recordsProcessed,
                PagesProcessed = Current.PagesProcessed + pagesProcessed,
                FilesProcessed = Current.FilesProcessed + filesProcessed
            };
        }

        public void RecordFailure(
            string runId,
            string? entityKey,
            string error,
            int tryNumber,
            DateTimeOffset? nextRetryAt,
            DateTimeOffset? failedAt = null)
        {
            Current = Current with
            {
                RunId = runId,
                CurrentEntity = entityKey,
                State = SyncRunState.Failed,
                LastError = error,
                TryNumber = tryNumber,
                NextRetryAt = nextRetryAt
            };
        }

        public void RecordSuccess(string runId, DateTimeOffset finishedAt)
        {
            Current = Current with
            {
                RunId = runId,
                State = SyncRunState.Succeeded,
                LastSuccessAt = finishedAt
            };
        }
    }

    private sealed class RecordingPagedApiClient(params PagedApiPage[] pages) : IPagedApiClient
    {
        private readonly Queue<PagedApiPage> _pages = new(pages);

        public List<(int Top, int Skip)> Requests { get; } = [];

        public List<SyncDateRange> RequestedRanges { get; } = [];

        public List<bool> ReceivedWatermarkMetadata { get; } = [];

        public Task<PagedApiPage> GetPageAsync(
            SwaggerSyncEntityMetadata metadata,
            SyncDateRange range,
            int top,
            int skip,
            CancellationToken cancellationToken)
        {
            Requests.Add((top, skip));
            RequestedRanges.Add(range);
            ReceivedWatermarkMetadata.Add(metadata.Watermark != null);
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class RecordingAsyncExportRowSource(params IReadOnlyList<JsonElement>[] rowSets) : IAsyncExportRowSource
    {
        private readonly IReadOnlyList<JsonElement>[] _rowSets = rowSets;

        public List<IReadOnlyList<AsyncExportRequest>> PreparedRequests { get; } = [];

        public List<AsyncExportDownloadedFile> CompletedFiles { get; } = [];

        public Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
            IReadOnlyList<AsyncExportRequest> requests,
            CancellationToken cancellationToken)
        {
            PreparedRequests.Add(requests);
            return Task.FromResult<IReadOnlyList<AsyncExportDownloadedFile>>(
                requests.Select(request =>
                {
                    var directory = Path.Combine(
                        Path.GetTempPath(),
                        "sollatek-datasync-tests",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, $"file-{request.Sequence}.parquet");
                    File.WriteAllText(path, $"file-{request.Sequence}");
                    return new AsyncExportDownloadedFile(request, path);
                }).ToArray());
        }

        public async IAsyncEnumerable<JsonElement> ReadRowsAsync(
            AsyncExportDownloadedFile file,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var row in _rowSets[file.Request.Sequence])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row.Clone();
            }

            await Task.CompletedTask;
        }

        public Task CompleteAsync(
            AsyncExportDownloadedFile file,
            CancellationToken cancellationToken)
        {
            CompletedFiles.Add(file);
            if (File.Exists(file.Path))
            {
                File.Delete(file.Path);
            }

            var directory = Path.GetDirectoryName(file.Path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFileExportObjectSink : IFileExportObjectSink
    {
        public List<(string EntityKey, DateOnly Day, string SourcePath, int PartNumber)> Copies { get; } = [];

        public Task<string?> WriteAsync(
            string entityKey,
            DateOnly day,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            int partNumber,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>($"memory://{entityKey}/{day:yyyyMMdd}/{partNumber}");
        }

        public Task<string> CopyAsync(
            string entityKey,
            DateOnly day,
            string sourcePath,
            int partNumber,
            CancellationToken cancellationToken)
        {
            Copies.Add((entityKey, day, sourcePath, partNumber));
            return Task.FromResult($"memory://{entityKey}/{day:yyyyMMdd}/{partNumber}");
        }

        public Task<bool> ExistsAsync(
            string entityKey,
            DateOnly day,
            int partNumber,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class OutOfOrderAsyncExportRowSource : IAsyncExportRowSource
    {
        public List<AsyncExportDownloadedFile> CompletedFiles { get; } = [];

        public Task<IReadOnlyList<AsyncExportDownloadedFile>> PrepareAsync(
            IReadOnlyList<AsyncExportRequest> requests,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AsyncExportDownloadedFile>>(
                requests
                    .OrderByDescending(x => x.Sequence)
                    .Select(CreateFile)
                    .ToArray());
        }

        public async IAsyncEnumerable<AsyncExportDownloadedFile> PrepareUnorderedAsync(
            IReadOnlyList<AsyncExportRequest> requests,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var request in requests.OrderByDescending(x => x.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateFile(request);
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<JsonElement> ReadRowsAsync(
            AsyncExportDownloadedFile file,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task CompleteAsync(
            AsyncExportDownloadedFile file,
            CancellationToken cancellationToken)
        {
            CompletedFiles.Add(file);
            if (File.Exists(file.Path))
            {
                File.Delete(file.Path);
            }

            var directory = Path.GetDirectoryName(file.Path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return Task.CompletedTask;
        }

        private static AsyncExportDownloadedFile CreateFile(AsyncExportRequest request)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "sollatek-datasync-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"file-{request.Sequence}.parquet");
            File.WriteAllText(path, $"file-{request.Sequence}");
            return new AsyncExportDownloadedFile(request, path);
        }
    }

    private sealed class FixedExportDateProvider(DateOnly utcToday) : IExportDateProvider
    {
        public DateOnly UtcToday { get; } = utcToday;
    }
}
