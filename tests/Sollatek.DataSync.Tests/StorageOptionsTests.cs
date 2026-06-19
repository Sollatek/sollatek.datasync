using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Tests;

public sealed class StorageOptionsTests
{
    [Fact]
    public void StorageOptions_DefaultsToSqlServerAndLegacyConnection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:dbConnection"] = "Server=localhost;Database=legacy;"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(StorageProvider.SqlServer, options.Provider);
        Assert.Equal("Server=localhost;Database=legacy;", options.ConnectionString);
        Assert.Equal(StorageSchemaMode.Validate, options.SchemaMode);
    }

    [Fact]
    public void StorageOptions_PrefersStorageConnectionOverLegacyConnection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:connectionString"] = "Server=localhost;Database=storage;",
                ["Settings:dbConnection"] = "Server=localhost;Database=legacy;"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal("Server=localhost;Database=storage;", options.ConnectionString);
    }

    [Fact]
    public void StorageOptions_ReadsNamedDataSyncConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataSync"] = "Host=postgres.example.com;Database=datasync;Username=user;Password=pass;Ssl Mode=Require"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal("Host=postgres.example.com;Database=datasync;Username=user;Password=pass;Ssl Mode=Require", options.ConnectionString);
    }

    [Fact]
    public void StorageOptions_PrefersStorageConnectionOverNamedConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:connectionString"] = "Server=sql.example.com;Database=storage;",
                ["ConnectionStrings:DataSync"] = "Server=sql.example.com;Database=named;"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal("Server=sql.example.com;Database=storage;", options.ConnectionString);
    }

    [Theory]
    [InlineData("sqlserver", StorageProvider.SqlServer)]
    [InlineData("postgres", StorageProvider.Postgres)]
    [InlineData("mysql", StorageProvider.MySql)]
    [InlineData("mongo", StorageProvider.Mongo)]
    [InlineData("filesystem", StorageProvider.Filesystem)]
    public void StorageOptions_ReadsSupportedProviders(string configuredProvider, StorageProvider expectedProvider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = configuredProvider,
                ["Storage:connectionString"] = "local"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(expectedProvider, options.Provider);
    }

    [Fact]
    public void StorageOptions_AllowsFilesystemWithoutConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "filesystem"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(StorageProvider.Filesystem, options.Provider);
        Assert.Null(options.ConnectionString);
    }

    [Fact]
    public void StorageOptions_RejectsMissingDatabaseConnection()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StorageOptions.FromConfiguration(configuration));

        Assert.Contains("Storage:connectionString", exception.Message);
    }

    [Fact]
    public void StorageOptions_RejectsUnknownProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "oracle",
                ["Storage:connectionString"] = "local"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StorageOptions.FromConfiguration(configuration));

        Assert.Contains("Unknown storage provider 'oracle'", exception.Message);
        Assert.Contains("sqlserver, postgres, mysql, mongo, filesystem", exception.Message);
    }

    [Fact]
    public void FileExportOptions_DefaultsToParquetUnderArtifacts()
    {
        var options = FileExportOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(".artifacts/exports", options.RootPath);
        Assert.Equal("parquet", options.Format);
    }

    [Fact]
    public void FileExportOptions_RejectsUnsupportedFormat()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:format"] = "json"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FileExportOptions.FromConfiguration(configuration));

        Assert.Contains("FileExport:format must be parquet", exception.Message);
    }

    [Fact]
    public void FileExportOptions_ReadsPerEntityExportPolicies()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:entities:assets:dataMode"] = "full",
                ["FileExport:entities:rawDataLocationdata:partitionDate"] = "exportRunDay"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        var assets = options.GetEntityOptions("assets");
        Assert.Equal(FileExportDataMode.Full, assets.DataMode);
        Assert.Equal(FileExportPartitionDateMode.WatermarkDay, assets.PartitionDate);

        var rawData = options.GetEntityOptions("rawDataLocationdata");
        Assert.Equal(FileExportDataMode.Differential, rawData.DataMode);
        Assert.Equal(FileExportPartitionDateMode.ExportRunDay, rawData.PartitionDate);
    }

    [Fact]
    public void FileExportOptions_ReadsEntityExportPoliciesFromSyncPlanEntries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0"] = "assets",
                ["SyncPlan:1:rawdata/dooropeningdata:dataMode"] = "differential",
                ["SyncPlan:1:rawdata/dooropeningdata:partitionDate"] = "exportRunDay"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        var rawData = options.GetEntityOptions("rawDataDooropeningdata");
        Assert.Equal(FileExportDataMode.Differential, rawData.DataMode);
        Assert.Equal(FileExportPartitionDateMode.ExportRunDay, rawData.PartitionDate);
    }

    [Fact]
    public void FileExportOptions_PrefersSyncPlanPolicyOverLegacyFileExportEntityPolicy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:entities:rawDataDooropeningdata:partitionDate"] = "watermarkDay",
                ["SyncPlan:0:rawdata/dooropeningdata:partitionDate"] = "exportRunDay"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        var rawData = options.GetEntityOptions("rawDataDooropeningdata");
        Assert.Equal(FileExportPartitionDateMode.ExportRunDay, rawData.PartitionDate);
    }

    [Fact]
    public void FileExportOptions_IgnoresSyncPlanEntriesWithoutFileExportPolicy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:entities:assets:dataMode"] = "full",
                ["SyncPlan:0:assets:initial"] = "full"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        var assets = options.GetEntityOptions("assets");
        Assert.Equal(FileExportDataMode.Full, assets.DataMode);
    }

    [Fact]
    public void SyncPlanOptions_DefaultsInitialModeToDifferential()
    {
        var options = SyncPlanOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(SyncInitialDataMode.Differential, options.GetEntityOptions("assets").Initial);
    }

    [Fact]
    public void SyncPlanOptions_ReadsInitialFullPolicyFromSyncPlanEntry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0:assets:initial"] = "full"
            })
            .Build();

        var options = SyncPlanOptions.FromConfiguration(configuration);

        Assert.Equal(SyncInitialDataMode.Full, options.GetEntityOptions("assets").Initial);
    }

    [Fact]
    public void SyncPlanOptions_ResolvesRawDataPathAliases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0:rawdata/locationdata:initial"] = "full"
            })
            .Build();

        var options = SyncPlanOptions.FromConfiguration(configuration);

        Assert.Equal(SyncInitialDataMode.Full, options.GetEntityOptions("rawDataLocationdata").Initial);
    }

    [Fact]
    public void SyncPlanOptions_RejectsUnsupportedInitialMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0:assets:initial"] = "everything"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SyncPlanOptions.FromConfiguration(configuration));

        Assert.Contains("SyncPlan initial must be one of: differential, full", exception.Message);
    }
}
