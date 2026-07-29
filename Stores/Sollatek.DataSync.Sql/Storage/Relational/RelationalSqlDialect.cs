#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

internal static class RelationalSqlDialect
{
    public const string NewLine = "\n";

    public static string Quote(StorageProvider provider, string identifier)
    {
        return provider switch
        {
            StorageProvider.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            StorageProvider.Postgres => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
            StorageProvider.MySql => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
            _ => throw new InvalidOperationException($"Storage provider '{provider}' does not support SQL identifiers.")
        };
    }

    public static string FlexibleTextColumnType(StorageProvider provider)
    {
        return provider switch
        {
            StorageProvider.SqlServer => "nvarchar(450)",
            StorageProvider.Postgres => "text",
            StorageProvider.MySql => "varchar(512)",
            _ => throw new InvalidOperationException($"Storage provider '{provider}' is not a relational provider.")
        };
    }

    public static string LargeTextColumnType(StorageProvider provider)
    {
        return provider switch
        {
            StorageProvider.SqlServer => "nvarchar(max)",
            StorageProvider.Postgres => "text",
            StorageProvider.MySql => "longtext",
            _ => throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.")
        };
    }
}
