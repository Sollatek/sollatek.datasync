using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalConnectionFactoryTests
{
    [Fact]
    public void Create_ReturnsSqlServerConnection()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.SqlServer,
            RelationalFixtureConnectionSettings.Create(StorageProvider.SqlServer));

        Assert.IsType<SqlConnection>(connection);
    }

    [Fact]
    public void Create_ReturnsPostgresConnection()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.Postgres,
            RelationalFixtureConnectionSettings.Create(StorageProvider.Postgres));

        Assert.IsType<NpgsqlConnection>(connection);
    }

    [Fact]
    public void Create_ReturnsMySqlConnection()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.MySql,
            RelationalFixtureConnectionSettings.Create(StorageProvider.MySql));

        Assert.IsType<MySqlConnection>(connection);
    }

    [Fact]
    public void Create_RejectsNonRelationalProviders()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelationalConnectionFactory.Create(StorageProvider.Filesystem, "unused"));

        Assert.Contains("filesystem", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relational", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
