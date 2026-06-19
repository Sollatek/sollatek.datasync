#nullable enable

using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using Sollatek.DataSync.State;

namespace Sollatek.DataSync.Storage.Document;

public sealed class MongoSyncStateStore : ISyncStateStore
{
    public const string CollectionName = "__sollatek_datasync_state";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoSyncStateStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _collection = database.GetCollection<BsonDocument>(CollectionName);
    }

    public async Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
        string entityKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        var filter = Builders<BsonDocument>.Filter.Eq("_id", entityKey);
        var document = await _collection
            .Find(filter)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        if (document is null || !document.TryGetValue("lastSuccessfulEndUtc", out var value))
        {
            return null;
        }

        var text = value.AsString;
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var end))
        {
            return end;
        }

        throw new InvalidOperationException(
            $"Stored sync state for entity '{entityKey}' has an invalid lastSuccessfulEndUtc value.");
    }

    public Task SaveSuccessfulEndAsync(
        string entityKey,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        var document = new BsonDocument
        {
            ["_id"] = entityKey,
            ["lastSuccessfulEndUtc"] = end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        var filter = Builders<BsonDocument>.Filter.Eq("_id", entityKey);
        return _collection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
