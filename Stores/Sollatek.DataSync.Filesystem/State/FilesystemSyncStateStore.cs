#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.State;

public sealed class FilesystemSyncStateStore : ISyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FilesystemSyncStateStore(FileExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = Path.Combine(options.RootPath, "_state", "sync-state.json");
    }

    public async Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
        string entityKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            return state.Entities.TryGetValue(entityKey, out var entityState)
                ? entityState.LastSuccessfulEndUtc
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSuccessfulEndAsync(
        string entityKey,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var state = await ReadStateAsync(cancellationToken);
            state.Entities[entityKey] = new FilesystemSyncEntityState(
                end.ToUniversalTime(),
                DateTimeOffset.UtcNow);
            await WriteStateAsync(state, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<FilesystemSyncStateDocument> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new FilesystemSyncStateDocument(
                new Dictionary<string, FilesystemSyncEntityState>(StringComparer.OrdinalIgnoreCase));
        }

        await using var stream = File.OpenRead(_path);
        var state = await JsonSerializer.DeserializeAsync<FilesystemSyncStateDocument>(
            stream,
            JsonOptions,
            cancellationToken);

        if (state?.Entities is null)
        {
            throw new InvalidOperationException($"Filesystem sync state file '{_path}' is invalid.");
        }

        return state with
        {
            Entities = new Dictionary<string, FilesystemSyncEntityState>(
                state.Entities,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task WriteStateAsync(
        FilesystemSyncStateDocument state,
        CancellationToken cancellationToken)
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
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
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

    private sealed record FilesystemSyncStateDocument(
        Dictionary<string, FilesystemSyncEntityState> Entities);

    private sealed record FilesystemSyncEntityState(
        DateTimeOffset LastSuccessfulEndUtc,
        DateTimeOffset UpdatedAtUtc);
}
