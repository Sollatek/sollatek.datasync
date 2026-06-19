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
            "Server=tcp:example.database.windows.net,1433;Initial Catalog=datasync;User ID=user;Password=pass;Encrypt=True;Trust Server Certificate=False;Connection Timeout=30");

        Assert.IsType<SqlConnection>(connection);
    }

    [Fact]
    public void Create_ReturnsPostgresConnection()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.Postgres,
            "Host=localhost;Port=5432;Database=datasync;Username=postgres;Password=pass;Ssl Mode=Require;Timeout=30");

        Assert.IsType<NpgsqlConnection>(connection);
    }

    [Fact]
    public void Create_ReturnsMySqlConnection()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.MySql,
            "Server=localhost;Port=3306;Database=datasync;User ID=mysql;Password=pass;SslMode=Required;Connection Timeout=30");

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
