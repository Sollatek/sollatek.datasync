using System.Net;
using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync;

namespace Sollatek.DataSync.Tests;

public sealed class SwaggerBackedSyncPlanLoaderTests
{
    [Fact]
    public async Task LoadAsync_FetchesConfiguredSwaggerDocumentsAndBuildsExecutablePlan()
    {
        var handler = new RecordingHandler(
            new Dictionary<string, string>
            {
                ["https://api.sollatek.io/swagger/data-v1/swagger.json"] = DataSwagger,
                ["https://api.sollatek.io/swagger/portal-v1/swagger.json"] = PortalSwagger
            });
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "data-v1",
                ["SwaggerDocuments:0:url"] = "https://api.sollatek.io/swagger/data-v1/swagger.json",
                ["SwaggerDocuments:1:name"] = "portal-v1",
                ["SwaggerDocuments:1:url"] = "https://api.sollatek.io/swagger/portal-v1/swagger.json",
                ["SyncPlan:0"] = "rawDataLocationdata",
                ["SyncPlan:1"] = "assets"
            })
            .Build();

        var plan = await SwaggerBackedSyncPlanLoader.LoadAsync(
            httpClient,
            configuration,
            CancellationToken.None);

        Assert.Equal(
            [
                "https://api.sollatek.io/swagger/data-v1/swagger.json",
                "https://api.sollatek.io/swagger/portal-v1/swagger.json"
            ],
            handler.Requests.Select(x => x.ToString()));
        Assert.Equal(["rawDataLocationdata", "assets"], plan.MetadataEntities.Select(x => x.Key));
    }

    [Fact]
    public async Task LoadAsync_AllowsMetadataOnlyPlanWhenRequested()
    {
        var handler = new RecordingHandler(
            new Dictionary<string, string>
            {
                ["https://api.sollatek.io/swagger/portal-v1/swagger.json"] = PortalSwagger
            });
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "portal-v1",
                ["SwaggerDocuments:0:url"] = "https://api.sollatek.io/swagger/portal-v1/swagger.json",
                ["SyncPlan:0"] = "firmwares"
            })
            .Build();

        var plan = await SwaggerBackedSyncPlanLoader.LoadAsync(
            httpClient,
            configuration,
            CancellationToken.None);

        Assert.Equal(["firmwares"], plan.MetadataEntities.Select(x => x.Key));
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
          "assets": { "operationId": "Assets_Get", "primaryKey": ["id"] },
          "rawDataLocationdata": { "operationId": "RawData_GetLocationData", "primaryKey": ["id"] }
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
          "firmwares": { "operationId": "Firmwares_Get", "primaryKey": ["id"] }
        }
      }
    }
    """;
}
