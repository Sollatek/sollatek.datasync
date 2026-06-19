#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Sync;

public sealed record SwaggerBackedSyncPlan(
    IReadOnlyList<SwaggerSyncEntityMetadata> MetadataEntities);
