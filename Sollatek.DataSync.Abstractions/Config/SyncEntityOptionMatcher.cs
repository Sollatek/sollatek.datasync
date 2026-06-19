#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Config;

internal static class SyncEntityOptionMatcher
{
    public static T GetEntityOptions<T>(
        IReadOnlyDictionary<string, T> entities,
        string entityKey,
        T defaultOptions,
        string optionDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        return GetEntityOptions(
            entities,
            [entityKey],
            defaultOptions,
            optionDescription);
    }

    public static T GetEntityOptions<T>(
        IReadOnlyDictionary<string, T> entities,
        SwaggerSyncEntityMetadata metadata,
        T defaultOptions,
        string optionDescription)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return GetEntityOptions(
            entities,
            BuildAliases(metadata),
            defaultOptions,
            optionDescription);
    }

    public static void RemoveNormalizedMatch<T>(
        IDictionary<string, T> entities,
        string entityKey)
    {
        var normalizedEntityKey = Normalize(entityKey);
        var existingKeys = entities.Keys
            .Where(key => string.Equals(Normalize(key), normalizedEntityKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var existingKey in existingKeys)
        {
            entities.Remove(existingKey);
        }
    }

    private static T GetEntityOptions<T>(
        IReadOnlyDictionary<string, T> entities,
        IEnumerable<string?> aliases,
        T defaultOptions,
        string optionDescription)
    {
        var aliasList = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias!)
            .ToArray();

        foreach (var alias in aliasList)
        {
            if (entities.TryGetValue(alias, out var options))
            {
                return options;
            }
        }

        var normalizedAliases = aliasList
            .Select(Normalize)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedMatches = entities
            .Where(entity => normalizedAliases.Contains(Normalize(entity.Key)))
            .Select(entity => entity.Value)
            .Distinct()
            .ToArray();

        return normalizedMatches.Length switch
        {
            0 => defaultOptions,
            1 => normalizedMatches[0],
            _ => throw new InvalidOperationException(
                $"{optionDescription} for sync entity '{aliasList[0]}' match multiple configured keys.")
        };
    }

    private static IEnumerable<string?> BuildAliases(SwaggerSyncEntityMetadata metadata)
    {
        yield return metadata.Key;
        yield return metadata.Table;
        yield return metadata.Collection;

        const string rawDataPrefix = "rawData";
        if (metadata.Key.StartsWith(rawDataPrefix, StringComparison.OrdinalIgnoreCase) &&
            metadata.Key.Length > rawDataPrefix.Length)
        {
            yield return metadata.Key[rawDataPrefix.Length..];
        }

        foreach (var operation in metadata.Operations)
        {
            yield return operation.Path;
            yield return TrimApiPrefix(operation.Path);
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
