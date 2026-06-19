using Sollatek.DataSync.Storage.Relational;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSchemaPlannerTests
{
    [Fact]
    public void Plan_StoresReferenceAsFlatColumnWhenTargetIsNotSelected()
    {
        var asset = AssetWithCustomerReference();

        var table = RelationalSchemaPlanner.Plan(asset, selectedEntityKeys: ["assets"]);

        Assert.Equal("assets", table.TableName);
        Assert.Contains(table.Columns, column =>
            column.Name == "owner_customer_id" &&
            column.Role == RelationalColumnRole.ReferenceFlatValue);
        Assert.Empty(table.ForeignKeys);
    }

    [Fact]
    public void Plan_AddsFlexibleForeignKeyWhenTargetIsSelected()
    {
        var asset = AssetWithCustomerReference();
        var customer = Customer();

        var table = RelationalSchemaPlanner.Plan(
            asset,
            selectedEntityKeys: ["assets", "customers"],
            knownEntities: new Dictionary<string, SwaggerSyncEntityMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [asset.Key] = asset,
                [customer.Key] = customer
            });

        var foreignKey = Assert.Single(table.ForeignKeys);
        Assert.Equal("owner_customer_id", foreignKey.ColumnName);
        Assert.Equal("customers", foreignKey.TargetEntity);
        Assert.Equal("customers", foreignKey.TargetTable);
        Assert.Equal("id", foreignKey.TargetColumn);
        Assert.True(foreignKey.IsNullable);
        Assert.Equal(RelationalForeignKeyDeleteBehavior.NoAction, foreignKey.OnDelete);

        Assert.Contains(table.Columns, column =>
            column.Name == "owner_customer_id" &&
            column.Role == RelationalColumnRole.ReferenceForeignKey);
    }

    [Fact]
    public void Plan_StoresSelfReferenceAsFlatColumnEvenWhenTargetIsSelected()
    {
        var customer = CustomerWithParentReference();

        var table = RelationalSchemaPlanner.Plan(
            customer,
            selectedEntityKeys: ["customers"],
            knownEntities: new Dictionary<string, SwaggerSyncEntityMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [customer.Key] = customer
            });

        Assert.Empty(table.ForeignKeys);
        Assert.Contains(table.Columns, column =>
            column.Name == "parent_customer_id" &&
            column.Role == RelationalColumnRole.ReferenceFlatValue);
    }

    [Fact]
    public void Plan_RejectsDuplicateStorageColumns()
    {
        var asset = AssetWithCustomerReference() with
        {
            PrimaryKey = ["ownerCustomer.id"]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelationalSchemaPlanner.Plan(asset, selectedEntityKeys: ["assets"]));

        Assert.Contains("Duplicate relational column 'owner_customer_id'", exception.Message);
        Assert.Contains("assets", exception.Message);
    }

    [Fact]
    public void Plan_AddsScalarFieldsAndSkipsPrimaryKeyDuplicates()
    {
        var table = RelationalSchemaPlanner.Plan(
            AssetWithCustomerReference() with
            {
                ScalarFields =
                [
                    new SwaggerSyncScalarFieldMetadata
                    {
                        Source = "id",
                        LocalColumn = "id",
                        Type = "string",
                        Format = "guid"
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
            },
            selectedEntityKeys: ["assets"]);

        Assert.Contains(table.Columns, column =>
            column.Name == "serial" &&
            column.Source == "serial" &&
            column.Role == RelationalColumnRole.Scalar);
        Assert.Contains(table.Columns, column =>
            column.Name == "is_deleted" &&
            column.Source == "isDeleted" &&
            column.Role == RelationalColumnRole.Scalar);
        Assert.Single(table.Columns, column => column.Name == "id");
    }

    [Fact]
    public void Plan_AddsWatermarkFieldWhenItIsNotAlreadyAColumn()
    {
        var table = RelationalSchemaPlanner.Plan(
            AssetWithCustomerReference() with
            {
                Watermark = new SwaggerSyncWatermarkMetadata
                {
                    Field = "modification.dateTime",
                    TieBreakers = ["id"]
                }
            },
            selectedEntityKeys: ["assets"]);

        Assert.Contains(table.Columns, column =>
            column.Name == "modification_date_time" &&
            column.Source == "modification.dateTime" &&
            column.Role == RelationalColumnRole.Scalar);
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
                    FlatFallbackColumns = ["owner_customer_name"]
                }
            ],
            DocumentNames = ["data-v1"]
        };
    }

    private static SwaggerSyncEntityMetadata Customer()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "customers",
            Table = "customers",
            OperationIds = ["Customers_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }

    private static SwaggerSyncEntityMetadata CustomerWithParentReference()
    {
        return Customer() with
        {
            References =
            [
                new SwaggerSyncReferenceMetadata
                {
                    Source = "parentCustomer.id",
                    LocalColumn = "parent_customer_id",
                    TargetEntity = "customers",
                    TargetKey = "id",
                    Nullability = "nullable",
                    Enforce = "whenTargetInSyncPlan",
                    OnDelete = "setNull",
                    FlatFallbackColumns = []
                }
            ]
        };
    }
}
