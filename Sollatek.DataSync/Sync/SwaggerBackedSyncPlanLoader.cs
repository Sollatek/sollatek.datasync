#nullable enable

using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Sync;

public static class SwaggerBackedSyncPlanLoader
{
    public static async Task<SwaggerBackedSyncPlan> LoadAsync(
        HttpClient httpClient,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(configuration);

        var documents = SwaggerSyncDocumentOptionsReader.FromConfiguration(configuration);
        var registry = await SwaggerSyncMetadataLoader.LoadAsync(httpClient, documents, cancellationToken);

        return SwaggerBackedSyncPlanResolver.Resolve(
            registry,
            configuration.GetSyncPlan());
    }
}
