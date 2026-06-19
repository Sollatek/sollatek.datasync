#nullable enable

using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Sollatek.DataSync.Storage.Document;

public sealed class MongoSchemaManifestStore : IMongoSchemaManifestStore
{
    public const string CollectionName = "__sollatek_datasync_schema_manifest";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoSchemaManifestStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _collection = database.GetCollection<BsonDocument>(CollectionName);
    }

    public async Task<bool> IsCurrentAsync(
        SchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var filter = Builders<BsonDocument>.Filter.Eq("_id", "current")
                     & Builders<BsonDocument>.Filter.Eq("version", manifest.Version)
                     & Builders<BsonDocument>.Filter.Eq("provider", manifest.Provider.ToString())
                     & Builders<BsonDocument>.Filter.Eq("syncPlanHash", manifest.SyncPlanHash)
                     & Builders<BsonDocument>.Filter.Eq("schemaHash", manifest.SchemaHash);

        var document = await _collection
            .Find(filter)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        return document != null;
    }

    public Task SaveAsync(
        SchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var document = new BsonDocument
        {
            ["_id"] = "current",
            ["version"] = manifest.Version,
            ["provider"] = manifest.Provider.ToString(),
            ["syncPlanHash"] = manifest.SyncPlanHash,
            ["schemaHash"] = manifest.SchemaHash,
            ["appliedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var filter = Builders<BsonDocument>.Filter.Eq("_id", "current");
        return _collection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
