#nullable enable

using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Config;

public sealed record SyncPlanOptions
{
    public static SyncPlanOptions Default { get; } = new()
    {
        DefaultEntityOptions = SyncPlanEntityOptions.Default,
        Entities = new Dictionary<string, SyncPlanEntityOptions>(StringComparer.OrdinalIgnoreCase)
    };

    public SyncPlanEntityOptions DefaultEntityOptions { get; init; } = SyncPlanEntityOptions.Default;

    public required IReadOnlyDictionary<string, SyncPlanEntityOptions> Entities { get; init; }

    public SyncPlanEntityOptions GetEntityOptions(string entityKey)
    {
        return SyncEntityOptionMatcher.GetEntityOptions(
            Entities,
            entityKey,
            DefaultEntityOptions,
            "SyncPlan options");
    }

    public SyncPlanEntityOptions GetEntityOptions(SwaggerSyncEntityMetadata metadata)
    {
        return SyncEntityOptionMatcher.GetEntityOptions(
            Entities,
            metadata,
            DefaultEntityOptions,
            "SyncPlan options");
    }

    public static SyncPlanOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaultEntityOptions = new SyncPlanEntityOptions
        {
            Initial = ReadInitialMode(configuration.GetSection("Sync"), defaultValue: SyncInitialDataMode.Differential)
        };
        var entities = new Dictionary<string, SyncPlanEntityOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in SyncPlanConfigurationReader.Read(configuration))
        {
            if (entry.OptionsSection == null)
            {
                continue;
            }

            SyncEntityOptionMatcher.RemoveNormalizedMatch(entities, entry.EntityKey);
            entities[entry.EntityKey] = new SyncPlanEntityOptions
            {
                Initial = ReadInitialMode(entry.OptionsSection, defaultEntityOptions.Initial)
            };
        }

        return new SyncPlanOptions
        {
            DefaultEntityOptions = defaultEntityOptions,
            Entities = entities
        };
    }

    private static SyncInitialDataMode ReadInitialMode(
        IConfiguration configuration,
        SyncInitialDataMode defaultValue)
    {
        var configuredValue = configuration.GetValue<string>("initial");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "differential" => SyncInitialDataMode.Differential,
            "full" => SyncInitialDataMode.Full,
            _ => throw new InvalidOperationException(
                "SyncPlan initial must be one of: differential, full.")
        };
    }
}

public sealed record SyncPlanEntityOptions
{
    public static SyncPlanEntityOptions Default { get; } = new();

    public SyncInitialDataMode Initial { get; init; } = SyncInitialDataMode.Differential;
}

public enum SyncInitialDataMode
{
    Differential,
    Full
}
