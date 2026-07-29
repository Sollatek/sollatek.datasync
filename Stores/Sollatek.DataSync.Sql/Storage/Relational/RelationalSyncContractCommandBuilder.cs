#nullable enable

using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalSyncContractCommandBuilder
{
    public const string TableName = "__sollatek_datasync_contract";

    public static RelationalCommand BuildCreateTableIfMissing(StorageProvider provider)
    {
        EnsureRelationalProvider(provider);

        var id = RelationalSqlDialect.Quote(provider, "id");
        var snapshot = RelationalSqlDialect.Quote(provider, "snapshot_json");
        var table = RelationalSqlDialect.Quote(provider, TableName);
        var idType = RelationalSqlDialect.FlexibleTextColumnType(provider);
        var snapshotType = RelationalSqlDialect.LargeTextColumnType(provider);
        var primaryKey = RelationalSqlDialect.Quote(provider, $"pk_{TableName}");

        var sql = provider == StorageProvider.SqlServer
            ? string.Join(
                RelationalSqlDialect.NewLine,
                $"IF OBJECT_ID(N'{table}', N'U') IS NULL",
                "BEGIN",
                $"    CREATE TABLE {table} (",
                $"        {id} {idType} NOT NULL,",
                $"        {snapshot} {snapshotType} NOT NULL,",
                $"        CONSTRAINT {primaryKey} PRIMARY KEY ({id})",
                "    );",
                "END;")
            : string.Join(
                RelationalSqlDialect.NewLine,
                $"CREATE TABLE IF NOT EXISTS {table} (",
                $"    {id} {idType} NOT NULL,",
                $"    {snapshot} {snapshotType} NOT NULL,",
                $"    CONSTRAINT {primaryKey} PRIMARY KEY ({id})",
                ");");

        return new RelationalCommand(sql, []);
    }

    public static RelationalCommand BuildLoad(StorageProvider provider)
    {
        EnsureRelationalProvider(provider);

        var sql = string.Join(
            RelationalSqlDialect.NewLine,
            $"SELECT {RelationalSqlDialect.Quote(provider, "snapshot_json")}",
            $"FROM {RelationalSqlDialect.Quote(provider, TableName)}",
            $"WHERE {RelationalSqlDialect.Quote(provider, "id")} = {Placeholder(provider, 0)};");

        return new RelationalCommand(
            sql,
            [Parameter(provider, 0, "accepted")]);
    }

    public static RelationalCommand BuildSave(
        StorageProvider provider,
        string snapshotJson)
    {
        EnsureRelationalProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);

        var sql = provider switch
        {
            StorageProvider.Postgres => BuildPostgresSave(),
            StorageProvider.MySql => BuildMySqlSave(),
            StorageProvider.SqlServer => BuildSqlServerSave(),
            _ => throw new InvalidOperationException(
                $"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(
            sql,
            [
                Parameter(provider, 0, "accepted"),
                Parameter(provider, 1, snapshotJson)
            ]);
    }

    private static string BuildPostgresSave()
    {
        var provider = StorageProvider.Postgres;
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(provider, TableName)} ({RelationalSqlDialect.Quote(provider, "id")}, {RelationalSqlDialect.Quote(provider, "snapshot_json")})",
            $"VALUES ({Placeholder(provider, 0)}, {Placeholder(provider, 1)})",
            $"ON CONFLICT ({RelationalSqlDialect.Quote(provider, "id")}) DO UPDATE SET",
            $"    {RelationalSqlDialect.Quote(provider, "snapshot_json")} = EXCLUDED.{RelationalSqlDialect.Quote(provider, "snapshot_json")};");
    }

    private static string BuildMySqlSave()
    {
        var provider = StorageProvider.MySql;
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(provider, TableName)} ({RelationalSqlDialect.Quote(provider, "id")}, {RelationalSqlDialect.Quote(provider, "snapshot_json")})",
            $"VALUES ({Placeholder(provider, 0)}, {Placeholder(provider, 1)})",
            "ON DUPLICATE KEY UPDATE",
            $"    {RelationalSqlDialect.Quote(provider, "snapshot_json")} = {Placeholder(provider, 1)};");
    }

    private static string BuildSqlServerSave()
    {
        var provider = StorageProvider.SqlServer;
        var id = RelationalSqlDialect.Quote(provider, "id");
        var snapshot = RelationalSqlDialect.Quote(provider, "snapshot_json");
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"MERGE {RelationalSqlDialect.Quote(provider, TableName)} AS target",
            $"USING (SELECT {Placeholder(provider, 0)} AS {id}, {Placeholder(provider, 1)} AS {snapshot}) AS source",
            $"ON target.{id} = source.{id}",
            "WHEN MATCHED THEN",
            $"    UPDATE SET target.{snapshot} = source.{snapshot}",
            "WHEN NOT MATCHED THEN",
            $"    INSERT ({id}, {snapshot})",
            $"    VALUES (source.{id}, source.{snapshot});");
    }

    private static RelationalCommandParameter Parameter(
        StorageProvider provider,
        int index,
        object value)
    {
        return new RelationalCommandParameter(
            $"p{index.ToString(CultureInfo.InvariantCulture)}",
            Placeholder(provider, index),
            value);
    }

    private static string Placeholder(StorageProvider provider, int index)
    {
        return provider switch
        {
            StorageProvider.Postgres => $"${index + 1}",
            StorageProvider.MySql or StorageProvider.SqlServer =>
                $"@p{index.ToString(CultureInfo.InvariantCulture)}",
            _ => throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.")
        };
    }

    private static void EnsureRelationalProvider(StorageProvider provider)
    {
        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }
    }
}
