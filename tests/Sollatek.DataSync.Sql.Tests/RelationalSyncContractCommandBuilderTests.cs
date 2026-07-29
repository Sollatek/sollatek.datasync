using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSyncContractCommandBuilderTests
{
    [Theory]
    [InlineData(StorageProvider.Postgres, "text")]
    [InlineData(StorageProvider.MySql, "longtext")]
    [InlineData(StorageProvider.SqlServer, "nvarchar(max)")]
    public void BuildCreateTableIfMissing_UsesUnboundedSnapshotColumn(
        StorageProvider provider,
        string expectedType)
    {
        var command =
            RelationalSyncContractCommandBuilder.BuildCreateTableIfMissing(provider);

        Assert.Contains(RelationalSyncContractCommandBuilder.TableName, command.Sql);
        Assert.Contains(expectedType, command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Theory]
    [InlineData(StorageProvider.Postgres, "ON CONFLICT")]
    [InlineData(StorageProvider.MySql, "ON DUPLICATE KEY UPDATE")]
    [InlineData(StorageProvider.SqlServer, "MERGE")]
    public void BuildSave_UpsertsAcceptedSnapshot(
        StorageProvider provider,
        string expectedSyntax)
    {
        var command = RelationalSyncContractCommandBuilder.BuildSave(
            provider,
            """{"formatVersion":1}""");

        Assert.Contains(expectedSyntax, command.Sql);
        Assert.Equal(
            ["accepted", """{"formatVersion":1}"""],
            command.Parameters.Select(parameter => parameter.Value).ToArray());
    }
}
