#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalSchemaCommandBuilder
{
    public static RelationalCommand BuildCreateTableIfMissing(
        StorageProvider provider,
        RelationalTablePlan table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }

        var sql = provider switch
        {
            StorageProvider.Postgres => BuildPostgres(table),
            StorageProvider.MySql => BuildMySql(table),
            StorageProvider.SqlServer => BuildSqlServer(table),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, []);
    }

    private static string BuildPostgres(RelationalTablePlan table)
    {
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"CREATE TABLE IF NOT EXISTS {RelationalSqlDialect.Quote(StorageProvider.Postgres, table.TableName)} (",
            string.Join($",{RelationalSqlDialect.NewLine}", BuildColumnAndConstraintLines(StorageProvider.Postgres, table)),
            ");");
    }

    private static string BuildMySql(RelationalTablePlan table)
    {
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"CREATE TABLE IF NOT EXISTS {RelationalSqlDialect.Quote(StorageProvider.MySql, table.TableName)} (",
            string.Join($",{RelationalSqlDialect.NewLine}", BuildColumnAndConstraintLines(StorageProvider.MySql, table)),
            ");");
    }

    private static string BuildSqlServer(RelationalTablePlan table)
    {
        return string.Join(
            RelationalSqlDialect.NewLine,
            $"IF OBJECT_ID(N'{RelationalSqlDialect.Quote(StorageProvider.SqlServer, table.TableName)}', N'U') IS NULL",
            "BEGIN",
            $"    CREATE TABLE {RelationalSqlDialect.Quote(StorageProvider.SqlServer, table.TableName)} (",
            string.Join($",{RelationalSqlDialect.NewLine}", BuildColumnAndConstraintLines(StorageProvider.SqlServer, table, indent: "        ")),
            "    );",
            "END;");
    }

    private static IReadOnlyList<string> BuildColumnAndConstraintLines(
        StorageProvider provider,
        RelationalTablePlan table,
        string indent = "    ")
    {
        var primaryKeys = table.PrimaryKeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = table.Columns
            .Select(column =>
            {
                var nullability = primaryKeys.Contains(column.Name) ? "NOT NULL" : "NULL";
                return $"{indent}{RelationalSqlDialect.Quote(provider, column.Name)} {RelationalSqlDialect.FlexibleTextColumnType(provider)} {nullability}";
            })
            .ToList();

        lines.Add(
            $"{indent}CONSTRAINT {RelationalSqlDialect.Quote(provider, $"pk_{table.TableName}")} PRIMARY KEY ({JoinColumns(provider, table.PrimaryKeyColumns)})");

        return lines;
    }

    private static string JoinColumns(StorageProvider provider, IEnumerable<string> columns)
    {
        return string.Join(", ", columns.Select(column => RelationalSqlDialect.Quote(provider, column)));
    }
}
