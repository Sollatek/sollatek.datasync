#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public sealed record SwaggerSyncSchemaMetadata
{
    public required string SchemaName { get; init; }

    public string? Schema { get; init; }

    public string? Type { get; init; }

    public string? OperationId { get; init; }

    public required IReadOnlyList<string> OperationIds { get; init; }

    public IReadOnlyList<SwaggerSyncOperationMetadata> Operations { get; init; } = [];

    public string? MetadataSource { get; init; }

    public required IReadOnlyList<string> PrimaryKey { get; init; }

    public IReadOnlyList<SwaggerSyncScalarFieldMetadata> ScalarFields { get; init; } = [];

    public SwaggerSyncWatermarkMetadata? Watermark { get; init; }

    public required IReadOnlyList<SwaggerSyncReferenceMetadata> References { get; init; }

    public required IReadOnlyList<string> DocumentNames { get; init; }
}
