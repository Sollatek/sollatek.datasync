#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public sealed record SwaggerSyncScalarFieldMetadata
{
    public required string Source { get; init; }

    public required string LocalColumn { get; init; }

    public required string Type { get; init; }

    public string? Format { get; init; }

    public bool IsNullable { get; init; }
}
