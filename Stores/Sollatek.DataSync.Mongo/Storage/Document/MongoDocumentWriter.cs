#nullable enable

using MongoDB.Bson;
using MongoDB.Driver;

namespace Sollatek.DataSync.Storage.Document;

public sealed class MongoDocumentWriter : IMongoDocumentWriter
{
    private readonly IMongoDatabase _database;

    public MongoDocumentWriter(IMongoDatabase database)
    {
        _database = database;
    }

    public Task UpsertAsync(
        string collectionName,
        BsonDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentNullException.ThrowIfNull(document);

        if (!document.TryGetValue("_id", out var id) || id.IsBsonNull)
        {
            throw new InvalidOperationException(
                $"MongoDB document for collection '{collectionName}' must contain a non-null _id.");
        }

        var collection = _database.GetCollection<BsonDocument>(collectionName);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);

        return collection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
