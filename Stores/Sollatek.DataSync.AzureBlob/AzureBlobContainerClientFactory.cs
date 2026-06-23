#nullable enable

using Azure.Identity;
using Azure.Storage.Blobs;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.AzureBlob;

public static class AzureBlobContainerClientFactory
{
    public static BlobContainerClient Create(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.BlobAuthentication switch
        {
            AzureBlobAuthenticationMode.ConnectionString => CreateFromConnectionString(options),
            AzureBlobAuthenticationMode.ContainerUri => CreateFromContainerUri(options),
            AzureBlobAuthenticationMode.DefaultAzureCredential => CreateFromDefaultAzureCredential(options),
            _ => throw new InvalidOperationException(
                $"Unsupported Azure Blob authentication mode '{options.BlobAuthentication}'.")
        };
    }

    private static BlobContainerClient CreateFromConnectionString(StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("Storage:connectionString must be configured for Azure Blob connection string authentication.");
        }

        if (string.IsNullOrWhiteSpace(options.ContainerName))
        {
            throw new InvalidOperationException("Storage:containerName must be configured for Azure Blob connection string authentication.");
        }

        return new BlobContainerClient(options.ConnectionString, options.ContainerName);
    }

    private static BlobContainerClient CreateFromContainerUri(StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ContainerUri))
        {
            throw new InvalidOperationException("Storage:containerUri must be configured for Azure Blob containerUri authentication.");
        }

        return new BlobContainerClient(new Uri(options.ContainerUri, UriKind.Absolute));
    }

    private static BlobContainerClient CreateFromDefaultAzureCredential(StorageOptions options)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;
        }

        return new BlobContainerClient(
            BuildContainerUri(options),
            new DefaultAzureCredential(credentialOptions));
    }

    private static Uri BuildContainerUri(StorageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ContainerUri))
        {
            return new Uri(options.ContainerUri, UriKind.Absolute);
        }

        if (string.IsNullOrWhiteSpace(options.ContainerName))
        {
            throw new InvalidOperationException("Storage:containerName must be configured for Azure Blob identity authentication.");
        }

        if (!string.IsNullOrWhiteSpace(options.BlobServiceUri))
        {
            return new Uri(
                $"{options.BlobServiceUri.TrimEnd('/')}/{options.ContainerName}",
                UriKind.Absolute);
        }

        if (!string.IsNullOrWhiteSpace(options.AccountName))
        {
            return new Uri(
                $"https://{options.AccountName}.blob.core.windows.net/{options.ContainerName}",
                UriKind.Absolute);
        }

        throw new InvalidOperationException(
            "Storage:accountName, Storage:blobServiceUri, or Storage:containerUri must be configured for Azure Blob identity authentication.");
    }
}
