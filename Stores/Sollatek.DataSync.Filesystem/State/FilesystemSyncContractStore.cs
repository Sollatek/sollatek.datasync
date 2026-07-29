#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Sync.Contract;

namespace Sollatek.DataSync.State;

public sealed class FilesystemSyncContractStore : ISyncContractStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FilesystemSyncContractStore(FileExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stateDirectory = Path.GetDirectoryName(options.StatePath)
                             ?? throw new InvalidOperationException(
                                 $"FileExport:statePath '{options.StatePath}' has no directory.");
        _path = Path.Combine(stateDirectory, "sync-contract.json");
    }

    public async Task<SyncContractSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            await using var stream = File.OpenRead(_path);
            var snapshot = await JsonSerializer.DeserializeAsync<SyncContractSnapshot>(
                stream,
                JsonOptions,
                cancellationToken);
            if (snapshot is null)
            {
                throw new InvalidOperationException(
                    $"Filesystem sync contract file '{_path}' is invalid.");
            }

            snapshot.Validate();
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(
        SyncContractSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 16 * 1024,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        snapshot,
                        JsonOptions,
                        cancellationToken);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
