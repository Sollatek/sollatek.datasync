using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Filesystem.Tests;

public sealed class FilesystemSyncTargetDataStoreTests
{
    [Fact]
    public async Task HasStoredDataAsync_ReturnsFalseWhenEntityFolderHasNoParquetFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            var store = new FilesystemSyncTargetDataStore(new FileExportOptions { RootPath = directory });

            var result = await store.HasStoredDataAsync(Metadata("assets"), CancellationToken.None);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HasStoredDataAsync_ReturnsTrueWhenEntityFolderHasParquetFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            var partDirectory = Path.Combine(directory, "assets", "year=2026", "month=06", "day=19");
            Directory.CreateDirectory(partDirectory);
            await File.WriteAllTextAsync(Path.Combine(partDirectory, "part-000000.parquet"), "data");
            var store = new FilesystemSyncTargetDataStore(new FileExportOptions { RootPath = directory });

            var result = await store.HasStoredDataAsync(Metadata("assets"), CancellationToken.None);

            Assert.True(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HasStoredDataAsync_ReturnsTrueWhenConfiguredLayoutHasEntityFile()
    {
        var directory = CreateTempDirectory();
        try
        {
            var exportDirectory = Path.Combine(directory, "202602");
            Directory.CreateDirectory(exportDirectory);
            await File.WriteAllTextAsync(Path.Combine(exportDirectory, "Assets_20260202.parquet"), "data");
            await File.WriteAllTextAsync(Path.Combine(exportDirectory, "Temperature_20260202.parquet"), "data");
            var options = new FileExportOptions
            {
                RootPath = directory,
                FolderFormat = "yyyyMM",
                FileNameFormat = "{entity}_{date:yyyyMMdd}.parquet",
                Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["assets"] = new()
                    {
                        OutputName = "Assets"
                    },
                    ["rawDataDooropeningdata"] = new()
                    {
                        OutputName = "Dooropening"
                    }
                }
            };
            var store = new FilesystemSyncTargetDataStore(options);

            var assets = await store.HasStoredDataAsync(Metadata("assets"), CancellationToken.None);
            var doorOpening = await store.HasStoredDataAsync(Metadata("rawDataDooropeningdata"), CancellationToken.None);

            Assert.True(assets);
            Assert.False(doorOpening);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"datasync-filesystem-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static SwaggerSyncEntityMetadata Metadata(string entityKey)
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = entityKey,
            OperationIds = [$"{entityKey}_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }
}
