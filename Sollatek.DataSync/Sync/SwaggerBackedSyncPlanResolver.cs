#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Sync;

public static class SwaggerBackedSyncPlanResolver
{
    public static SwaggerBackedSyncPlan Resolve(
        SwaggerSyncMetadataRegistry registry,
        IEnumerable<string>? configuredKeys)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return new SwaggerBackedSyncPlan(
            SwaggerSyncPlanResolver.Resolve(registry, configuredKeys));
    }
}
