using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalUpsertCommandBuilderTests
{
    [Fact]
    public void BuildUpsert_UsesPostgresOnConflictSyntax()
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(
            StorageProvider.Postgres,
            AssetTable(),
            AssetRow());

        Assert.Equal(
            """
            INSERT INTO "assets" ("id", "owner_customer_id")
            VALUES ($1, $2)
            ON CONFLICT ("id") DO UPDATE SET "owner_customer_id" = EXCLUDED."owner_customer_id";
            """,
            command.Sql);
        Assert.Equal(["p1", "p2"], command.Parameters.Select(x => x.Name).ToArray());
        Assert.Equal(["42", "customer-1"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void BuildUpsert_UsesMySqlOnDuplicateKeySyntax()
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(
            StorageProvider.MySql,
            AssetTable(),
            AssetRow());

        Assert.Equal(
            """
            INSERT INTO `assets` (`id`, `owner_customer_id`)
            VALUES (@p0, @p1)
            ON DUPLICATE KEY UPDATE `owner_customer_id` = VALUES(`owner_customer_id`);
            """,
            command.Sql);
        Assert.Equal(["p0", "p1"], command.Parameters.Select(x => x.Name).ToArray());
        Assert.Equal(["42", "customer-1"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void BuildUpsert_UsesSqlServerMergeSyntax()
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(
            StorageProvider.SqlServer,
            AssetTable(),
            AssetRow());

        Assert.Equal(
            """
            MERGE INTO [assets] AS target
            USING (SELECT @p0 AS [id], @p1 AS [owner_customer_id]) AS source
            ON target.[id] = source.[id]
            WHEN MATCHED THEN UPDATE SET target.[owner_customer_id] = source.[owner_customer_id]
            WHEN NOT MATCHED THEN INSERT ([id], [owner_customer_id]) VALUES (source.[id], source.[owner_customer_id]);
            """,
            command.Sql);
        Assert.Equal(["p0", "p1"], command.Parameters.Select(x => x.Name).ToArray());
        Assert.Equal(["42", "customer-1"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void BuildUpsert_BatchesPostgresRows()
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(
            StorageProvider.Postgres,
            AssetTable(),
            [AssetRow(), AssetRow("43", "customer-2")]);

        Assert.Equal(
            """
            INSERT INTO "assets" ("id", "owner_customer_id")
            VALUES ($1, $2), ($3, $4)
            ON CONFLICT ("id") DO UPDATE SET "owner_customer_id" = EXCLUDED."owner_customer_id";
            """,
            command.Sql);
        Assert.Equal(["p1", "p2", "p3", "p4"], command.Parameters.Select(x => x.Name).ToArray());
        Assert.Equal(["42", "customer-1", "43", "customer-2"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void BuildUpsert_BatchesMySqlRows()
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(
            StorageProvider.MySql,
            AssetTable(),
            [AssetRow(), AssetRow("43", "customer-2")]);

        Assert.Equal(
            """
            INSERT INTO `assets` (`id`, `owner_customer_id`)
            VALUES (@p0, @p1), (@p2, @p3)
            ON DUPLICATE KEY UPDATE `owner_customer_id` = VALUES(`owner_customer_id`);
            """,
            command.Sql);
        Assert.Equal(["p0", "p1", "p2", "p3"], command.Parameters.Select(x => x.Name).ToArray());
        Assert.Equal(["42", "customer-1", "43", "customer-2"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void BuildUpsert_BatchesSqlServerRows()
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(
            StorageProvider.SqlServer,
            AssetTable(),
            [AssetRow(), AssetRow("43", "customer-2")]);

        Assert.Equal(
            """
            MERGE INTO [assets] AS target
            USING (VALUES (@p0, @p1), (@p2, @p3)) AS source ([id], [owner_customer_id])
            ON target.[id] = source.[id]
            WHEN MATCHED THEN UPDATE SET target.[owner_customer_id] = source.[owner_customer_id]
            WHEN NOT MATCHED THEN INSERT ([id], [owner_customer_id]) VALUES (source.[id], source.[owner_customer_id]);
            """,
            command.Sql);
        Assert.Equal(["p0", "p1", "p2", "p3"], command.Parameters.Select(x => x.Name).ToArray());
        Assert.Equal(["42", "customer-1", "43", "customer-2"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void BuildUpsert_DoesNothingOnConflictWhenTableOnlyHasPrimaryKeyColumns()
    {
        var table = new RelationalTablePlan(
            "customers",
            "customers",
            [new RelationalColumnPlan("id", "id", RelationalColumnRole.PrimaryKey)],
            ["id"],
            []);
        var row = new RelationalRow(
            "customers",
            "customers",
            new Dictionary<string, object?> { ["id"] = 42L });

        var command = RelationalUpsertCommandBuilder.BuildUpsert(StorageProvider.Postgres, table, row);

        Assert.Equal(
            """
            INSERT INTO "customers" ("id")
            VALUES ($1)
            ON CONFLICT ("id") DO NOTHING;
            """,
            command.Sql);
    }

    [Fact]
    public void BuildUpsert_NormalizesParameterValuesForFlexibleTextColumns()
    {
        var row = new RelationalRow(
            "assets",
            "assets",
            new Dictionary<string, object?>
            {
                ["id"] = 42L,
                ["owner_customer_id"] = null
            });

        var command = RelationalUpsertCommandBuilder.BuildUpsert(StorageProvider.Postgres, AssetTable(), row);

        Assert.Equal("42", command.Parameters[0].Value);
        Assert.Null(command.Parameters[1].Value);
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

    private static RelationalRow AssetRow()
    {
        return AssetRow(42L, "customer-1");
    }

    private static RelationalRow AssetRow(object id, string ownerCustomerId)
    {
        return new RelationalRow(
            "assets",
            "assets",
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["owner_customer_id"] = ownerCustomerId
            });
    }
}
