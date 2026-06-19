#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public sealed record SwaggerSyncReferenceMetadata
{
    public required string Source { get; init; }

    public required string LocalColumn { get; init; }

    public required string TargetEntity { get; init; }

    public required string TargetKey { get; init; }

    public string? Nullability { get; init; }

    public string? Enforce { get; init; }

    public string? OnDelete { get; init; }

    public required IReadOnlyList<string> FlatFallbackColumns { get; init; }
}
