#nullable enable

using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalSchemaManifestCommandBuilder
{
    public const string TableName = "__sollatek_datasync_schema_manifest";

    private static readonly string[] Columns =
    [
        "id",
        "version",
        "provider",
        "sync_plan_hash",
        "schema_hash",
        "applied_at_utc"
    ];

    public static RelationalCommand BuildCreateTableIfMissing(StorageProvider provider)
    {
        EnsureRelationalProvider(provider);

        var sql = provider switch
        {
            StorageProvider.Postgres => BuildPostgresCreateTable(),
            StorageProvider.MySql => BuildMySqlCreateTable(),
            StorageProvider.SqlServer => BuildSqlServerCreateTable(),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, []);
    }

    public static RelationalCommand BuildIsCurrent(
        StorageProvider provider,
        SchemaManifest manifest)
    {
        EnsureRelationalProvider(provider);
        ArgumentNullException.ThrowIfNull(manifest);

        var quotedTable = RelationalSqlDialect.Quote(provider, TableName);
        var conditions = string.Join(
            $" AND{RelationalSqlDialect.NewLine}    ",
            Columns
                .Take(5)
                .Select((column, index) =>
                    $"{RelationalSqlDialect.Quote(provider, column)} = {Placeholder(provider, index)}"));

        var sql = string.Join(
            RelationalSqlDialect.NewLine,
            $"SELECT COUNT(1) FROM {quotedTable}",
            "WHERE",
            $"    {conditions};");

        return new RelationalCommand(sql, BuildManifestParameters(provider, manifest, includeAppliedAt: false));
    }

    public static RelationalCommand BuildSave(
        StorageProvider provider,
        SchemaManifest manifest)
    {
        EnsureRelationalProvider(provider);
        ArgumentNullException.ThrowIfNull(manifest);

        var sql = provider switch
        {
            StorageProvider.Postgres => BuildPostgresSave(),
            StorageProvider.MySql => BuildMySqlSave(),
            StorageProvider.SqlServer => BuildSqlServerSave(),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, BuildManifestParameters(provider, manifest, includeAppliedAt: true));
    }

    private static string BuildPostgresCreateTable()
    {
        return BuildCreateTableIfMissingStatement(StorageProvider.Postgres);
    }

    private static string BuildMySqlCreateTable()
    {
        return BuildCreateTableIfMissingStatement(StorageProvider.MySql);
    }

    private static string BuildSqlServerCreateTable()
    {
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"IF OBJECT_ID(N'{RelationalSqlDialect.Quote(StorageProvider.SqlServer, TableName)}', N'U') IS NULL",
            "BEGIN",
            $"    CREATE TABLE {RelationalSqlDialect.Quote(StorageProvider.SqlServer, TableName)} (",
            string.Join($",{RelationalSqlDialect.NewLine}", BuildCreateColumnLines(StorageProvider.SqlServer, "        ")),
            "    );",
            "END;");
    }

    private static string BuildCreateTableIfMissingStatement(StorageProvider provider)
    {
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"CREATE TABLE IF NOT EXISTS {RelationalSqlDialect.Quote(provider, TableName)} (",
            string.Join($",{RelationalSqlDialect.NewLine}", BuildCreateColumnLines(provider, "    ")),
            ");");
    }

    private static IReadOnlyList<string> BuildCreateColumnLines(
        StorageProvider provider,
        string indent)
    {
        var textType = RelationalSqlDialect.FlexibleTextColumnType(provider);
        var lines = Columns
            .Select(column => $"{indent}{RelationalSqlDialect.Quote(provider, column)} {textType} NOT NULL")
            .ToList();

        lines.Add(
            $"{indent}CONSTRAINT {RelationalSqlDialect.Quote(provider, $"pk_{TableName}")} PRIMARY KEY ({RelationalSqlDialect.Quote(provider, "id")})");

        return lines;
    }

    private static string BuildPostgresSave()
    {
        var provider = StorageProvider.Postgres;
        var assignments = Columns
            .Skip(1)
            .Select(column =>
                $"{RelationalSqlDialect.Quote(provider, column)} = EXCLUDED.{RelationalSqlDialect.Quote(provider, column)}");

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(provider, TableName)} ({JoinColumns(provider)})",
            $"VALUES ({JoinPlaceholders(provider)})",
            $"ON CONFLICT ({RelationalSqlDialect.Quote(provider, "id")}) DO UPDATE SET",
            $"    {string.Join($",{RelationalSqlDialect.NewLine}    ", assignments)};");
    }

    private static string BuildMySqlSave()
    {
        var provider = StorageProvider.MySql;
        var assignments = Columns
            .Skip(1)
            .Select((column, index) =>
                $"{RelationalSqlDialect.Quote(provider, column)} = {Placeholder(provider, index + 1)}");

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"INSERT INTO {RelationalSqlDialect.Quote(provider, TableName)} ({JoinColumns(provider)})",
            $"VALUES ({JoinPlaceholders(provider)})",
            "ON DUPLICATE KEY UPDATE",
            $"    {string.Join($",{RelationalSqlDialect.NewLine}    ", assignments)};");
    }

    private static string BuildSqlServerSave()
    {
        var provider = StorageProvider.SqlServer;
        var selectColumns = Columns
            .Select((column, index) =>
                $"{Placeholder(provider, index)} AS {RelationalSqlDialect.Quote(provider, column)}");
        var updateColumns = Columns
            .Skip(1)
            .Select(column =>
                $"target.{RelationalSqlDialect.Quote(provider, column)} = source.{RelationalSqlDialect.Quote(provider, column)}");

        return string.Join(
            RelationalSqlDialect.NewLine,
            $"MERGE {RelationalSqlDialect.Quote(provider, TableName)} AS target",
            $"USING (SELECT {string.Join(", ", selectColumns)}) AS source",
            $"ON target.{RelationalSqlDialect.Quote(provider, "id")} = source.{RelationalSqlDialect.Quote(provider, "id")}",
            "WHEN MATCHED THEN",
            $"    UPDATE SET {string.Join(", ", updateColumns)}",
            "WHEN NOT MATCHED THEN",
            $"    INSERT ({JoinColumns(provider)})",
            $"    VALUES ({JoinSourceColumns(provider)});");
    }

    private static IReadOnlyList<RelationalCommandParameter> BuildManifestParameters(
        StorageProvider provider,
        SchemaManifest manifest,
        bool includeAppliedAt)
    {
        var values = new List<object?>
        {
            "current",
            manifest.Version.ToString(CultureInfo.InvariantCulture),
            manifest.Provider.ToString(),
            manifest.SyncPlanHash,
            manifest.SchemaHash
        };

        if (includeAppliedAt)
        {
            values.Add(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        return values
            .Select((value, index) => new RelationalCommandParameter(
                $"p{index.ToString(CultureInfo.InvariantCulture)}",
                Placeholder(provider, index),
                value))
            .ToArray();
    }

    private static string JoinColumns(StorageProvider provider)
    {
        return string.Join(", ", Columns.Select(column => RelationalSqlDialect.Quote(provider, column)));
    }

    private static string JoinPlaceholders(StorageProvider provider)
    {
        return string.Join(", ", Columns.Select((_, index) => Placeholder(provider, index)));
    }

    private static string JoinSourceColumns(StorageProvider provider)
    {
        return string.Join(
            ", ",
            Columns.Select(column => $"source.{RelationalSqlDialect.Quote(provider, column)}"));
    }

    private static string Placeholder(StorageProvider provider, int index)
    {
        return provider switch
        {
            StorageProvider.Postgres => $"${index + 1}",
            StorageProvider.MySql or StorageProvider.SqlServer => $"@p{index.ToString(CultureInfo.InvariantCulture)}",
            _ => throw new InvalidOperationException($"Storage provider '{provider}' is not a relational provider.")
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
