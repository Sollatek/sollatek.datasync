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
    [InlineData("azureBlob", StorageProvider.AzureBlob)]
    [InlineData("azureBlobStorage", StorageProvider.AzureBlob)]
    [InlineData("blob", StorageProvider.AzureBlob)]
    public void StorageOptions_ReadsSupportedProviders(string configuredProvider, StorageProvider expectedProvider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = configuredProvider,
                ["Storage:connectionString"] = "local",
                ["Storage:containerName"] = "exports"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(expectedProvider, options.Provider);
    }

    [Fact]
    public void StorageOptions_ReadsAzureBlobContainerName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "azureBlobStorage",
                ["Storage:connectionString"] = "UseDevelopmentStorage=true",
                ["Storage:containerName"] = "exports"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(StorageProvider.AzureBlob, options.Provider);
        Assert.Equal("UseDevelopmentStorage=true", options.ConnectionString);
        Assert.Equal("exports", options.ContainerName);
    }

    [Fact]
    public void StorageOptions_DefaultsAzureBlobAuthenticationToConnectionStringWhenConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "azureBlobStorage",
                ["Storage:connectionString"] = "UseDevelopmentStorage=true",
                ["Storage:containerName"] = "exports"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(AzureBlobAuthenticationMode.ConnectionString, options.BlobAuthentication);
    }

    [Fact]
    public void StorageOptions_ReadsAzureBlobDefaultCredentialAuthentication()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "azureBlobStorage",
                ["Storage:authentication"] = "defaultAzureCredential",
                ["Storage:accountName"] = "storageacct",
                ["Storage:containerName"] = "exports",
                ["Storage:managedIdentityClientId"] = "11111111-1111-1111-1111-111111111111"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(StorageProvider.AzureBlob, options.Provider);
        Assert.Equal(AzureBlobAuthenticationMode.DefaultAzureCredential, options.BlobAuthentication);
        Assert.Null(options.ConnectionString);
        Assert.Equal("storageacct", options.AccountName);
        Assert.Equal("exports", options.ContainerName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", options.ManagedIdentityClientId);
    }

    [Fact]
    public void StorageOptions_ReadsAzureBlobContainerUriAuthentication()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "azureBlobStorage",
                ["Storage:authentication"] = "containerUri",
                ["Storage:containerUri"] = "https://storageacct.blob.core.windows.net/exports"
            })
            .Build();

        var options = StorageOptions.FromConfiguration(configuration);

        Assert.Equal(AzureBlobAuthenticationMode.ContainerUri, options.BlobAuthentication);
        Assert.Equal("https://storageacct.blob.core.windows.net/exports", options.ContainerUri);
        Assert.Null(options.ContainerName);
    }

    [Fact]
    public void StorageOptions_RejectsAzureBlobIdentityAuthenticationWithoutContainerAddress()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "azureBlobStorage",
                ["Storage:authentication"] = "defaultAzureCredential",
                ["Storage:containerName"] = "exports"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StorageOptions.FromConfiguration(configuration));

        Assert.Contains("Storage:accountName, Storage:blobServiceUri, or Storage:containerUri", exception.Message);
    }

    [Fact]
    public void StorageOptions_RejectsMissingAzureBlobContainerName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:provider"] = "azureBlobStorage",
                ["Storage:connectionString"] = "UseDevelopmentStorage=true"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StorageOptions.FromConfiguration(configuration));

        Assert.Contains("Storage:containerName", exception.Message);
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
        Assert.Contains("sqlserver, postgres, mysql, mongo, filesystem, azureBlobStorage", exception.Message);
    }

    [Fact]
    public void FileExportOptions_DefaultsToParquetUnderArtifacts()
    {
        var options = FileExportOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(".artifacts/exports", options.RootPath);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "_state", "sync-state.json"), options.StatePath);
        Assert.Equal("parquet", options.Format);
        Assert.Equal("{entityKey}/year={date:yyyy}/month={date:MM}/day={date:dd}", options.FolderFormat);
        Assert.Equal("part-{part:000000}.{format}", options.FileNameFormat);
        Assert.True(options.ReplaceExisting);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void FileExportOptions_ReadsReplaceExisting(string configuredValue, bool expectedValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:replaceExisting"] = configuredValue
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        Assert.Equal(expectedValue, options.ReplaceExisting);
    }

    [Fact]
    public void FileExportOptions_ResolvesRelativeStatePathAgainstAppBase()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:statePath"] = "_state/custom-state.json"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "_state", "custom-state.json"), options.StatePath);
    }

    [Theory]
    [InlineData("csv", "csv")]
    [InlineData("xml", "xml")]
    [InlineData("xlsx", "xlsx")]
    [InlineData("parquet", "parquet")]
    public void FileExportOptions_ReadsPortalExportFormat(string configuredFormat, string expectedFormat)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:format"] = configuredFormat
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        Assert.Equal(expectedFormat, options.Format);
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

        Assert.Contains("FileExport:format must be one of: Csv, Xml, Xlsx, Parquet", exception.Message);
    }

    [Fact]
    public void FileExportOptions_RejectsLegacyEntityPolicies()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileExport:entities:assets:dataMode"] = "full"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FileExportOptions.FromConfiguration(configuration));

        Assert.Contains("FileExport:entities is no longer supported", exception.Message);
        Assert.Contains("SyncPlan", exception.Message);
    }

    [Fact]
    public void FileExportOptions_ReadsEntityExportPoliciesFromSyncPlanEntries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0:assets:dataMode"] = "full",
                ["SyncPlan:0:assets:outputName"] = "Assets",
                ["SyncPlan:1:rawdata/temperaturedata:outputName"] = "Temperature",
                ["SyncPlan:2:rawdata/dooropeningdata:dataMode"] = "differential",
                ["SyncPlan:2:rawdata/dooropeningdata:partitionDate"] = "exportRunDay",
                ["SyncPlan:2:rawdata/dooropeningdata:outputName"] = "Dooropening"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        var assets = options.GetEntityOptions("assets");
        Assert.Equal(FileExportDataMode.Full, assets.DataMode);
        Assert.Equal("Assets", assets.OutputName);

        var temperature = options.GetEntityOptions("rawDataTemperaturedata");
        Assert.Equal("Temperature", temperature.OutputName);

        var doorOpening = options.GetEntityOptions("rawDataDooropeningdata");
        Assert.Equal(FileExportDataMode.Differential, doorOpening.DataMode);
        Assert.Equal(FileExportPartitionDateMode.ExportRunDay, doorOpening.PartitionDate);
        Assert.Equal("Dooropening", doorOpening.OutputName);
    }

    [Fact]
    public void FileExportOptions_IgnoresSyncPlanEntriesWithoutFileExportPolicy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlan:0:assets:initial"] = "full"
            })
            .Build();

        var options = FileExportOptions.FromConfiguration(configuration);

        var assets = options.GetEntityOptions("assets");
        Assert.Equal(FileExportDataMode.Differential, assets.DataMode);
        Assert.Null(assets.OutputName);
    }

    [Fact]
    public void SyncPlanOptions_DefaultsInitialModeToDifferential()
    {
        var options = SyncPlanOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(SyncInitialDataMode.Differential, options.GetEntityOptions("assets").Initial);
    }

    [Fact]
    public void SyncPlanOptions_UsesSyncInitialAsDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:initial"] = "full",
                ["SyncPlan:0"] = "assets"
            })
            .Build();

        var options = SyncPlanOptions.FromConfiguration(configuration);

        Assert.Equal(SyncInitialDataMode.Full, options.GetEntityOptions("assets").Initial);
        Assert.Equal(SyncInitialDataMode.Full, options.GetEntityOptions("rawDataTemperaturedata").Initial);
    }

    [Fact]
    public void SyncPlanOptions_PrefersEntityInitialOverSyncInitial()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:initial"] = "full",
                ["SyncPlan:0:rawdata/temperaturedata:initial"] = "differential"
            })
            .Build();

        var options = SyncPlanOptions.FromConfiguration(configuration);

        Assert.Equal(SyncInitialDataMode.Differential, options.GetEntityOptions("rawDataTemperaturedata").Initial);
        Assert.Equal(SyncInitialDataMode.Full, options.GetEntityOptions("assets").Initial);
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
