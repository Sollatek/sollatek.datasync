using System.Text.Json;
using Sollatek.DataSync.Storage.Relational;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalRowMapperTests
{
    [Fact]
    public void Map_ExtractsPrimaryKeyAndReferenceColumnsFromJsonPaths()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 42,
          "serial": "A-100",
          "isDeleted": false,
          "ownerCustomer": {
            "id": "customer-1"
          }
        }
        """);
        var table = RelationalSchemaPlanner.Plan(AssetWithCustomerReferenceAndScalars(), selectedEntityKeys: ["assets"]);

        var row = RelationalRowMapper.Map(table, document.RootElement);

        Assert.Equal("assets", row.TableName);
        Assert.Equal(42L, row.Values["id"]);
        Assert.Equal("A-100", row.Values["serial"]);
        Assert.Equal(false, row.Values["is_deleted"]);
        Assert.Equal("customer-1", row.Values["owner_customer_id"]);
    }

    [Fact]
    public void Map_UsesNullWhenAPlannedJsonPathIsMissing()
    {
        using var document = JsonDocument.Parse("""{ "id": 42 }""");
        var table = RelationalSchemaPlanner.Plan(AssetWithCustomerReference(), selectedEntityKeys: ["assets"]);

        var row = RelationalRowMapper.Map(table, document.RootElement);

        Assert.Null(row.Values["owner_customer_id"]);
    }

    [Fact]
    public void Map_UsesFlatColumnNameFallbackForFlatRows()
    {
        using var document = JsonDocument.Parse("""
        {
          "ID": 42,
          "SERIAL": "A-100",
          "IS_DELETED": false,
          "OWNER_CUSTOMER_ID": "customer-1"
        }
        """);
        var table = RelationalSchemaPlanner.Plan(AssetWithCustomerReferenceAndScalars(), selectedEntityKeys: ["assets"]);

        var row = RelationalRowMapper.Map(table, document.RootElement);

        Assert.Equal(42L, row.Values["id"]);
        Assert.Equal("A-100", row.Values["serial"]);
        Assert.Equal(false, row.Values["is_deleted"]);
        Assert.Equal("customer-1", row.Values["owner_customer_id"]);
    }

    [Fact]
    public void Map_RejectsObjectValuesForScalarRelationalColumns()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": {
            "value": 42
          }
        }
        """);
        var table = RelationalSchemaPlanner.Plan(AssetWithCustomerReference(), selectedEntityKeys: ["assets"]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelationalRowMapper.Map(table, document.RootElement));

        Assert.Contains("Column 'id'", exception.Message);
        Assert.Contains("JSON path 'id'", exception.Message);
        Assert.Contains("scalar value", exception.Message);
    }

    private static SwaggerSyncEntityMetadata AssetWithCustomerReference()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            Table = "assets",
            OperationIds = ["Assets_Get"],
            PrimaryKey = ["id"],
            ScalarFields = [],
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

    private static SwaggerSyncEntityMetadata AssetWithCustomerReferenceAndScalars()
    {
        return AssetWithCustomerReference() with
        {
            ScalarFields =
            [
                new SwaggerSyncScalarFieldMetadata
                {
                    Source = "id",
                    LocalColumn = "id",
                    Type = "integer",
                    Format = "int64"
                },
                new SwaggerSyncScalarFieldMetadata
                {
                    Source = "serial",
                    LocalColumn = "serial",
                    Type = "string"
                },
                new SwaggerSyncScalarFieldMetadata
                {
                    Source = "isDeleted",
                    LocalColumn = "is_deleted",
                    Type = "boolean"
                }
            ]
        };
    }
}
