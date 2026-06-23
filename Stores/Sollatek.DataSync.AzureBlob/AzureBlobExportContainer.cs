#nullable enable

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Sollatek.DataSync.AzureBlob;

public sealed class AzureBlobExportContainer : IBlobExportContainer
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private bool _created;

    public AzureBlobExportContainer(BlobContainerClient container)
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
        var uploadOptions = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions
            {
                IfNoneMatch = Azure.ETag.All
            }
        };
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            uploadOptions.HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            };
        }

        await _container.GetBlobClient(blobName)
            .UploadAsync(content, uploadOptions, cancellationToken);
    }

    public async Task<bool> ExistsAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        await EnsureContainerAsync(cancellationToken);
        var response = await _container.GetBlobClient(blobName)
            .ExistsAsync(cancellationToken);
        return response.Value;
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
