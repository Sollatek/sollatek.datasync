#nullable enable

using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalSchemaValidationCommandBuilder
{
    public static RelationalCommand BuildTableExists(
        StorageProvider provider,
        string tableName)
    {
        EnsureRelationalProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var sql = provider switch
        {
            StorageProvider.Postgres => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM information_schema.tables",
                "WHERE table_schema = current_schema()",
                $"  AND table_name = {Placeholder(provider, 0)};"),
            StorageProvider.MySql => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM information_schema.TABLES",
                "WHERE TABLE_SCHEMA = DATABASE()",
                $"  AND TABLE_NAME = {Placeholder(provider, 0)};"),
            StorageProvider.SqlServer => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM INFORMATION_SCHEMA.TABLES",
                "WHERE TABLE_SCHEMA = SCHEMA_NAME()",
                $"  AND TABLE_NAME = {Placeholder(provider, 0)};"),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, BuildParameters(provider, [tableName]));
    }

    public static RelationalCommand BuildColumnExists(
        StorageProvider provider,
        string tableName,
        string columnName)
    {
        EnsureRelationalProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var sql = provider switch
        {
            StorageProvider.Postgres => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM information_schema.columns",
                "WHERE table_schema = current_schema()",
                $"  AND table_name = {Placeholder(provider, 0)}",
                $"  AND column_name = {Placeholder(provider, 1)};"),
            StorageProvider.MySql => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM information_schema.COLUMNS",
                "WHERE TABLE_SCHEMA = DATABASE()",
                $"  AND TABLE_NAME = {Placeholder(provider, 0)}",
                $"  AND COLUMN_NAME = {Placeholder(provider, 1)};"),
            StorageProvider.SqlServer => string.Join(
                RelationalSqlDialect.NewLine,
                "SELECT COUNT(1)",
                "FROM INFORMATION_SCHEMA.COLUMNS",
                "WHERE TABLE_SCHEMA = SCHEMA_NAME()",
                $"  AND TABLE_NAME = {Placeholder(provider, 0)}",
                $"  AND COLUMN_NAME = {Placeholder(provider, 1)};"),
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, BuildParameters(provider, [tableName, columnName]));
    }

    private static IReadOnlyList<RelationalCommandParameter> BuildParameters(
        StorageProvider provider,
        IReadOnlyList<object?> values)
    {
        return values
            .Select((value, index) => new RelationalCommandParameter(
                $"p{index.ToString(CultureInfo.InvariantCulture)}",
                Placeholder(provider, index),
                value))
            .ToArray();
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
