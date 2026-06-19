#nullable enable

using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalConnectionFactory
{
    public static DbConnection Create(StorageProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return provider switch
        {
            StorageProvider.SqlServer => new SqlConnection(connectionString),
            StorageProvider.Postgres => new NpgsqlConnection(connectionString),
            StorageProvider.MySql => new MySqlConnection(connectionString),
            _ => throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.")
        };
    }
}
