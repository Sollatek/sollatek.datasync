#nullable enable

using Azure.Storage.Blobs;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobStateContainer : IBlobStateContainer
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private bool _created;

    public AzureBlobStateContainer(BlobContainerClient container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    public async Task UploadAsync(
        string blobName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(content);

        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(blobName)
            .UploadAsync(content, overwrite: true, cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        await EnsureContainerAsync(cancellationToken);
        var blob = _container.GetBlobClient(blobName);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var response = await blob.DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToStream();
    }

    public async Task DeleteIfExistsAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        await EnsureContainerAsync(cancellationToken);
        await _container.GetBlobClient(blobName)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<string> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        await foreach (var blob in _container.GetBlobsAsync(
                           prefix: string.IsNullOrWhiteSpace(prefix) ? null : prefix,
                           cancellationToken: cancellationToken))
        {
            yield return blob.Name;
        }
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_created)
        {
            return;
        }

        await _createLock.WaitAsync(cancellationToken);
        try
        {
            if (_created)
            {
                return;
            }

            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _created = true;
        }
        finally
        {
            _createLock.Release();
        }
    }
}
