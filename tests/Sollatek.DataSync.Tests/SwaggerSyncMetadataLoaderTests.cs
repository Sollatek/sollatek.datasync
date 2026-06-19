using System.Net;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SwaggerSyncMetadataLoaderTests
{
    [Fact]
    public async Task LoadAsync_FetchesAllConfiguredDocumentsAndBuildsRegistry()
    {
        var handler = new RecordingHandler(
            new Dictionary<string, string>
            {
                ["https://api.sollatek.io/swagger/data-v1/swagger.json"] = DataSwagger,
                ["https://api.sollatek.io/swagger/portal-v1/swagger.json"] = PortalSwagger
            });
        using var httpClient = new HttpClient(handler);

        var registry = await SwaggerSyncMetadataLoader.LoadAsync(
            httpClient,
            [
                new SwaggerSyncDocumentOptions("data-v1", "https://api.sollatek.io/swagger/data-v1/swagger.json"),
                new SwaggerSyncDocumentOptions("portal-v1", "https://api.sollatek.io/swagger/portal-v1/swagger.json")
            ],
            CancellationToken.None);

        Assert.Equal(
            [
                "https://api.sollatek.io/swagger/data-v1/swagger.json",
                "https://api.sollatek.io/swagger/portal-v1/swagger.json"
            ],
            handler.Requests.Select(x => x.ToString()));
        Assert.Empty(registry.ValidateReferences());
        Assert.True(registry.ContainsEntity("assets"));
        Assert.True(registry.ContainsEntity("firmwares"));
    }

    [Fact]
    public async Task LoadAsync_ThrowsClearErrorForFailedSwaggerRequest()
    {
        var handler = new RecordingHandler(
            new Dictionary<string, string>
            {
                ["https://api.sollatek.io/swagger/data-v1/swagger.json"] = DataSwagger
            });
        using var httpClient = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SwaggerSyncMetadataLoader.LoadAsync(
                httpClient,
                [new SwaggerSyncDocumentOptions("portal-v1", "https://api.sollatek.io/swagger/portal-v1/swagger.json")],
                CancellationToken.None));

        Assert.Contains("Failed to load swagger document 'portal-v1'", exception.Message);
        Assert.Contains("404", exception.Message);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public RecordingHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            if (!_responses.TryGetValue(request.RequestUri!.ToString(), out var response))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response)
            });
        }
    }

    private const string DataSwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "assets": {
            "schema": "#/components/schemas/AssetDto",
            "operationId": "Assets_GetAssets",
            "operationIds": ["Assets_GetAssets"],
            "table": "assets",
            "primaryKey": ["id"],
            "references": [
              {
                "source": "firmware.id",
                "localColumn": "firmware_id",
                "targetEntity": "firmwares",
                "targetKey": "id"
              }
            ]
          }
        }
      }
    }
    """;

    private const string PortalSwagger = """
    {
      "openapi": "3.0.1",
      "x-sollatek-sync": {
        "version": 1,
        "entities": {
          "firmwares": {
            "schema": "#/components/schemas/FirmwareDto",
            "operationId": "Firmwares_GetFirmwares",
            "operationIds": ["Firmwares_GetFirmwares"],
            "table": "firmwares",
            "primaryKey": ["id"],
            "references": []
          }
        }
      }
    }
    """;
}
