#nullable enable

namespace Sollatek.DataSync.Config;

public enum AzureBlobAuthenticationMode
{
    ConnectionString,
    ContainerUri,
    DefaultAzureCredential
}
