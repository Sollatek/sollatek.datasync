#nullable enable

namespace Sollatek.DataSync.Sync.Metadata;

public static class SwaggerSyncPlanResolver
{
    private const string RawDataPrefix = "rawData";

    public static IReadOnlyList<SwaggerSyncEntityMetadata> Resolve(
        SwaggerSyncMetadataRegistry registry,
        IEnumerable<string>? configuredKeys)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var keys = configuredKeys?.Select(x => x?.Trim() ?? string.Empty).ToArray();

        if (keys is not { Length: > 0 })
        {
            return registry.OrderedEntities;
        }

        var lookup = BuildLookup(registry.OrderedEntities);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<SwaggerSyncEntityMetadata>(keys.Length);

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("SyncPlan contains an empty entity key.");
            }

            var entity = ResolveEntity(registry, lookup, key);
            if (!seen.Add(entity.Key))
            {
                throw new InvalidOperationException(
                    $"Duplicate sync entity '{entity.Key}' in SyncPlan.");
            }

            resolved.Add(entity);
        }

        return resolved;
    }

    private static SwaggerSyncEntityMetadata ResolveEntity(
        SwaggerSyncMetadataRegistry registry,
        IReadOnlyDictionary<string, IReadOnlyList<SwaggerSyncEntityMetadata>> lookup,
        string key)
    {
        if (registry.Entities.TryGetValue(key, out var exactEntity))
        {
            return exactEntity;
        }

        var normalizedKey = Normalize(key);
        if (!string.IsNullOrWhiteSpace(normalizedKey) &&
            lookup.TryGetValue(normalizedKey, out var matchingEntities))
        {
            var uniqueMatches = matchingEntities
                .DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (uniqueMatches.Length == 1)
            {
                return uniqueMatches[0];
            }

            throw new InvalidOperationException(
                $"SyncPlan key '{key}' matches multiple sync entities: {string.Join(", ", uniqueMatches.Select(x => x.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");
        }

        throw new InvalidOperationException(
            $"Unknown sync entity '{key}' in SyncPlan. Use a swagger sync entity key or operation path. Available entities: {string.Join(", ", registry.Entities.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}.");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SwaggerSyncEntityMetadata>> BuildLookup(
        IReadOnlyList<SwaggerSyncEntityMetadata> entities)
    {
        var lookup = new Dictionary<string, List<SwaggerSyncEntityMetadata>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities)
        {
            AddLookupValue(lookup, entity.Key, entity);
            AddLookupValue(lookup, entity.Table, entity);
            AddLookupValue(lookup, entity.Collection, entity);

            if (entity.Key.StartsWith(RawDataPrefix, StringComparison.OrdinalIgnoreCase) &&
                entity.Key.Length > RawDataPrefix.Length)
            {
                AddLookupValue(lookup, entity.Key[RawDataPrefix.Length..], entity);
            }

            foreach (var operation in entity.Operations)
            {
                AddLookupValue(lookup, operation.Path, entity);
                AddLookupValue(lookup, TrimApiPrefix(operation.Path), entity);
            }
        }

        return lookup.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<SwaggerSyncEntityMetadata>)x.Value
                .DistinctBy(entity => entity.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddLookupValue(
        IDictionary<string, List<SwaggerSyncEntityMetadata>> lookup,
        string? value,
        SwaggerSyncEntityMetadata entity)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!lookup.TryGetValue(normalized, out var entities))
        {
            entities = [];
            lookup.Add(normalized, entities);
        }

        if (entities.All(x => !string.Equals(x.Key, entity.Key, StringComparison.OrdinalIgnoreCase)))
        {
            entities.Add(entity);
        }
    }

    private static string? TrimApiPrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('/');
        return trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
            ? trimmed[4..]
            : trimmed;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new char[value.Length];
        var index = 0;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                continue;
            }

            normalized[index] = char.ToLowerInvariant(character);
            index++;
        }

        return new string(normalized, 0, index);
    }
}
