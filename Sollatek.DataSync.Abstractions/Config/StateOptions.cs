#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record StateOptions
{
    private const string SupportedProviders = "filesystem, azureBlobStorage";

    public StateProvider Provider { get; init; } = StateProvider.Filesystem;

    public string RootPath { get; init; } = "_state";

    public static StateOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rootPath = configuration.GetValue<string>("State:rootPath");
        return new StateOptions
        {
            Provider = GetProvider(configuration),
            RootPath = string.IsNullOrWhiteSpace(rootPath) ? "_state" : TrimPath(rootPath)
        };
    }

    private static StateProvider GetProvider(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("State:provider");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return StateProvider.Filesystem;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "filesystem" => StateProvider.Filesystem,
            "file" => StateProvider.Filesystem,
            "files" => StateProvider.Filesystem,
            "azureblob" => StateProvider.AzureBlob,
            "azureblobstorage" => StateProvider.AzureBlob,
            "blob" => StateProvider.AzureBlob,
            _ => throw new InvalidOperationException(
                $"Unknown state provider '{configuredValue}'. Supported providers: {SupportedProviders}.")
        };
    }

    private static string TrimPath(string value)
    {
        return value.Trim().Trim('/', '\\');
    }
}
