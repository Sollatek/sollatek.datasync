using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync;
using Sollatek.DataSync.Sync.Contract;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SchemaAwareSyncPlanLoaderTests
{
    [Fact]
    public async Task LoadAsync_UsesAcceptedContractWhenSwaggerCannotLoad()
    {
        var accepted = SyncContractSnapshot.Create("1", [AssetEntity()]);
        var store = new RecordingSyncContractStore { Snapshot = accepted };
        using var httpClient = new HttpClient(new StatusHandler(HttpStatusCode.BadGateway));

        var result = await SchemaAwareSyncPlanLoader.LoadAsync(
            httpClient,
            Configuration(),
            new SchemaContractOptions(),
            store,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(result.UsedLastKnownGood);
        Assert.Null(result.PendingAcceptance);
        Assert.Equal("assets", Assert.Single(result.Plan.MetadataEntities).Key);
    }

    [Fact]
    public async Task LoadAsync_RequiresVersionChangeForBreakingTypeChange()
    {
        var store = new RecordingSyncContractStore();
        using var initialClient = new HttpClient(new JsonHandler(Swagger("int64")));
        var initial = await SchemaAwareSyncPlanLoader.LoadAsync(
            initialClient,
            Configuration(),
            new SchemaContractOptions { Version = "1" },
            store,
            NullLogger.Instance,
            CancellationToken.None);
        store.Snapshot = Assert.IsType<SyncContractSnapshot>(initial.PendingAcceptance);

        using var changedClient = new HttpClient(new JsonHandler(Swagger("uint64")));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SchemaAwareSyncPlanLoader.LoadAsync(
                changedClient,
                Configuration(),
                new SchemaContractOptions { Version = "1" },
                store,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("Schema:version is still '1'", exception.Message);
        Assert.Contains("integer/int64", exception.Message);
        Assert.Contains("integer/uint64", exception.Message);

        using var approvedClient = new HttpClient(new JsonHandler(Swagger("uint64")));
        var approved = await SchemaAwareSyncPlanLoader.LoadAsync(
            approvedClient,
            Configuration(),
            new SchemaContractOptions { Version = "2" },
            store,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.False(approved.UsedLastKnownGood);
        Assert.Equal("2", Assert.IsType<SyncContractSnapshot>(approved.PendingAcceptance).SchemaVersion);
    }

    [Fact]
    public async Task LoadAsync_DoesNotHideInvalidLocalSyncPlanBehindAcceptedContract()
    {
        var store = new RecordingSyncContractStore
        {
            Snapshot = SyncContractSnapshot.Create("1", [AssetEntity()])
        };
        using var client = new HttpClient(new JsonHandler(Swagger("int64")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SchemaAwareSyncPlanLoader.LoadAsync(
                client,
                Configuration("missingEntity"),
                new SchemaContractOptions(),
                store,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("missingEntity", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_DoesNotFallbackWhenVersionChangedOrPlanDiffers()
    {
        var store = new RecordingSyncContractStore
        {
            Snapshot = SyncContractSnapshot.Create("1", [AssetEntity()])
        };
        using var versionClient = new HttpClient(
            new StatusHandler(HttpStatusCode.BadGateway));

        var versionException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SchemaAwareSyncPlanLoader.LoadAsync(
                versionClient,
                Configuration(),
                new SchemaContractOptions { Version = "2" },
                store,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("Schema:version changed", versionException.Message);

        using var planClient = new HttpClient(
            new StatusHandler(HttpStatusCode.BadGateway));
        var planException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SchemaAwareSyncPlanLoader.LoadAsync(
                planClient,
                Configuration("missingEntity"),
                new SchemaContractOptions { Version = "1" },
                store,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("configured SyncPlan cannot be resolved", planException.Message);
    }

    private static IConfiguration Configuration(string entity = "assets")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SwaggerDocuments:0:name"] = "data-v1",
                ["SwaggerDocuments:0:url"] =
                    "https://api.sollatek.io/swagger/data-v1/swagger.json",
                ["SyncPlan:0"] = entity
            })
            .Build();
    }

    private static string Swagger(string readingFormat)
    {
        return $$"""
        {
          "openapi": "3.0.1",
          "paths": {},
          "components": {
            "schemas": {
              "Asset": {
                "type": "object",
                "properties": {
                  "id": { "type": "string", "format": "uuid" },
                  "reading": {
                    "type": "integer",
                    "format": "{{readingFormat}}",
                    "nullable": true
                  }
                }
              }
            }
          },
          "x-sollatek-sync": {
            "version": 1,
            "entities": {
              "assets": {
                "schema": "#/components/schemas/Asset",
                "operationId": "Assets_Get",
                "table": "assets",
                "collection": "assets",
                "primaryKey": ["id"]
              }
            }
          }
        }
        """;
    }

    private static SwaggerSyncEntityMetadata AssetEntity()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            OperationIds = ["Assets_Get"],
            Operations = [],
            Table = "assets",
            Collection = "assets",
            PrimaryKey = ["id"],
            ScalarFields = [],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }

    private sealed class RecordingSyncContractStore : ISyncContractStore
    {
        public SyncContractSnapshot? Snapshot { get; set; }

        public Task<SyncContractSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Snapshot);
        }

        public Task SaveAsync(
            SyncContractSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json)
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request
            });
        }
    }
}
