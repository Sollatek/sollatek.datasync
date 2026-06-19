using System.Data.Common;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalCommandBinderTests
{
    [Fact]
    public void CreateCommand_BindsSqlServerParametersWithAtPrefixedNames()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.SqlServer,
            "Server=localhost;Database=datasync;User ID=user;Password=pass;Encrypt=True;Trust Server Certificate=True");
        var command = CreateCommand("@p0");

        using var dbCommand = RelationalCommandBinder.CreateCommand(
            StorageProvider.SqlServer,
            connection,
            transaction: null,
            command);

        Assert.Equal(command.Sql, dbCommand.CommandText);
        var parameter = Assert.Single(dbCommand.Parameters.Cast<DbParameter>());
        Assert.Equal("@p0", parameter.ParameterName);
        Assert.Equal(42L, parameter.Value);
    }

    [Fact]
    public void CreateCommand_BindsPostgresParametersInPlaceholderOrder()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.Postgres,
            "Host=localhost;Database=datasync;Username=postgres;Password=pass");
        var command = CreateCommand("$1");

        using var dbCommand = RelationalCommandBinder.CreateCommand(
            StorageProvider.Postgres,
            connection,
            transaction: null,
            command);

        Assert.Equal(command.Sql, dbCommand.CommandText);
        var parameter = Assert.Single(dbCommand.Parameters.Cast<DbParameter>());
        Assert.Equal("", parameter.ParameterName);
        Assert.Equal(42L, parameter.Value);
    }

    [Fact]
    public void CreateCommand_BindsMySqlParametersWithoutAtPrefix()
    {
        using var connection = RelationalConnectionFactory.Create(
            StorageProvider.MySql,
            "Server=localhost;Database=datasync;User ID=mysql;Password=pass");
        var command = CreateCommand("@p0");

        using var dbCommand = RelationalCommandBinder.CreateCommand(
            StorageProvider.MySql,
            connection,
            transaction: null,
            command);

        Assert.Equal(command.Sql, dbCommand.CommandText);
        var parameter = Assert.Single(dbCommand.Parameters.Cast<DbParameter>());
        Assert.Equal("p0", parameter.ParameterName);
        Assert.Equal(42L, parameter.Value);
    }

    private static RelationalCommand CreateCommand(string placeholder)
    {
        var name = placeholder == "$1" ? "p1" : "p0";
        return new RelationalCommand(
            $"SELECT {placeholder}",
            [new RelationalCommandParameter(name, placeholder, 42L)]);
    }
}
