using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalForeignKeyCommandBuilderTests
{
    [Theory]
    [InlineData(StorageProvider.Postgres, "pg_constraint")]
    [InlineData(StorageProvider.MySql, "information_schema.TABLE_CONSTRAINTS")]
    [InlineData(StorageProvider.SqlServer, "sys.foreign_keys")]
    public void BuildExists_QueriesProviderCatalog(StorageProvider provider, string expectedCatalog)
    {
        var command = RelationalForeignKeyCommandBuilder.BuildExists(provider, AssetTable(), AssetCustomerForeignKey());

        Assert.Contains(expectedCatalog, command.Sql);
        Assert.Contains("fk_assets_owner_customer_id", command.Parameters.Select(x => x.Value));
    }

    [Fact]
    public void BuildAdd_UsesPostgresForeignKeySyntax()
    {
        var command = RelationalForeignKeyCommandBuilder.BuildAdd(
            StorageProvider.Postgres,
            AssetTable(),
            AssetCustomerForeignKey());

        Assert.Equal(
            """
            ALTER TABLE "assets"
            ADD CONSTRAINT "fk_assets_owner_customer_id"
            FOREIGN KEY ("owner_customer_id")
            REFERENCES "customers" ("id")
            ON DELETE NO ACTION;
            """,
            command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void BuildAdd_UsesMySqlForeignKeySyntax()
    {
        var command = RelationalForeignKeyCommandBuilder.BuildAdd(
            StorageProvider.MySql,
            AssetTable(),
            AssetCustomerForeignKey());

        Assert.Equal(
            """
            ALTER TABLE `assets`
            ADD CONSTRAINT `fk_assets_owner_customer_id`
            FOREIGN KEY (`owner_customer_id`)
            REFERENCES `customers` (`id`)
            ON DELETE NO ACTION;
            """,
            command.Sql);
    }

    [Fact]
    public void BuildAdd_UsesSqlServerForeignKeySyntaxWithoutValidatingExistingRows()
    {
        var command = RelationalForeignKeyCommandBuilder.BuildAdd(
            StorageProvider.SqlServer,
            AssetTable(),
            AssetCustomerForeignKey());

        Assert.Equal(
            """
            ALTER TABLE [assets] WITH NOCHECK
            ADD CONSTRAINT [fk_assets_owner_customer_id]
            FOREIGN KEY ([owner_customer_id])
            REFERENCES [customers] ([id])
            ON DELETE NO ACTION;
            """,
            command.Sql);
    }

    private static RelationalTablePlan AssetTable()
    {
        return new RelationalTablePlan(
            "assets",
            "assets",
            [
                new RelationalColumnPlan("id", "id", RelationalColumnRole.PrimaryKey),
                new RelationalColumnPlan("owner_customer_id", "ownerCustomer.id", RelationalColumnRole.ReferenceForeignKey)
            ],
            ["id"],
            [AssetCustomerForeignKey()]);
    }

    private static RelationalForeignKeyPlan AssetCustomerForeignKey()
    {
        return new RelationalForeignKeyPlan(
            "owner_customer_id",
            "customers",
            "customers",
            "id",
            IsNullable: true,
            RelationalForeignKeyDeleteBehavior.NoAction);
    }
}
