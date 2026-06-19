#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public sealed record SwaggerSyncEntityMetadata
{
    public required string Key { get; init; }

    public string? Schema { get; init; }

    public string? OperationId { get; init; }

    public required IReadOnlyList<string> OperationIds { get; init; }

    public IReadOnlyList<SwaggerSyncOperationMetadata> Operations { get; init; } = [];

    public string? MetadataSource { get; init; }

    public string? Table { get; init; }

    public string? Collection { get; init; }

    public required IReadOnlyList<string> PrimaryKey { get; init; }

    public IReadOnlyList<SwaggerSyncScalarFieldMetadata> ScalarFields { get; init; } = [];

    public SwaggerSyncWatermarkMetadata? Watermark { get; init; }

    public required IReadOnlyList<SwaggerSyncReferenceMetadata> References { get; init; }

    public required IReadOnlyList<string> DocumentNames { get; init; }
}
