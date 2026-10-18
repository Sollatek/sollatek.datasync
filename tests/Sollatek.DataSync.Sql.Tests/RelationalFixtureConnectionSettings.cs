using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Tests;

internal static class RelationalFixtureConnectionSettings
{
    public static string Create(StorageProvider provider)
    {
        var instance = Guid.NewGuid().ToString("N");
        var host = instance + ".invalid";
        return provider switch
        {
            StorageProvider.SqlServer => new SqlConnectionStringBuilder
            {
                DataSource = host,
                InitialCatalog = instance
            }.ConnectionString,
            StorageProvider.Postgres => new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Database = instance
            }.ConnectionString,
            StorageProvider.MySql => new MySqlConnectionStringBuilder
            {
                Server = host,
                Database = instance
            }.ConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
    }
}
