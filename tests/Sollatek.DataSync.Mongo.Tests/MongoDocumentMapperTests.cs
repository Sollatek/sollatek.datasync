using System.Text.Json;
using MongoDB.Bson;
using Sollatek.DataSync.Storage.Document;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class MongoDocumentMapperTests
{
    [Fact]
    public void Map_PreservesJsonDocumentAndSetsIdFromPrimaryKey()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 42,
          "ownerCustomer": {
            "id": "customer-1"
          }
        }
        """);

        var bson = MongoDocumentMapper.Map(AssetMetadata(), document.RootElement);

        Assert.Equal("42", bson["_id"].AsString);
        Assert.Equal(42, bson["id"].AsInt32);
        Assert.Equal("customer-1", bson["ownerCustomer"].AsBsonDocument["id"].AsString);
    }

    [Fact]
    public void Map_RejectsMissingPrimaryKey()
    {
        using var document = JsonDocument.Parse("""{ "name": "Asset 1" }""");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MongoDocumentMapper.Map(AssetMetadata(), document.RootElement));

        Assert.Contains("assets", exception.Message);
        Assert.Contains("id", exception.Message);
    }

    private static SwaggerSyncEntityMetadata AssetMetadata()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            OperationIds = ["Assets_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }
}
