using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Sql.Tests;

public sealed class RelationalTargetDataCommandBuilderTests
{
    [Theory]
    [InlineData(StorageProvider.SqlServer, "SELECT TOP (1) 1 FROM [assets];")]
    [InlineData(StorageProvider.Postgres, "SELECT 1 FROM \"assets\" LIMIT 1;")]
    [InlineData(StorageProvider.MySql, "SELECT 1 FROM `assets` LIMIT 1;")]
    public void BuildHasAnyRows_UsesProviderSpecificLimitSyntax(
        StorageProvider provider,
        string expectedSql)
    {
        var command = RelationalTargetDataCommandBuilder.BuildHasAnyRows(provider, "assets");

        Assert.Equal(expectedSql, command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Theory]
    [InlineData(StorageProvider.SqlServer, "SELECT MAX([modification_date_time]) FROM [assets];")]
    [InlineData(StorageProvider.Postgres, "SELECT MAX(\"modification_date_time\") FROM \"assets\";")]
    [InlineData(StorageProvider.MySql, "SELECT MAX(`modification_date_time`) FROM `assets`;")]
    public void BuildGetLatestWatermark_UsesProviderSpecificQuoting(
        StorageProvider provider,
        string expectedSql)
    {
        var command = RelationalTargetDataCommandBuilder.BuildGetLatestWatermark(
            provider,
            "assets",
            "modification_date_time");

        Assert.Equal(expectedSql, command.Sql);
        Assert.Empty(command.Parameters);
    }
}
