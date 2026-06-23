using System.Net;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Fetch;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class PagedApiClientTests
{
    [Fact]
    public async Task GetPageAsync_SendsSwaggerDerivedRequestAndReadsRowsAndPagination()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{ "id": 42, "name": "Asset 1" }]""")
            });
        handler.Response.Headers.Add(
            "x-pagination",
            """{ "totalCount": 1200, "pageSize": 500, "currentPage": 1, "totalPages": 3 }""");
        var client = new PagedApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.test")
        });

        var page = await client.GetPageAsync(
            AssetMetadata(),
            new SyncDateRange(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            top: 500,
            skip: 0,
            CancellationToken.None);

        Assert.Equal(
            "/api/Assets?$top=500&$skip=0&$orderby=id%20asc",
            handler.Requests.Single().RequestUri?.PathAndQuery);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(1200, page.TotalCount);
        Assert.Equal(42, page.Rows.Single().GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task GetPageAsync_ThrowsWhenResponseIsNotSuccessful()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Bad filter")
            });
        var client = new PagedApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.test")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetPageAsync(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
                top: 500,
                skip: 0,
                CancellationToken.None));

        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("Bad filter", exception.Message);
    }

    [Fact]
    public async Task GetPageAsync_HonorsHttpClientTimeoutWhileReadingContent()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BlockingContent()
            });
        var client = new PagedApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.test"),
            Timeout = TimeSpan.FromMilliseconds(100)
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetPageAsync(
                AssetMetadata(),
                new SyncDateRange(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
                top: 500,
                skip: 0,
                CancellationToken.None));
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
            References = [],
            DocumentNames = ["data-v1"]
        };
    }

    private sealed class RecordingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; } = response;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Response);
        }
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            return Task.Delay(TimeSpan.FromSeconds(5));
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            return Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
