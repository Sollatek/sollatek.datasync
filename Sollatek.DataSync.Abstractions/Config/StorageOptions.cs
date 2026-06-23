#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record StorageOptions
{
    private const string SupportedProviders = "sqlserver, postgres, mysql, mongo, filesystem, azureBlobStorage";

    public StorageProvider Provider { get; init; } = StorageProvider.SqlServer;

    public string? ConnectionString { get; init; }

    public string? ContainerName { get; init; }

    public AzureBlobAuthenticationMode BlobAuthentication { get; init; } =
        AzureBlobAuthenticationMode.ConnectionString;

    public string? AccountName { get; init; }

    public string? BlobServiceUri { get; init; }

    public string? ContainerUri { get; init; }

    public string? ManagedIdentityClientId { get; init; }

    public StorageSchemaMode SchemaMode { get; init; } = StorageSchemaMode.Validate;

    public static StorageOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = GetProvider(configuration);
        var connectionString = GetConnectionString(configuration);
        if (provider != StorageProvider.Filesystem &&
            provider != StorageProvider.AzureBlob &&
            string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Storage:connectionString must be configured for non-filesystem storage providers. Settings:dbConnection is still accepted as a legacy fallback.");
        }

        var containerName = GetContainerName(configuration);
        var containerUri = GetTrimmed(configuration, "Storage:containerUri");
        var blobAuthentication = GetBlobAuthentication(configuration, connectionString, containerUri);
        if (provider == StorageProvider.AzureBlob)
        {
            ValidateAzureBlobOptions(
                blobAuthentication,
                connectionString,
                containerName,
                GetTrimmed(configuration, "Storage:accountName"),
                GetTrimmed(configuration, "Storage:blobServiceUri"),
                containerUri);
        }

        return new StorageOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
            ContainerName = containerName,
            BlobAuthentication = blobAuthentication,
            AccountName = GetTrimmed(configuration, "Storage:accountName"),
            BlobServiceUri = GetTrimmed(configuration, "Storage:blobServiceUri"),
            ContainerUri = containerUri,
            ManagedIdentityClientId = GetTrimmed(configuration, "Storage:managedIdentityClientId"),
            SchemaMode = GetSchemaMode(configuration)
        };
    }

    private static StorageProvider GetProvider(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("Storage:provider");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return StorageProvider.SqlServer;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "sqlserver" => StorageProvider.SqlServer,
            "postgres" => StorageProvider.Postgres,
            "mysql" => StorageProvider.MySql,
            "mongo" => StorageProvider.Mongo,
            "filesystem" => StorageProvider.Filesystem,
            "azureblob" => StorageProvider.AzureBlob,
            "azureblobstorage" => StorageProvider.AzureBlob,
            "blob" => StorageProvider.AzureBlob,
            _ => throw new InvalidOperationException(
                $"Unknown storage provider '{configuredValue}'. Supported providers: {SupportedProviders}.")
        };
    }

    private static string? GetConnectionString(IConfiguration configuration)
    {
        var storageConnection = configuration.GetValue<string>("Storage:connectionString");
        if (!string.IsNullOrWhiteSpace(storageConnection))
        {
            return storageConnection;
        }

        var namedConnection = configuration.GetConnectionString("DataSync");
        if (!string.IsNullOrWhiteSpace(namedConnection))
        {
            return namedConnection;
        }

        var legacyConnection = configuration.GetValue<string>("Settings:dbConnection");
        return string.IsNullOrWhiteSpace(legacyConnection) ? null : legacyConnection;
    }

    private static string? GetContainerName(IConfiguration configuration)
    {
        var containerName = configuration.GetValue<string>("Storage:containerName");
        return string.IsNullOrWhiteSpace(containerName) ? null : containerName.Trim();
    }

    private static string? GetTrimmed(IConfiguration configuration, string key)
    {
        var value = configuration.GetValue<string>(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static AzureBlobAuthenticationMode GetBlobAuthentication(
        IConfiguration configuration,
        string? connectionString,
        string? containerUri)
    {
        var configuredValue = configuration.GetValue<string>("Storage:authentication");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                return AzureBlobAuthenticationMode.ConnectionString;
            }

            return string.IsNullOrWhiteSpace(containerUri)
                ? AzureBlobAuthenticationMode.DefaultAzureCredential
                : AzureBlobAuthenticationMode.ContainerUri;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "connectionstring" => AzureBlobAuthenticationMode.ConnectionString,
            "connection-string" => AzureBlobAuthenticationMode.ConnectionString,
            "sas" => AzureBlobAuthenticationMode.ContainerUri,
            "containeruri" => AzureBlobAuthenticationMode.ContainerUri,
            "container-uri" => AzureBlobAuthenticationMode.ContainerUri,
            "defaultazurecredential" => AzureBlobAuthenticationMode.DefaultAzureCredential,
            "default-azure-credential" => AzureBlobAuthenticationMode.DefaultAzureCredential,
            "managedidentity" => AzureBlobAuthenticationMode.DefaultAzureCredential,
            "managed-identity" => AzureBlobAuthenticationMode.DefaultAzureCredential,
            _ => throw new InvalidOperationException(
                "Storage:authentication must be one of: connectionString, containerUri, defaultAzureCredential.")
        };
    }

    private static void ValidateAzureBlobOptions(
        AzureBlobAuthenticationMode authentication,
        string? connectionString,
        string? containerName,
        string? accountName,
        string? blobServiceUri,
        string? containerUri)
    {
        switch (authentication)
        {
            case AzureBlobAuthenticationMode.ConnectionString:
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        "Storage:connectionString must be configured when Azure Blob Storage:authentication is connectionString.");
                }

                if (string.IsNullOrWhiteSpace(containerName))
                {
                    throw new InvalidOperationException("Storage:containerName must be configured for Azure Blob storage.");
                }

                return;

            case AzureBlobAuthenticationMode.ContainerUri:
                if (string.IsNullOrWhiteSpace(containerUri))
                {
                    throw new InvalidOperationException(
                        "Storage:containerUri must be configured when Azure Blob Storage:authentication is containerUri.");
                }

                return;

            case AzureBlobAuthenticationMode.DefaultAzureCredential:
                if (string.IsNullOrWhiteSpace(containerUri) &&
                    (string.IsNullOrWhiteSpace(containerName) ||
                     (string.IsNullOrWhiteSpace(accountName) && string.IsNullOrWhiteSpace(blobServiceUri))))
                {
                    throw new InvalidOperationException(
                        "Storage:accountName, Storage:blobServiceUri, or Storage:containerUri must be configured for Azure Blob defaultAzureCredential authentication.");
                }

                return;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Azure Blob authentication mode '{authentication}'.");
        }
    }

    private static StorageSchemaMode GetSchemaMode(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("Storage:schemaMode");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return StorageSchemaMode.Validate;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "validate" => StorageSchemaMode.Validate,
            "applysafechanges" => StorageSchemaMode.ApplySafeChanges,
            "apply-safe-changes" => StorageSchemaMode.ApplySafeChanges,
            _ => throw new InvalidOperationException(
                "Storage:schemaMode must be one of: validate, applySafeChanges.")
        };
    }
}
