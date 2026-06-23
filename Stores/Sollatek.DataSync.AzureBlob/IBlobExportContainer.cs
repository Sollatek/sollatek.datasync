#nullable enable

namespace Sollatek.DataSync.AzureBlob;

public interface IBlobExportContainer
{
    Task UploadAsync(
        string blobName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken);

    Task<bool> ExistsAsync(
        string blobName,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> ListAsync(
        string prefix,
        CancellationToken cancellationToken);
}
