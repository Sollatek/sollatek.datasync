#nullable enable

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Document;

namespace Sollatek.DataSync.Mongo;

public sealed class MongoDataSyncStorageProvider : IDataSyncStorageProvider
{
    public IReadOnlyCollection<StorageProvider> SupportedProviders { get; } =
    [
        StorageProvider.Mongo
    ];

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<IMongoDatabase>(sp => CreateMongoDatabase(sp.GetRequiredService<StorageOptions>()));
        services.AddSingleton<IMongoDocumentWriter>(sp =>
            new MongoDocumentWriter(sp.GetRequiredService<IMongoDatabase>()));
        services.AddSingleton<IMongoSchemaManifestStore>(sp =>
            new MongoSchemaManifestStore(sp.GetRequiredService<IMongoDatabase>()));
        services.AddSingleton<MongoSyncStateStore>(sp =>
            new MongoSyncStateStore(sp.GetRequiredService<IMongoDatabase>()));
        services.AddSingleton<MongoSyncTargetDataStore>(sp =>
            new MongoSyncTargetDataStore(sp.GetRequiredService<IMongoDatabase>()));
        services.AddSingleton<MongoSyncSink>();
        services.AddSingleton<IDocumentSyncSink>(sp => sp.GetRequiredService<MongoSyncSink>());
        services.AddSingleton<DocumentMetadataSyncRunner>();
    }

    public ISyncJobRunner ResolveRunner(IServiceProvider services)
    {
        return services.GetRequiredService<DocumentMetadataSyncRunner>();
    }

    public ISyncStateStore ResolveStateStore(IServiceProvider services)
    {
        return services.GetRequiredService<MongoSyncStateStore>();
    }

    public ISyncTargetDataStore ResolveTargetDataStore(IServiceProvider services)
    {
        return services.GetRequiredService<MongoSyncTargetDataStore>();
    }

    private static IMongoDatabase CreateMongoDatabase(StorageOptions options)
    {
        if (options.Provider != StorageProvider.Mongo)
        {
            throw new InvalidOperationException(
                $"The configured storage provider '{options.Provider}' does not use the MongoDB document sink.");
        }

        var connectionString = options.ConnectionString
                               ?? throw new InvalidOperationException("Storage:connectionString must be configured for MongoDB storage.");
        var mongoUrl = MongoUrl.Create(connectionString);
        if (string.IsNullOrWhiteSpace(mongoUrl.DatabaseName))
        {
            throw new InvalidOperationException(
                "MongoDB Storage:connectionString must include a database name, for example mongodb://localhost:27017/sollatek_datasync.");
        }

        var client = new MongoClient(mongoUrl);
        return client.GetDatabase(mongoUrl.DatabaseName);
    }
}
