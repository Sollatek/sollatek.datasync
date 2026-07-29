#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Sync.Contract;

public sealed record SyncContractSnapshot
{
    private static readonly JsonSerializerOptions HashJsonOptions = new(JsonSerializerDefaults.Web);

    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string SchemaVersion { get; init; }

    public required string ContractHash { get; init; }

    public required DateTimeOffset AcceptedAtUtc { get; init; }

    public required IReadOnlyList<SwaggerSyncEntityMetadata> Entities { get; init; }

    public static SyncContractSnapshot Create(
        string schemaVersion,
        IReadOnlyList<SwaggerSyncEntityMetadata> entities,
        DateTimeOffset? acceptedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentNullException.ThrowIfNull(entities);

        var normalizedEntities = NormalizeEntities(entities);
        EnsureUniqueEntityKeys(normalizedEntities);

        return new SyncContractSnapshot
        {
            SchemaVersion = schemaVersion.Trim(),
            ContractHash = ComputeHash(normalizedEntities),
            AcceptedAtUtc = (acceptedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            Entities = normalizedEntities
        };
    }

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported DataSync contract snapshot format version '{FormatVersion}'. Expected '{CurrentFormatVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(SchemaVersion))
        {
            throw new InvalidOperationException("DataSync contract snapshot has no schema version.");
        }

        if (Entities is null)
        {
            throw new InvalidOperationException("DataSync contract snapshot has no entities.");
        }

        EnsureUniqueEntityKeys(Entities);

        var expectedHash = ComputeHash(NormalizeEntities(Entities));
        if (!string.Equals(ContractHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DataSync contract snapshot hash does not match its stored metadata.");
        }
    }

    private static IReadOnlyList<SwaggerSyncEntityMetadata> NormalizeEntities(
        IReadOnlyList<SwaggerSyncEntityMetadata> entities)
    {
        return entities
            .Select(entity => entity with
            {
                OperationIds = entity.OperationIds
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Operations = entity.Operations
                    .OrderBy(x => x.OperationId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Method, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.DocumentName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ScalarFields = entity.ScalarFields
                    .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.LocalColumn, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                References = entity.References
                    .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.LocalColumn, StringComparer.OrdinalIgnoreCase)
                    .Select(reference => reference with
                    {
                        FlatFallbackColumns = reference.FlatFallbackColumns
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    })
                    .ToArray(),
                DocumentNames = entity.DocumentNames
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .ToArray();
    }

    private static void EnsureUniqueEntityKeys(
        IReadOnlyList<SwaggerSyncEntityMetadata> entities)
    {
        var duplicate = entities
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"DataSync contract snapshot contains duplicate entity '{duplicate.Key}'.");
        }
    }

    private static string ComputeHash(
        IReadOnlyList<SwaggerSyncEntityMetadata> entities)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(entities, HashJsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
