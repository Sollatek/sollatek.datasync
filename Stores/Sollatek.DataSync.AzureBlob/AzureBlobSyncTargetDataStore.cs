#nullable enable

using Sollatek.DataSync.Config;
using Sollatek.DataSync.Export;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobSyncTargetDataStore : ISyncTargetDataStore
{
    private readonly IBlobExportContainer _container;
    private readonly FileExportOptions _options;

    public AzureBlobSyncTargetDataStore(
        IBlobExportContainer container,
        FileExportOptions options)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<bool> HasStoredDataAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var prefix = AzureBlobExportPath.GetRootPrefix(_options);
        await foreach (var blobName in _container.ListAsync(prefix, cancellationToken))
        {
            var relativePath = AzureBlobExportPath.ToRelativePath(_options, blobName);
            if (relativePath is not null &&
                DailyExportPath.IsEntityExportRelativePath(_options, metadata.Key, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    public Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<DateTimeOffset?>(null);
    }
}
