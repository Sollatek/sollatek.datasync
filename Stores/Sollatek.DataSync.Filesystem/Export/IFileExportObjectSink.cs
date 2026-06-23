#nullable enable

namespace Sollatek.DataSync.Export;

public interface IFileExportObjectSink
{
    Task<string?> WriteAsync(
        string entityKey,
        DateOnly day,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int partNumber,
        CancellationToken cancellationToken);

    Task<string> CopyAsync(
        string entityKey,
        DateOnly day,
        string sourcePath,
        int partNumber,
        CancellationToken cancellationToken);

    Task<bool> ExistsAsync(
        string entityKey,
        DateOnly day,
        int partNumber,
        CancellationToken cancellationToken);
}
