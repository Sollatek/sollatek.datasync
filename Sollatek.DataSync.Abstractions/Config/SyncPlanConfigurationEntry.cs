#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record SyncPlanConfigurationEntry(
    string EntityKey,
    IConfigurationSection? OptionsSection);
