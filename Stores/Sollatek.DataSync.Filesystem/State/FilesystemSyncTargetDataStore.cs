#nullable enable

using Sollatek.DataSync.Config;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.State;

public sealed class FilesystemSyncTargetDataStore : ISyncTargetDataStore
{
    private readonly FileExportOptions _options;

    public FilesystemSyncTargetDataStore(FileExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<bool> HasStoredDataAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!IsSafeFolderName(metadata.Key))
        {
            return Task.FromResult(false);
        }

        var directory = Path.Combine(_options.RootPath, metadata.Key);
        var hasData = Directory.Exists(directory) &&
                      Directory.EnumerateFiles(directory, "*.parquet", SearchOption.AllDirectories).Any();

        return Task.FromResult(hasData);
    }

    public Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<DateTimeOffset?>(null);
    }

    private static bool IsSafeFolderName(string value)
    {
        return value.All(character =>
            char.IsLetterOrDigit(character) ||
            character == '_' ||
            character == '-');
    }
}
