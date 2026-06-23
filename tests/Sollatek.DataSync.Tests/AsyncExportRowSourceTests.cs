using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Parquet;
using Parquet.Schema;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Monitoring;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class AsyncExportRowSourceTests
{
    [Fact]
    public void HttpClientFactory_ResolvesRowSourceWithStateStoreAndMonitor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AsyncExportOptions());
        services.AddSingleton<IAsyncExportStateStore, RecordingAsyncExportStateStore>();
        services.AddSingleton<ISyncMonitor>(new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance));
        services.AddHttpClient<HttpAsyncExportRowSource>(client =>
            {
                client.BaseAddress = new Uri("https://api.test/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());

        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<HttpAsyncExportRowSource>();

        Assert.NotNull(source);
    }

    [Fact]
    public async Task PrepareAsync_SubmitsPollsDownloadsReadsRowsAndCleansState()
    {
        var directory = CreateTempDirectory();
        try
        {
            var exportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var downloadId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var parquet = await ParquetAsync();
            var handler = new RecordingHandler(
                _ => Json(HttpStatusCode.Accepted, $$"""
                    { "exportId": "{{exportId}}", "createdAtUtc": "2026-06-22T00:00:00Z" }
                    """),
                _ => Json(HttpStatusCode.OK, $$"""
                    { "exportId": "{{exportId}}", "status": "processing" }
                    """),
                _ => Json(HttpStatusCode.OK, $$"""
                    {
                      "exportId": "{{exportId}}",
                      "status": "succeeded",
                      "downloadId": "{{downloadId}}",
                      "expiresAtUtc": "2099-06-23T00:00:00Z"
                    }
                    """),
                _ => Binary(parquet, "application/vnd.apache.parquet"));
            var monitor = new LoggingSyncMonitor(NullLogger<LoggingSyncMonitor>.Instance);
            var source = new HttpAsyncExportRowSource(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://api.test/")
                },
                new AsyncExportOptions
                {
                    StatePath = directory,
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    MaxParallelRequests = 10
                },
                NullLogger<HttpAsyncExportRowSource>.Instance,
                monitor);
            var job = new SyncJob(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                SyncTransferMode.AsyncExport);
            var request = new AsyncExportRequest(0, job, job.Metadata, job.Range);

            var files = await source.PrepareAsync([request], CancellationToken.None);
            Assert.Equal(1, monitor.Current.AsyncExportsDownloaded);

            var rows = new List<System.Text.Json.JsonElement>();
            await foreach (var row in source.ReadRowsAsync(files.Single(), CancellationToken.None))
            {
                rows.Add(row.Clone());
            }

            Assert.Equal(1, monitor.Current.AsyncExportsProcessing);

            await source.CompleteAsync(files.Single(), CancellationToken.None);

            var createRequest = handler.Requests[0];
            Assert.Equal(HttpMethod.Get, createRequest.Method);
            Assert.Contains("api/Assets", createRequest.RequestUri!.PathAndQuery);
            Assert.Contains("%24export=Parquet", createRequest.RequestUri.PathAndQuery);
            Assert.Contains("%24exportAsync=true", createRequest.RequestUri.PathAndQuery);
            Assert.Contains("%24filter=", createRequest.RequestUri.PathAndQuery);
            Assert.Contains("%24top=-1", createRequest.RequestUri.PathAndQuery);
            Assert.Equal("customer-1", rows.Single().GetProperty("ownerCustomer").GetProperty("id").GetString());
            Assert.EndsWith(".parquet", files.Single().Path);
            Assert.False(File.Exists(files.Single().Path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            Assert.Equal(0, monitor.Current.AsyncExportsDownloaded);
            Assert.Equal(0, monitor.Current.AsyncExportsProcessing);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsNewRequestsWhenPreviousDurableStateIsActive()
    {
        var directory = CreateTempDirectory();
        try
        {
            await WriteStateAsync(directory, "previous-request", "polling", entityKey: "customers");
            var exportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var downloadId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var parquet = await ParquetAsync();
            var handler = new RecordingHandler(
                _ => Json(HttpStatusCode.Accepted, $$"""
                    { "exportId": "{{exportId}}", "createdAtUtc": "2026-06-22T00:00:00Z" }
                    """),
                _ => Json(HttpStatusCode.OK, $$"""
                    {
                      "exportId": "{{exportId}}",
                      "status": "succeeded",
                      "downloadId": "{{downloadId}}",
                      "expiresAtUtc": "2099-06-23T00:00:00Z"
                    }
                    """),
                _ => Binary(parquet, "application/vnd.apache.parquet"));
            var source = new HttpAsyncExportRowSource(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://api.test/")
                },
                new AsyncExportOptions
                {
                    StatePath = directory,
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    MaxParallelRequests = 10
                },
                NullLogger<HttpAsyncExportRowSource>.Instance);
            var job = new SyncJob(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                SyncTransferMode.AsyncExport);
            var request = new AsyncExportRequest(0, job, job.Metadata, job.Range);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.PrepareAsync([request], CancellationToken.None));

            Assert.Contains("previous unfinished", exception.Message);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetActiveRequestsAsync_ReadsInjectedDurableStateStore()
    {
        var directory = CreateTempDirectory();
        try
        {
            var stateStore = new RecordingAsyncExportStateStore();
            await WriteStateAsync(stateStore, "previous-request", "polling");
            var source = new HttpAsyncExportRowSource(
                new HttpClient(new RecordingHandler())
                {
                    BaseAddress = new Uri("https://api.test/")
                },
                new AsyncExportOptions
                {
                    StatePath = directory,
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    MaxParallelRequests = 10
                },
                NullLogger<HttpAsyncExportRowSource>.Instance,
                stateStore);

            var active = await source.GetActiveRequestsAsync(CancellationToken.None);

            var request = Assert.Single(active);
            Assert.Equal("assets", request.EntityKey);
            Assert.Equal("polling", request.Status);
            Assert.False(Directory.Exists(Path.Combine(directory, "state")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_ResumesPreviousDurableStateForSameEntity()
    {
        var directory = CreateTempDirectory();
        try
        {
            var exportId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var downloadId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var parquet = await ParquetAsync();
            await WriteStateAsync(directory, "previous-request", "polling");
            var handler = new RecordingHandler(
                _ => Json(HttpStatusCode.OK, $$"""
                    {
                      "exportId": "{{exportId}}",
                      "status": "succeeded",
                      "downloadId": "{{downloadId}}",
                      "expiresAtUtc": "2099-06-23T00:00:00Z"
                    }
                    """),
                _ => Binary(parquet, "application/vnd.apache.parquet"));
            var source = new HttpAsyncExportRowSource(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://api.test/")
                },
                new AsyncExportOptions
                {
                    StatePath = directory,
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    MaxParallelRequests = 10
                },
                NullLogger<HttpAsyncExportRowSource>.Instance);
            var job = new SyncJob(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero)),
                SyncTransferMode.AsyncExport);
            var request = new AsyncExportRequest(0, job, job.Metadata, job.Range);

            var files = await source.PrepareAsync([request], CancellationToken.None);
            var rows = new List<System.Text.Json.JsonElement>();
            await foreach (var row in source.ReadRowsAsync(files.Single(), CancellationToken.None))
            {
                rows.Add(row.Clone());
            }

            await source.CompleteAsync(files.Single(), CancellationToken.None);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("/api/exports/33333333-3333-3333-3333-333333333333", handler.Requests[0].RequestUri!.AbsolutePath);
            Assert.DoesNotContain("api/Assets", handler.Requests.Select(x => x.RequestUri!.PathAndQuery));
            Assert.Equal("customer-1", rows.Single().GetProperty("ownerCustomer").GetProperty("id").GetString());
            Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_LimitsNewSubmissionsByConfiguredWindow()
    {
        var directory = CreateTempDirectory();
        try
        {
            var firstExportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var secondExportId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var firstDownloadId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var secondDownloadId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var parquet = await ParquetAsync();
            var handler = new RecordingHandler(
                _ => Json(HttpStatusCode.Accepted, $$"""
                    { "exportId": "{{firstExportId}}", "createdAtUtc": "2026-06-22T00:00:00Z" }
                    """),
                _ => Json(HttpStatusCode.OK, $$"""
                    {
                      "exportId": "{{firstExportId}}",
                      "status": "succeeded",
                      "downloadId": "{{firstDownloadId}}",
                      "expiresAtUtc": "2099-06-23T00:00:00Z"
                    }
                    """),
                _ => Binary(parquet, "application/vnd.apache.parquet"),
                _ => Json(HttpStatusCode.Accepted, $$"""
                    { "exportId": "{{secondExportId}}", "createdAtUtc": "2026-06-22T00:00:00Z" }
                    """),
                _ => Json(HttpStatusCode.OK, $$"""
                    {
                      "exportId": "{{secondExportId}}",
                      "status": "succeeded",
                      "downloadId": "{{secondDownloadId}}",
                      "expiresAtUtc": "2099-06-23T00:00:00Z"
                    }
                    """),
                _ => Binary(parquet, "application/vnd.apache.parquet"));
            var source = new HttpAsyncExportRowSource(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://api.test/")
                },
                new AsyncExportOptions
                {
                    StatePath = directory,
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    MaxParallelRequests = 2,
                    MaxSubmissions = 1,
                    SubmissionWindow = TimeSpan.FromMilliseconds(75)
                },
                NullLogger<HttpAsyncExportRowSource>.Instance);
            var firstJob = new SyncJob(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                SyncTransferMode.AsyncExport);
            var secondJob = new SyncJob(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero)),
                SyncTransferMode.AsyncExport);

            var files = await source.PrepareAsync(
                [
                    new AsyncExportRequest(0, firstJob, firstJob.Metadata, firstJob.Range),
                    new AsyncExportRequest(1, secondJob, secondJob.Metadata, secondJob.Range)
                ],
                CancellationToken.None);

            foreach (var file in files)
            {
                await source.CompleteAsync(file, CancellationToken.None);
            }

            var createRequestIndexes = handler.Requests
                .Select((request, index) => new { Request = request, Index = index })
                .Where(x => x.Request.RequestUri!.PathAndQuery.Contains("%24exportAsync=true", StringComparison.Ordinal))
                .Select(x => x.Index)
                .ToArray();

            Assert.Equal(2, createRequestIndexes.Length);
            var gap = handler.RequestTimestamps[createRequestIndexes[1]] -
                      handler.RequestTimestamps[createRequestIndexes[0]];
            Assert.True(
                gap >= TimeSpan.FromMilliseconds(50),
                $"Expected submit gap to honor the configured window, but it was {gap}.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SwaggerSyncEntityMetadata AssetMetadata()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
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
            Watermark = new SwaggerSyncWatermarkMetadata
            {
                Field = "modification.dateTime",
                TieBreakers = ["id"]
            },
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

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage Binary(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private static async Task<byte[]> ParquetAsync()
    {
        var schema = new ParquetSchema(
            new DataField("id", typeof(int), isNullable: true, isArray: false, propertyName: null),
            new DataField("ownerCustomer_id", typeof(string), isNullable: true, isArray: false, propertyName: null),
            new DataField("modification_dateTime", typeof(DateTime), isNullable: true, isArray: false, propertyName: null));
        await using var stream = new MemoryStream();
        await using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using var rowGroup = writer.CreateRowGroup();
            var fields = schema.GetDataFields();
            await rowGroup.WriteAsync<int>(fields[0], new int?[] { 1 }.AsMemory());
            await rowGroup.WriteAsync(fields[1], new[] { "customer-1" });
            await rowGroup.WriteAsync<DateTime>(
                fields[2],
                new DateTime?[]
                {
                    new(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc)
                }.AsMemory());
        }

        return stream.ToArray();
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

    private static async Task WriteStateAsync(
        string directory,
        string key,
        string status,
        string entityKey = "assets")
    {
        var stateDirectory = Path.Combine(directory, "state");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(stateDirectory, $"{key}.json"),
            $$"""
              {
                "key": "{{key}}",
                "entityKey": "{{entityKey}}",
                "sequence": 0,
                "rangeStart": "2026-06-01T00:00:00+00:00",
                "rangeEnd": "2026-06-02T00:00:00+00:00",
                "status": "{{status}}",
                "exportId": "33333333-3333-3333-3333-333333333333",
                "downloadId": null,
                "filePath": null,
                "expiresAtUtc": null,
                "attempts": 1,
                "error": null,
                "updatedAtUtc": "2026-06-22T00:00:00+00:00"
              }
              """);
    }

    private static async Task WriteStateAsync(
        IAsyncExportStateStore stateStore,
        string key,
        string status,
        string entityKey = "assets")
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes($$"""
              {
                "key": "{{key}}",
                "entityKey": "{{entityKey}}",
                "sequence": 0,
                "rangeStart": "2026-06-01T00:00:00+00:00",
                "rangeEnd": "2026-06-02T00:00:00+00:00",
                "status": "{{status}}",
                "exportId": "33333333-3333-3333-3333-333333333333",
                "downloadId": null,
                "filePath": null,
                "expiresAtUtc": null,
                "attempts": 1,
                "error": null,
                "updatedAtUtc": "2026-06-22T00:00:00+00:00"
              }
              """));
        await stateStore.SaveAsync(key, stream, CancellationToken.None);
    }

    private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        private readonly object _lock = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<DateTimeOffset> RequestTimestamps { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Requests.Add(CloneRequest(request));
                RequestTimestamps.Add(DateTimeOffset.UtcNow);
                return Task.FromResult(_responses.Dequeue()(request));
            }
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            return new HttpRequestMessage(request.Method, request.RequestUri);
        }
    }

    private sealed class RecordingAsyncExportStateStore : IAsyncExportStateStore
    {
        private readonly Dictionary<string, byte[]> _documents = new(StringComparer.OrdinalIgnoreCase);

        public async IAsyncEnumerable<string> ListKeysAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var key in _documents.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return key;
            }

            await Task.CompletedTask;
        }

        public Task<Stream?> OpenReadAsync(
            string key,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream?>(
                _documents.TryGetValue(key, out var bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null);
        }

        public async Task SaveAsync(
            string key,
            Stream content,
            CancellationToken cancellationToken)
        {
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            _documents[key] = buffer.ToArray();
        }

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken)
        {
            _documents.Remove(key);
            return Task.CompletedTask;
        }
    }
}
