using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSchemaManifestCommandBuilderTests
{
    [Theory]
    [InlineData(StorageProvider.Postgres, "CREATE TABLE IF NOT EXISTS \"__sollatek_datasync_schema_manifest\"")]
    [InlineData(StorageProvider.MySql, "CREATE TABLE IF NOT EXISTS `__sollatek_datasync_schema_manifest`")]
    [InlineData(StorageProvider.SqlServer, "IF OBJECT_ID(N'[__sollatek_datasync_schema_manifest]', N'U') IS NULL")]
    public void BuildCreateTableIfMissing_UsesProviderQuoting(
        StorageProvider provider,
        string expectedStart)
    {
        var command = RelationalSchemaManifestCommandBuilder.BuildCreateTableIfMissing(provider);

        Assert.StartsWith(expectedStart, command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Theory]
    [InlineData(StorageProvider.Postgres, "$1")]
    [InlineData(StorageProvider.MySql, "@p0")]
    [InlineData(StorageProvider.SqlServer, "@p0")]
    public void BuildIsCurrent_UsesProviderParameterPlaceholders(
        StorageProvider provider,
        string firstPlaceholder)
    {
        var command = RelationalSchemaManifestCommandBuilder.BuildIsCurrent(provider, Manifest(provider));

        Assert.Contains(firstPlaceholder, command.Sql);
        Assert.Equal(
            ["current", SchemaManifest.CurrentVersion.ToString(), provider.ToString(), "plan", "schema"],
            command.Parameters.Select(x => x.Value).ToArray());
    }

    [Theory]
    [InlineData(StorageProvider.Postgres, "ON CONFLICT")]
    [InlineData(StorageProvider.MySql, "ON DUPLICATE KEY UPDATE")]
    [InlineData(StorageProvider.SqlServer, "MERGE")]
    public void BuildSave_UsesProviderUpsertSyntax(
        StorageProvider provider,
        string expectedSql)
    {
        var command = RelationalSchemaManifestCommandBuilder.BuildSave(provider, Manifest(provider));

        Assert.Contains(expectedSql, command.Sql);
        Assert.Contains("applied_at_utc", command.Sql);
        Assert.Equal(6, command.Parameters.Count);
    }

    private static SchemaManifest Manifest(StorageProvider provider)
    {
        return new SchemaManifest(
            SchemaManifest.CurrentVersion,
            provider,
            "plan",
            "schema");
    }
}
