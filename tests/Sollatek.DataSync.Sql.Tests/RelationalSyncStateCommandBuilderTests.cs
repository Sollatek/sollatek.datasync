using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSyncStateCommandBuilderTests
{
    [Theory]
    [InlineData(StorageProvider.Postgres, "CREATE TABLE IF NOT EXISTS \"__sollatek_datasync_state\"")]
    [InlineData(StorageProvider.MySql, "CREATE TABLE IF NOT EXISTS `__sollatek_datasync_state`")]
    [InlineData(StorageProvider.SqlServer, "IF OBJECT_ID(N'[__sollatek_datasync_state]', N'U') IS NULL")]
    public void BuildCreateTableIfMissing_UsesProviderQuoting(
        StorageProvider provider,
        string expectedStart)
    {
        var command = RelationalSyncStateCommandBuilder.BuildCreateTableIfMissing(provider);

        Assert.StartsWith(expectedStart, command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Theory]
    [InlineData(StorageProvider.Postgres, "$1")]
    [InlineData(StorageProvider.MySql, "@p0")]
    [InlineData(StorageProvider.SqlServer, "@p0")]
    public void BuildGetLastSuccessfulEnd_UsesProviderParameterPlaceholders(
        StorageProvider provider,
        string firstPlaceholder)
    {
        var command = RelationalSyncStateCommandBuilder.BuildGetLastSuccessfulEnd(provider, "assets");

        Assert.Contains(firstPlaceholder, command.Sql);
        Assert.Equal(["assets"], command.Parameters.Select(x => x.Value).ToArray());
    }

    [Theory]
    [InlineData(StorageProvider.Postgres, "ON CONFLICT")]
    [InlineData(StorageProvider.MySql, "ON DUPLICATE KEY UPDATE")]
    [InlineData(StorageProvider.SqlServer, "MERGE")]
    public void BuildSaveSuccessfulEnd_UsesProviderUpsertSyntax(
        StorageProvider provider,
        string expectedSql)
    {
        var end = new DateTimeOffset(2026, 6, 19, 8, 30, 0, TimeSpan.Zero);
        var updatedAt = new DateTimeOffset(2026, 6, 19, 8, 31, 0, TimeSpan.Zero);

        var command = RelationalSyncStateCommandBuilder.BuildSaveSuccessfulEnd(
            provider,
            "assets",
            end,
            updatedAt);

        Assert.Contains(expectedSql, command.Sql);
        Assert.Contains("last_successful_end_utc", command.Sql);
        Assert.Equal(
            ["assets", "2026-06-19T08:30:00.0000000+00:00", "2026-06-19T08:31:00.0000000+00:00"],
            command.Parameters.Select(x => x.Value).ToArray());
    }
}
