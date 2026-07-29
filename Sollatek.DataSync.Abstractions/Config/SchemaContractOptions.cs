#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record SchemaContractOptions
{
    public const string DefaultVersion = "1";

    public string Version { get; init; } = DefaultVersion;

    public static SchemaContractOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var version = configuration.GetValue<string>("Schema:version");
        return new SchemaContractOptions
        {
            Version = string.IsNullOrWhiteSpace(version)
                ? DefaultVersion
                : version.Trim()
        };
    }
}
