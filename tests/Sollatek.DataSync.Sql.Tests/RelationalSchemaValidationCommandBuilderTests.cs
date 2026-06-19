using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSchemaValidationCommandBuilderTests
{
    [Theory]
    [InlineData(StorageProvider.Postgres, "information_schema.tables")]
    [InlineData(StorageProvider.MySql, "information_schema.TABLES")]
    [InlineData(StorageProvider.SqlServer, "INFORMATION_SCHEMA.TABLES")]
    public void BuildTableExists_QueriesProviderTableCatalog(
        StorageProvider provider,
        string expectedCatalog)
    {
        var command = RelationalSchemaValidationCommandBuilder.BuildTableExists(provider, "assets");

        Assert.Contains(expectedCatalog, command.Sql);
        Assert.Equal(["assets"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Theory]
    [InlineData(StorageProvider.Postgres, "information_schema.columns")]
    [InlineData(StorageProvider.MySql, "information_schema.COLUMNS")]
    [InlineData(StorageProvider.SqlServer, "INFORMATION_SCHEMA.COLUMNS")]
    public void BuildColumnExists_QueriesProviderColumnCatalog(
        StorageProvider provider,
        string expectedCatalog)
    {
        var command = RelationalSchemaValidationCommandBuilder.BuildColumnExists(
            provider,
            "assets",
            "owner_customer_id");

        Assert.Contains(expectedCatalog, command.Sql);
        Assert.Equal(["assets", "owner_customer_id"], command.Parameters.Select(x => x.Value).ToArray());
    }
}
