using Sollatek.DataSync.AzureBlob;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.AzureBlob.Tests;

public sealed class AzureBlobSyncTargetDataStoreTests
{
    [Fact]
    public async Task HasStoredDataAsync_UsesConfiguredBlobLayout()
    {
        var container = new RecordingBlobExportContainer(
            "exports/202603/Assets_20260322.parquet",
            "exports/202603/Temperature_20260322.parquet");
        var store = new AzureBlobSyncTargetDataStore(
            container,
            new FileExportOptions
            {
                RootPath = "exports",
                FolderFormat = "yyyyMM",
                FileNameFormat = "{entity}_{date:yyyyMMdd}.{format}",
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
            });

        var assets = await store.HasStoredDataAsync(Metadata("assets"), CancellationToken.None);
        var doorOpening = await store.HasStoredDataAsync(Metadata("rawDataDooropeningdata"), CancellationToken.None);

        Assert.True(assets);
        Assert.False(doorOpening);
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

    private sealed class RecordingBlobExportContainer(params string[] blobNames) : IBlobExportContainer
    {
        private readonly string[] _blobNames = blobNames;

        public Task UploadAsync(
            string blobName,
            Stream content,
            string? contentType,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> ExistsAsync(
            string blobName,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_blobNames.Contains(blobName, StringComparer.Ordinal));
        }

        public async IAsyncEnumerable<string> ListAsync(
            string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var name in _blobNames.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
            }

            await Task.CompletedTask;
        }
    }
}
