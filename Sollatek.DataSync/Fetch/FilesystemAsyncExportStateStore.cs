#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Fetch;

public sealed class FilesystemAsyncExportStateStore : IAsyncExportStateStore
{
    private readonly AsyncExportOptions _options;

    public FilesystemAsyncExportStateStore(AsyncExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stateDirectory = GetStateDirectory();
        if (!Directory.Exists(stateDirectory))
        {
            yield break;
        }

        foreach (var statePath in Directory.EnumerateFiles(stateDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Path.GetFileNameWithoutExtension(statePath);
        }

        await Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var path = GetStatePath(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public async Task SaveAsync(
        string key,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = GetStatePath(key);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             useAsync: true))
            {
                await content.CopyToAsync(stream, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var path = GetStatePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetStateDirectory()
    {
        return Path.Combine(_options.StatePath, "state");
    }

    private string GetStatePath(string key)
    {
        return Path.Combine(GetStateDirectory(), $"{ValidateKey(key)}.json");
    }

    private static string ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            key.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            key.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Async export state key '{key}' is not a valid state file name.");
        }

        return key;
    }
}
