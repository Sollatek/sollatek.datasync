#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public static class SyncPlanConfigurationReader
{
    public static IReadOnlyList<SyncPlanConfigurationEntry> Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var entities = configuration.GetValue<string>("SyncPlan:entities");
        if (!string.IsNullOrWhiteSpace(entities))
        {
            return entities
                .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(entity => new SyncPlanConfigurationEntry(entity, OptionsSection: null))
                .ToArray();
        }

        var entries = new List<SyncPlanConfigurationEntry>();
        foreach (var entrySection in configuration.GetSection("SyncPlan").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entrySection.Value))
            {
                entries.Add(new SyncPlanConfigurationEntry(entrySection.Value, OptionsSection: null));
                continue;
            }

            var entitySections = entrySection.GetChildren().ToArray();
            if (entitySections.Length == 0)
            {
                continue;
            }

            if (entitySections.Length != 1)
            {
                throw new InvalidOperationException(
                    "SyncPlan object entries must contain exactly one entity key.");
            }

            var entitySection = entitySections[0];
            if (string.IsNullOrWhiteSpace(entitySection.Key))
            {
                throw new InvalidOperationException("SyncPlan contains an empty entity key.");
            }

            entries.Add(new SyncPlanConfigurationEntry(entitySection.Key, entitySection));
        }

        return entries;
    }
}
