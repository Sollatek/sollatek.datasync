#nullable enable

namespace Sollatek.DataSync.Fetch;

public interface IAsyncExportStateStore
{
    IAsyncEnumerable<string> ListKeysAsync(CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken);

    Task SaveAsync(
        string key,
        Stream content,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string key,
        CancellationToken cancellationToken);
}
