#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public static class SwaggerSyncMetadataLoader
{
    public static async Task<SwaggerSyncMetadataRegistry> LoadAsync(
        HttpClient httpClient,
        IEnumerable<SwaggerSyncDocumentOptions> documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(documents);

        var sources = new List<SwaggerSyncDocumentSource>();
        foreach (var document in documents)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, document.Url);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Failed to load swagger document '{document.Name}' from '{document.Url}'. HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            sources.Add(new SwaggerSyncDocumentSource(document.Name, json));
        }

        return SwaggerSyncMetadataRegistry.Load(sources);
    }
}
