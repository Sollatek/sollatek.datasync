#nullable enable

using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record StorageOptions
{
    private const string SupportedProviders = "sqlserver, postgres, mysql, mongo, filesystem";

    public StorageProvider Provider { get; init; } = StorageProvider.SqlServer;

    public string? ConnectionString { get; init; }

    public StorageSchemaMode SchemaMode { get; init; } = StorageSchemaMode.Validate;

    public static StorageOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = GetProvider(configuration);
        var connectionString = GetConnectionString(configuration);
        if (provider != StorageProvider.Filesystem && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Storage:connectionString must be configured for database storage providers. Settings:dbConnection is still accepted as a legacy fallback.");
        }

        return new StorageOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
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
