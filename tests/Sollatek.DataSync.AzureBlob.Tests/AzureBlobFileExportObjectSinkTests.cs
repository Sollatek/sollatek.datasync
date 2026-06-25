using Sollatek.DataSync.AzureBlob;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.AzureBlob.Tests;

public sealed class AzureBlobFileExportObjectSinkTests
{
    [Fact]
    public async Task CopyAsync_UploadsSourceFileToRenderedBlobName()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"datasync-blob-source-{Guid.NewGuid():N}.parquet");
        await File.WriteAllTextAsync(sourcePath, "payload");
        try
        {
            var container = new RecordingBlobExportContainer();
            var sink = new AzureBlobFileExportObjectSink(
                container,
                new FileExportOptions
                {
                    RootPath = "exports",
                    FolderFormat = "yyyyMM",
                    FileNameFormat = "{entity}_{date:yyyyMMdd}.{format}",
                    Format = "parquet",
                    Entities = new Dictionary<string, FileExportEntityOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["assets"] = new()
                        {
                            OutputName = "Assets"
                        }
                    }
                });

            var blobName = await sink.CopyAsync(
                "assets",
                new DateOnly(2026, 3, 22),
                sourcePath,
                partNumber: 0,
                CancellationToken.None);

            Assert.Equal("exports/202603/Assets_20260322.parquet", blobName);
            Assert.True(container.Blobs.TryGetValue("exports/202603/Assets_20260322.parquet", out var content));
            Assert.Equal("payload", content);
            Assert.Equal(
                "application/vnd.apache.parquet",
                container.ContentTypes["exports/202603/Assets_20260322.parquet"]);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task CopyAsync_ReplacesExistingBlobByDefault()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"datasync-blob-source-{Guid.NewGuid():N}.parquet");
        await File.WriteAllTextAsync(sourcePath, "new payload");
        try
        {
            var container = new RecordingBlobExportContainer();
            container.Blobs["exports/assets/year=2026/month=03/day=22/part-000000.parquet"] = "old payload";
            var sink = new AzureBlobFileExportObjectSink(
                container,
                new FileExportOptions
                {
                    RootPath = "exports"
                });

            var blobName = await sink.CopyAsync(
                "assets",
                new DateOnly(2026, 3, 22),
                sourcePath,
                partNumber: 0,
                CancellationToken.None);

            Assert.Equal("exports/assets/year=2026/month=03/day=22/part-000000.parquet", blobName);
            Assert.Equal("new payload", container.Blobs[blobName]);
            Assert.Equal([true], container.ReplaceExistingValues);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task CopyAsync_RejectsExistingBlobWhenReplaceExistingIsDisabled()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"datasync-blob-source-{Guid.NewGuid():N}.parquet");
        await File.WriteAllTextAsync(sourcePath, "new payload");
        try
        {
            var container = new RecordingBlobExportContainer();
            container.Blobs["exports/assets/year=2026/month=03/day=22/part-000000.parquet"] = "old payload";
            var sink = new AzureBlobFileExportObjectSink(
                container,
                new FileExportOptions
                {
                    RootPath = "exports",
                    ReplaceExisting = false
                });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sink.CopyAsync(
                    "assets",
                    new DateOnly(2026, 3, 22),
                    sourcePath,
                    partNumber: 0,
                    CancellationToken.None));

            Assert.Contains("already exists", exception.Message);
            Assert.Equal("old payload", container.Blobs["exports/assets/year=2026/month=03/day=22/part-000000.parquet"]);
            Assert.Empty(container.ReplaceExistingValues);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task ExistsAsync_ChecksRenderedBlobName()
    {
        var container = new RecordingBlobExportContainer();
        container.Blobs["exports/202603/Assets_20260322.parquet"] = "payload";
        var sink = new AzureBlobFileExportObjectSink(
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
                    }
                }
            });

        var exists = await sink.ExistsAsync(
            "assets",
            new DateOnly(2026, 3, 22),
            partNumber: 0,
            CancellationToken.None);

        Assert.True(exists);
    }

    private sealed class RecordingBlobExportContainer : IBlobExportContainer
    {
        public Dictionary<string, string> Blobs { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> ContentTypes { get; } = new(StringComparer.Ordinal);

        public List<bool> ReplaceExistingValues { get; } = [];

        public async Task UploadAsync(
            string blobName,
            Stream content,
            string? contentType,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(content);
            Blobs[blobName] = await reader.ReadToEndAsync(cancellationToken);
            ContentTypes[blobName] = contentType;
            ReplaceExistingValues.Add(replaceExisting);
        }

        public Task<bool> ExistsAsync(
            string blobName,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Blobs.ContainsKey(blobName));
        }

        public async IAsyncEnumerable<string> ListAsync(
            string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var name in Blobs.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
            }

            await Task.CompletedTask;
        }
    }
}
