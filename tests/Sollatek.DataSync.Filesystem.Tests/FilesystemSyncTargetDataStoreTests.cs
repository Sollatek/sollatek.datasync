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
