#nullable enable

namespace Sollatek.DataSync.AzureBlob;

public interface IBlobStateContainer
{
    Task UploadAsync(
        string blobName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(
        string blobName,
        CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(
        string blobName,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> ListAsync(
        string prefix,
        CancellationToken cancellationToken);
}
