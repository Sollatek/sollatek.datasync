#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalTargetDataCommandBuilder
{
    public static RelationalCommand BuildHasAnyRows(
        StorageProvider provider,
        string tableName)
    {
        EnsureRelationalProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var quotedTable = RelationalSqlDialect.Quote(provider, tableName);
        var sql = provider switch
        {
            StorageProvider.SqlServer => $"SELECT TOP (1) 1 FROM {quotedTable};",
            StorageProvider.Postgres or StorageProvider.MySql => $"SELECT 1 FROM {quotedTable} LIMIT 1;",
            _ => throw new InvalidOperationException($"Unsupported relational provider '{provider}'.")
        };

        return new RelationalCommand(sql, []);
    }

    public static RelationalCommand BuildGetLatestWatermark(
        StorageProvider provider,
        string tableName,
        string columnName)
    {
        EnsureRelationalProvider(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var sql =
            $"SELECT MAX({RelationalSqlDialect.Quote(provider, columnName)}) FROM {RelationalSqlDialect.Quote(provider, tableName)};";

        return new RelationalCommand(sql, []);
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
