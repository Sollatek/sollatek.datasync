#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Sql;

public sealed class SqlDataSyncStorageProvider : IDataSyncStorageProvider
{
    public IReadOnlyCollection<StorageProvider> SupportedProviders { get; } =
    [
        StorageProvider.SqlServer,
        StorageProvider.Postgres,
        StorageProvider.MySql
    ];

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<RelationalMetadataSyncRunner>();
        services.AddSingleton<IRelationalSyncSink>(sp =>
        {
            var options = sp.GetRequiredService<StorageOptions>();
            if (options.Provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
            {
                throw new InvalidOperationException(
                    $"The configured storage provider '{options.Provider}' does not use the relational metadata sink.");
            }

            return new RelationalSyncSink(
                options.Provider,
                options.ConnectionString
                ?? throw new InvalidOperationException("Storage:connectionString must be configured for relational storage."),
                options.SchemaMode);
        });
        services.AddSingleton<RelationalSyncStateStore>(sp =>
        {
            var options = sp.GetRequiredService<StorageOptions>();
            if (options.Provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
            {
                throw new InvalidOperationException(
                    $"The configured storage provider '{options.Provider}' does not use the relational sync state store.");
            }

            return new RelationalSyncStateStore(
                options.Provider,
                options.ConnectionString
                ?? throw new InvalidOperationException("Storage:connectionString must be configured for relational storage."));
        });
        services.AddSingleton<RelationalSyncTargetDataStore>(sp =>
        {
            var options = sp.GetRequiredService<StorageOptions>();
            if (options.Provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
            {
                throw new InvalidOperationException(
                    $"The configured storage provider '{options.Provider}' does not use the relational sync target data store.");
            }

            return new RelationalSyncTargetDataStore(
                options.Provider,
                options.ConnectionString
                ?? throw new InvalidOperationException("Storage:connectionString must be configured for relational storage."));
        });
    }

    public ISyncJobRunner ResolveRunner(IServiceProvider services)
    {
        return services.GetRequiredService<RelationalMetadataSyncRunner>();
    }

    public ISyncStateStore ResolveStateStore(IServiceProvider services)
    {
        return services.GetRequiredService<RelationalSyncStateStore>();
    }

    public ISyncTargetDataStore ResolveTargetDataStore(IServiceProvider services)
    {
        return services.GetRequiredService<RelationalSyncTargetDataStore>();
    }
}
