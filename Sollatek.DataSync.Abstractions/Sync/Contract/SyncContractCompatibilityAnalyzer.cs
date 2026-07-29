#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Sync.Contract;

public static class SyncContractCompatibilityAnalyzer
{
    public static SyncContractCompatibilityResult Compare(
        SyncContractSnapshot accepted,
        SyncContractSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(candidate);
        accepted.Validate();
        candidate.Validate();

        if (string.Equals(
                accepted.ContractHash,
                candidate.ContractHash,
                StringComparison.OrdinalIgnoreCase))
        {
            return SyncContractCompatibilityResult.Unchanged;
        }

        var compatibleChanges = new List<string>();
        var breakingChanges = new List<string>();
        var acceptedEntities = accepted.Entities.ToDictionary(
            x => x.Key,
            StringComparer.OrdinalIgnoreCase);
        var candidateEntities = candidate.Entities.ToDictionary(
            x => x.Key,
            StringComparer.OrdinalIgnoreCase);

        if (!accepted.Entities
                .Select(x => x.Key)
                .SequenceEqual(
                    candidate.Entities.Select(x => x.Key),
                    StringComparer.OrdinalIgnoreCase) &&
            acceptedEntities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(candidateEntities.Keys))
        {
            breakingChanges.Add("Sync plan entity order changed.");
        }

        foreach (var removedEntity in acceptedEntities.Keys.Except(
                     candidateEntities.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            breakingChanges.Add($"Entity '{removedEntity}' was removed from the sync plan.");
        }

        foreach (var addedEntity in candidateEntities.Keys.Except(
                     acceptedEntities.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            breakingChanges.Add($"Entity '{addedEntity}' was added to the sync plan.");
        }

        foreach (var entityKey in acceptedEntities.Keys.Intersect(
                     candidateEntities.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            CompareEntity(
                acceptedEntities[entityKey],
                candidateEntities[entityKey],
                compatibleChanges,
                breakingChanges);
        }

        return new SyncContractCompatibilityResult(
            compatibleChanges,
            breakingChanges);
    }

    private static void CompareEntity(
        SwaggerSyncEntityMetadata accepted,
        SwaggerSyncEntityMetadata candidate,
        ICollection<string> compatibleChanges,
        ICollection<string> breakingChanges)
    {
        CompareValue(accepted.Key, "table", accepted.Table, candidate.Table, breakingChanges);
        CompareValue(accepted.Key, "collection", accepted.Collection, candidate.Collection, breakingChanges);
        CompareSequence(
            accepted.Key,
            "primary key",
            accepted.PrimaryKey,
            candidate.PrimaryKey,
            breakingChanges);
        CompareWatermark(accepted, candidate, breakingChanges);
        CompareOperations(accepted, candidate, breakingChanges);
        CompareScalarFields(accepted, candidate, compatibleChanges, breakingChanges);
        CompareReferences(accepted, candidate, compatibleChanges, breakingChanges);
    }

    private static void CompareWatermark(
        SwaggerSyncEntityMetadata accepted,
        SwaggerSyncEntityMetadata candidate,
        ICollection<string> breakingChanges)
    {
        if (accepted.Watermark is null && candidate.Watermark is null)
        {
            return;
        }

        if (accepted.Watermark is null || candidate.Watermark is null ||
            !Same(accepted.Watermark.Field, candidate.Watermark.Field) ||
            !SequenceEqual(accepted.Watermark.TieBreakers, candidate.Watermark.TieBreakers))
        {
            breakingChanges.Add($"Entity '{accepted.Key}' watermark changed.");
        }
    }

    private static void CompareOperations(
        SwaggerSyncEntityMetadata accepted,
        SwaggerSyncEntityMetadata candidate,
        ICollection<string> breakingChanges)
    {
        var acceptedOperations = accepted.Operations
            .Select(OperationSignature)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateOperations = candidate.Operations
            .Select(OperationSignature)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!SequenceEqual(acceptedOperations, candidateOperations) ||
            !Same(accepted.OperationId, candidate.OperationId) ||
            !SetEqual(accepted.OperationIds, candidate.OperationIds))
        {
            breakingChanges.Add($"Entity '{accepted.Key}' API operation changed.");
        }
    }

    private static void CompareScalarFields(
        SwaggerSyncEntityMetadata accepted,
        SwaggerSyncEntityMetadata candidate,
        ICollection<string> compatibleChanges,
        ICollection<string> breakingChanges)
    {
        var acceptedFields = accepted.ScalarFields.ToDictionary(
            x => x.Source,
            StringComparer.OrdinalIgnoreCase);
        var candidateFields = candidate.ScalarFields.ToDictionary(
            x => x.Source,
            StringComparer.OrdinalIgnoreCase);

        foreach (var removedSource in acceptedFields.Keys.Except(
                     candidateFields.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            breakingChanges.Add(
                $"Entity '{accepted.Key}' scalar field '{removedSource}' was removed or renamed.");
        }

        foreach (var addedSource in candidateFields.Keys.Except(
                     acceptedFields.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            var added = candidateFields[addedSource];
            var message = $"Entity '{accepted.Key}' scalar field '{addedSource}' was added.";
            if (added.IsNullable)
            {
                compatibleChanges.Add(message);
            }
            else
            {
                breakingChanges.Add($"{message} The new field is not nullable.");
            }
        }

        foreach (var source in acceptedFields.Keys.Intersect(
                     candidateFields.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            var oldField = acceptedFields[source];
            var newField = candidateFields[source];

            if (!Same(oldField.LocalColumn, newField.LocalColumn))
            {
                breakingChanges.Add(
                    $"Entity '{accepted.Key}' scalar field '{source}' local column changed from '{oldField.LocalColumn}' to '{newField.LocalColumn}'.");
            }

            if (!Same(oldField.Type, newField.Type) ||
                !Same(oldField.Format, newField.Format))
            {
                breakingChanges.Add(
                    $"Entity '{accepted.Key}' scalar field '{source}' type changed from '{DescribeType(oldField)}' to '{DescribeType(newField)}'.");
            }

            if (oldField.IsNullable && !newField.IsNullable)
            {
                breakingChanges.Add(
                    $"Entity '{accepted.Key}' scalar field '{source}' changed from nullable to required.");
            }
            else if (!oldField.IsNullable && newField.IsNullable)
            {
                compatibleChanges.Add(
                    $"Entity '{accepted.Key}' scalar field '{source}' changed from required to nullable.");
            }
        }
    }

    private static void CompareReferences(
        SwaggerSyncEntityMetadata accepted,
        SwaggerSyncEntityMetadata candidate,
        ICollection<string> compatibleChanges,
        ICollection<string> breakingChanges)
    {
        var acceptedReferences = accepted.References.ToDictionary(
            x => x.Source,
            StringComparer.OrdinalIgnoreCase);
        var candidateReferences = candidate.References.ToDictionary(
            x => x.Source,
            StringComparer.OrdinalIgnoreCase);

        foreach (var removedSource in acceptedReferences.Keys.Except(
                     candidateReferences.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            breakingChanges.Add(
                $"Entity '{accepted.Key}' reference '{removedSource}' was removed or renamed.");
        }

        foreach (var addedSource in candidateReferences.Keys.Except(
                     acceptedReferences.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            var added = candidateReferences[addedSource];
            var message = $"Entity '{accepted.Key}' reference '{addedSource}' was added.";
            if (IsNullable(added.Nullability))
            {
                compatibleChanges.Add(message);
            }
            else
            {
                breakingChanges.Add($"{message} The new reference is not explicitly nullable.");
            }
        }

        foreach (var source in acceptedReferences.Keys.Intersect(
                     candidateReferences.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            var oldReference = acceptedReferences[source];
            var newReference = candidateReferences[source];
            if (!Same(oldReference.LocalColumn, newReference.LocalColumn) ||
                !Same(oldReference.TargetEntity, newReference.TargetEntity) ||
                !Same(oldReference.TargetKey, newReference.TargetKey) ||
                !Same(oldReference.Enforce, newReference.Enforce) ||
                !Same(oldReference.OnDelete, newReference.OnDelete) ||
                !SetEqual(oldReference.FlatFallbackColumns, newReference.FlatFallbackColumns))
            {
                breakingChanges.Add(
                    $"Entity '{accepted.Key}' reference '{source}' storage contract changed.");
            }

            var wasNullable = IsNullable(oldReference.Nullability);
            var isNullable = IsNullable(newReference.Nullability);
            if (wasNullable && !isNullable)
            {
                breakingChanges.Add(
                    $"Entity '{accepted.Key}' reference '{source}' changed from nullable to required.");
            }
            else if (!wasNullable && isNullable)
            {
                compatibleChanges.Add(
                    $"Entity '{accepted.Key}' reference '{source}' changed from required to nullable.");
            }
        }
    }

    private static void CompareValue(
        string entityKey,
        string label,
        string? accepted,
        string? candidate,
        ICollection<string> breakingChanges)
    {
        if (!Same(accepted, candidate))
        {
            breakingChanges.Add(
                $"Entity '{entityKey}' {label} changed from '{accepted ?? "<none>"}' to '{candidate ?? "<none>"}'.");
        }
    }

    private static void CompareSequence(
        string entityKey,
        string label,
        IReadOnlyList<string> accepted,
        IReadOnlyList<string> candidate,
        ICollection<string> breakingChanges)
    {
        if (!SequenceEqual(accepted, candidate))
        {
            breakingChanges.Add($"Entity '{entityKey}' {label} changed.");
        }
    }

    private static string OperationSignature(SwaggerSyncOperationMetadata operation)
    {
        return string.Join(
            "\u001f",
            operation.OperationId,
            operation.Method,
            operation.Path);
    }

    private static string DescribeType(SwaggerSyncScalarFieldMetadata field)
    {
        return string.IsNullOrWhiteSpace(field.Format)
            ? field.Type
            : $"{field.Type}/{field.Format}";
    }

    private static bool IsNullable(string? value)
    {
        return string.Equals(value, "nullable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SequenceEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SetEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right);
    }
}

public sealed record SyncContractCompatibilityResult(
    IReadOnlyList<string> CompatibleChanges,
    IReadOnlyList<string> BreakingChanges)
{
    public static SyncContractCompatibilityResult Unchanged { get; } =
        new([], []);

    public bool IsCompatible => BreakingChanges.Count == 0;

    public bool IsUnchanged =>
        CompatibleChanges.Count == 0 &&
        BreakingChanges.Count == 0;
}
