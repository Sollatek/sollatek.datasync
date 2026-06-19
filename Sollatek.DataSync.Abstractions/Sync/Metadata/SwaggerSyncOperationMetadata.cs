#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public sealed record SwaggerSyncOperationMetadata
{
    public required string OperationId { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string DocumentName { get; init; }
}
