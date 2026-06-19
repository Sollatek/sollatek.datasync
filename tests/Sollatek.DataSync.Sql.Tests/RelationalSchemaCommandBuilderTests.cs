using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSchemaCommandBuilderTests
{
    [Fact]
    public void BuildCreateTableIfMissing_UsesPostgresSyntax()
    {
        var command = RelationalSchemaCommandBuilder.BuildCreateTableIfMissing(
            StorageProvider.Postgres,
            AssetTable());

        Assert.Equal(
            """
            CREATE TABLE IF NOT EXISTS "assets" (
                "id" text NOT NULL,
                "owner_customer_id" text NULL,
                CONSTRAINT "pk_assets" PRIMARY KEY ("id")
            );
            """,
            command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void BuildCreateTableIfMissing_UsesMySqlSyntax()
    {
        var command = RelationalSchemaCommandBuilder.BuildCreateTableIfMissing(
            StorageProvider.MySql,
            AssetTable());

        Assert.Equal(
            """
            CREATE TABLE IF NOT EXISTS `assets` (
                `id` varchar(512) NOT NULL,
                `owner_customer_id` varchar(512) NULL,
                CONSTRAINT `pk_assets` PRIMARY KEY (`id`)
            );
            """,
            command.Sql);
    }

    [Fact]
    public void BuildCreateTableIfMissing_UsesSqlServerSyntax()
    {
        var command = RelationalSchemaCommandBuilder.BuildCreateTableIfMissing(
            StorageProvider.SqlServer,
            AssetTable());

        Assert.Equal(
            """
            IF OBJECT_ID(N'[assets]', N'U') IS NULL
            BEGIN
                CREATE TABLE [assets] (
                    [id] nvarchar(450) NOT NULL,
                    [owner_customer_id] nvarchar(450) NULL,
                    CONSTRAINT [pk_assets] PRIMARY KEY ([id])
                );
            END;
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
                new RelationalColumnPlan("owner_customer_id", "ownerCustomer.id", RelationalColumnRole.ReferenceFlatValue)
            ],
            ["id"],
            []);
    }
}
