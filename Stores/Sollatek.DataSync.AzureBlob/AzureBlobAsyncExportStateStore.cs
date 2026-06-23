#nullable enable

using Sollatek.DataSync.Config;
using Sollatek.DataSync.Fetch;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobAsyncExportStateStore : IAsyncExportStateStore
{
    private const string StateFileExtension = ".json";

    private readonly IBlobStateContainer _container;
    private readonly StateOptions _options;

    public AzureBlobAsyncExportStateStore(
        IBlobStateContainer container,
        StateOptions options)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prefix = GetStatePrefix();
        await foreach (var blobName in _container.ListAsync(prefix, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!blobName.StartsWith(prefix, StringComparison.Ordinal) ||
                !blobName.EndsWith(StateFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativeName = blobName[prefix.Length..];
            if (relativeName.Contains('/', StringComparison.Ordinal) ||
                relativeName.Contains('\\', StringComparison.Ordinal))
            {
                continue;
            }

            yield return relativeName[..^StateFileExtension.Length];
        }
    }

    public Task<Stream?> OpenReadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        return _container.OpenReadAsync(GetStateBlobName(key), cancellationToken);
    }

    public Task SaveAsync(
        string key,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        return _container.UploadAsync(
            GetStateBlobName(key),
            content,
            "application/json",
            cancellationToken);
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        return _container.DeleteIfExistsAsync(GetStateBlobName(key), cancellationToken);
    }

    private string GetStatePrefix()
    {
        return $"{AzureBlobStatePath.Combine(_options.RootPath, "async-exports", "state")}/";
    }

    private string GetStateBlobName(string key)
    {
        return $"{GetStatePrefix()}{AzureBlobStatePath.ValidateDocumentKey(key)}{StateFileExtension}";
    }
}
