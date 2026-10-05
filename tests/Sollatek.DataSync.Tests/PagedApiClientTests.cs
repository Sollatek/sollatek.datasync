using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
    public async Task GetPageAsync_PreservesMultilingualUtf8AndNestedDynamicFields()
    {
        const string json =
            """[{"id":42,"name":"Ελλάδα العربية 日本語 México Україна 😀🚪🌡️","extraFields":{"city":"Αθήνα","labels":["دبي","東京","Côte d’Ivoire"]}}]""";
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
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

        var row = Assert.Single(page.Rows);
        Assert.Equal("Ελλάδα العربية 日本語 México Україна 😀🚪🌡️", row.GetProperty("name").GetString());
        var extraFields = row.GetProperty("extraFields");
        Assert.Equal("Αθήνα", extraFields.GetProperty("city").GetString());
        Assert.Equal(
            ["دبي", "東京", "Côte d’Ivoire"],
            extraFields.GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task GetPageAsync_PreservesLegacyNonUtf8ResponseCompatibility()
    {
        const string json = """[{"id":42,"name":"Ελλάδα 日本語"}]""";
        var content = new ByteArrayContent(Encoding.Unicode.GetBytes(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-16"
        };
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
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

        Assert.Equal("Ελλάδα 日本語", Assert.Single(page.Rows).GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPageAsync_PreservesUtf8BomCompatibility()
    {
        const string json = """[{"id":42,"name":"Ελλάδα 日本語 😀"}]""";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var payload = new byte[Encoding.UTF8.Preamble.Length + jsonBytes.Length];
        Encoding.UTF8.Preamble.CopyTo(payload);
        jsonBytes.CopyTo(payload.AsSpan(Encoding.UTF8.Preamble.Length));
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
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

        Assert.Equal("Ελλάδα 日本語 😀", Assert.Single(page.Rows).GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPageAsync_PreservesReplacementBehaviorForInvalidUtf8()
    {
        var content = new ByteArrayContent([0x5B, 0x22, 0xC3, 0x28, 0x22, 0x5D]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
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
            Encoding.UTF8.GetString([0xC3, 0x28]),
            Assert.Single(page.Rows).GetString());
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
