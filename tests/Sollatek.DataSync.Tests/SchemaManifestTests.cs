using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class SchemaManifestTests
{
    [Fact]
    public void Create_ChangesPlanHashWhenEntityOrderChanges()
    {
        var ordered = SchemaManifest.Create(
            StorageProvider.Postgres,
            [AssetWithCustomerReference(), Customer()]);
        var reversed = SchemaManifest.Create(
            StorageProvider.Postgres,
            [Customer(), AssetWithCustomerReference()]);

        Assert.NotEqual(ordered.SyncPlanHash, reversed.SyncPlanHash);
        Assert.Equal(ordered.SchemaHash, reversed.SchemaHash);
    }

    [Fact]
    public void Create_ChangesSchemaHashWhenReferenceTargetSelectionChanges()
    {
        var flatOnly = SchemaManifest.Create(
            StorageProvider.Postgres,
            [AssetWithCustomerReference()]);
        var withForeignKey = SchemaManifest.Create(
            StorageProvider.Postgres,
            [AssetWithCustomerReference(), Customer()]);

        Assert.NotEqual(flatOnly.SchemaHash, withForeignKey.SchemaHash);
    }

    [Fact]
    public void Create_ChangesSchemaHashWhenProviderChanges()
    {
        var postgres = SchemaManifest.Create(
            StorageProvider.Postgres,
            [AssetWithCustomerReference(), Customer()]);
        var sqlServer = SchemaManifest.Create(
            StorageProvider.SqlServer,
            [AssetWithCustomerReference(), Customer()]);

        Assert.NotEqual(postgres.SchemaHash, sqlServer.SchemaHash);
    }

    [Fact]
    public void Create_ChangesSchemaHashWhenMetadataChanges()
    {
        var baseline = SchemaManifest.Create(
            StorageProvider.Postgres,
            [AssetWithCustomerReference(), Customer()]);
        var renamedTable = SchemaManifest.Create(
            StorageProvider.Postgres,
            [AssetWithCustomerReference() with { Table = "asset_records" }, Customer()]);

        Assert.NotEqual(baseline.SchemaHash, renamedTable.SchemaHash);
    }

    private static SwaggerSyncEntityMetadata AssetWithCustomerReference()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            Table = "assets",
            OperationIds = ["Assets_Get"],
            PrimaryKey = ["id"],
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
}
