using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Relational;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalMetadataSyncRunnerTests
{
    [Theory]
    [InlineData(StorageProvider.SqlServer)]
    [InlineData(StorageProvider.Postgres)]
    [InlineData(StorageProvider.MySql)]
    public async Task RunAsync_FetchesPagesMapsRowsAndWritesBatches(StorageProvider provider)
    {
        var pagedClient = new RecordingPagedApiClient(
            Page("""[{ "id": 1, "ownerCustomer": { "id": "customer-1" } }]""", totalPages: 2),
            Page("""[{ "id": 2, "ownerCustomer": { "id": "customer-2" } }]""", totalPages: 2));
        var sink = new RecordingRelationalSyncSink();
        var monitor = new RecordingSyncMonitor();
        var runner = new RelationalMetadataSyncRunner(
            NullLogger<RelationalMetadataSyncRunner>.Instance,
            pagedClient,
            sink,
            new SyncOptions
            {
                StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                MaxPageSize = 1
            },
            new StorageOptions
            {
                Provider = provider
            },
            monitor);
        var job = new SyncJob(
            AssetMetadata(),
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            SyncTransferMode.PagedApi);

        await runner.RunAsync("run-1", [job], CancellationToken.None);

        Assert.Equal([0, 1], pagedClient.Requests.Select(x => x.Skip).ToArray());
        Assert.All(pagedClient.Requests, request => Assert.Equal(1, request.Top));
        var preparation = Assert.Single(sink.Preparations);
        Assert.Equal(provider, preparation.Manifest.Provider);
        Assert.Equal(["assets"], preparation.Tables.Select(x => x.EntityKey).ToArray());
        Assert.Equal(2, sink.Batches.Count);
        Assert.All(sink.Batches, batch => Assert.Equal("assets", batch.Table.TableName));
        Assert.Equal(1L, sink.Batches[0].Rows.Single().Values["id"]);
        Assert.Equal("customer-2", sink.Batches[1].Rows.Single().Values["owner_customer_id"]);
        Assert.Equal(2, monitor.Current.RecordsProcessed);
        Assert.Equal(2, monitor.Current.PagesProcessed);
    }

    [Fact]
    public async Task RunAsync_UsesUnfilteredPagedRequestsForFullDataMode()
    {
        var pagedClient = new RecordingPagedApiClient(
            Page("""[{ "id": 1, "ownerCustomer": { "id": "customer-1" } }]""", totalPages: 1));
        var sink = new RecordingRelationalSyncSink();
        var runner = new RelationalMetadataSyncRunner(
            NullLogger<RelationalMetadataSyncRunner>.Instance,
            pagedClient,
            sink,
            new SyncOptions
            {
                StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                MaxPageSize = 500
            },
            new StorageOptions
            {
                Provider = StorageProvider.SqlServer
            },
            new RecordingSyncMonitor());
        var job = new SyncJob(
            AssetMetadata(includeWatermark: true),
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            SyncTransferMode.PagedApi,
            SyncDataMode.Full,
            IsInitial: true);

        await runner.RunAsync("run-1", [job], CancellationToken.None);

        Assert.False(pagedClient.Requests.Single().HasWatermark);
    }

    [Fact]
    public async Task RunAsync_UsesOpenEndedWatermarkRangeForDifferentialPagedRequests()
    {
        var pagedClient = new RecordingPagedApiClient(
            Page("""[{ "id": 1, "ownerCustomer": { "id": "customer-1" }, "modification": { "dateTime": "2026-01-01T00:00:00Z" } }]""", totalPages: 1));
        var runner = new RelationalMetadataSyncRunner(
            NullLogger<RelationalMetadataSyncRunner>.Instance,
            pagedClient,
            new RecordingRelationalSyncSink(),
            new SyncOptions
            {
                StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                MaxPageSize = 500
            },
            new StorageOptions
            {
                Provider = StorageProvider.SqlServer
            },
            new RecordingSyncMonitor());
        var job = new SyncJob(
            AssetMetadata(includeWatermark: true),
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            SyncTransferMode.PagedApi);

        await runner.RunAsync("run-1", [job], CancellationToken.None);

        Assert.False(pagedClient.Requests.Single().IncludeEndFilter);
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

    private static SwaggerSyncEntityMetadata AssetMetadata(bool includeWatermark = false)
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            Table = "assets",
            OperationIds = ["Assets_GetAssets"],
            Operations =
            [
                new SwaggerSyncOperationMetadata
                {
                    OperationId = "Assets_GetAssets",
                    Method = "get",
                    Path = "/api/Assets",
                    DocumentName = "data-v1"
                }
            ],
            PrimaryKey = ["id"],
            Watermark = includeWatermark
                ? new SwaggerSyncWatermarkMetadata
                {
                    Field = "modification.dateTime",
                    TieBreakers = ["id"]
                }
                : null,
            References =
            [
                new SwaggerSyncReferenceMetadata
                {
                    Source = "ownerCustomer.id",
                    LocalColumn = "owner_customer_id",
                    TargetEntity = "customers",
                    TargetKey = "id",
                    Nullability = "nullable",
                    Enforce = "whenTargetInSyncPlan",
                    OnDelete = "setNull",
                    FlatFallbackColumns = []
                }
            ],
            DocumentNames = ["data-v1"]
        };
    }

    private sealed class RecordingPagedApiClient(params PagedApiPage[] pages) : IPagedApiClient
    {
        private readonly Queue<PagedApiPage> _pages = new(pages);

        public List<(string EntityKey, int Top, int Skip, bool HasWatermark, bool IncludeEndFilter)> Requests { get; } = [];

        public Task<PagedApiPage> GetPageAsync(
            SwaggerSyncEntityMetadata metadata,
            SyncDateRange range,
            int top,
            int skip,
            CancellationToken cancellationToken)
        {
            Requests.Add((metadata.Key, top, skip, metadata.Watermark != null, range.IncludeEndFilter));
            return Task.FromResult(_pages.Dequeue());
        }
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

    private sealed class RecordingRelationalSyncSink : IRelationalSyncSink
    {
        public List<(SchemaManifest Manifest, IReadOnlyList<RelationalTablePlan> Tables)> Preparations { get; } = [];

        public List<(RelationalTablePlan Table, IReadOnlyList<RelationalRow> Rows)> Batches { get; } = [];

        public Task PrepareAsync(
            SchemaManifest manifest,
            IReadOnlyList<RelationalTablePlan> tables,
            CancellationToken cancellationToken)
        {
            Preparations.Add((manifest, tables));
            return Task.CompletedTask;
        }

        public async Task WriteAsync(
            RelationalTablePlan table,
            IAsyncEnumerable<RelationalRow> rows,
            CancellationToken cancellationToken)
        {
            var batch = new List<RelationalRow>();
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                batch.Add(row);
            }

            Batches.Add((table, batch));
        }
    }
}
