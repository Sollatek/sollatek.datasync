#nullable enable

using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Config;

public sealed record SyncPlanOptions
{
    public static SyncPlanOptions Default { get; } = new()
    {
        Entities = new Dictionary<string, SyncPlanEntityOptions>(StringComparer.OrdinalIgnoreCase)
    };

    public required IReadOnlyDictionary<string, SyncPlanEntityOptions> Entities { get; init; }

    public SyncPlanEntityOptions GetEntityOptions(string entityKey)
    {
        return SyncEntityOptionMatcher.GetEntityOptions(
            Entities,
            entityKey,
            SyncPlanEntityOptions.Default,
            "SyncPlan options");
    }

    public SyncPlanEntityOptions GetEntityOptions(SwaggerSyncEntityMetadata metadata)
    {
        return SyncEntityOptionMatcher.GetEntityOptions(
            Entities,
            metadata,
            SyncPlanEntityOptions.Default,
            "SyncPlan options");
    }

    public static SyncPlanOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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
                Initial = ReadInitialMode(entry.OptionsSection)
            };
        }

        return new SyncPlanOptions { Entities = entities };
    }

    private static SyncInitialDataMode ReadInitialMode(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("initial");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return SyncInitialDataMode.Differential;
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
