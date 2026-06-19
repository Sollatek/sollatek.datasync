#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public sealed record SwaggerSyncWatermarkMetadata
{
    public required string Field { get; init; }

    public required IReadOnlyList<string> TieBreakers { get; init; }
}
