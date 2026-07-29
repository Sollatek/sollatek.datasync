#nullable enable

using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Contract;

namespace Sollatek.DataSync.Storage.Document;

public sealed class MongoSyncContractStore : ISyncContractStore
{
    public const string CollectionName = "__sollatek_datasync_contract";

    private const string DocumentId = "accepted";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoSyncContractStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _collection = database.GetCollection<BsonDocument>(CollectionName);
    }

    public async Task<SyncContractSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", DocumentId);
        var document = await _collection
            .Find(filter)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return null;
        }

        if (!document.TryGetValue("snapshotJson", out var value) ||
            !value.IsString)
        {
            throw new InvalidOperationException(
                "Stored MongoDB sync contract has no snapshotJson value.");
        }

        var snapshot = JsonSerializer.Deserialize<SyncContractSnapshot>(
            value.AsString,
            JsonOptions);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                "Stored MongoDB sync contract is invalid.");
        }

        snapshot.Validate();
        return snapshot;
    }

    public Task SaveAsync(
        SyncContractSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        var document = new BsonDocument
        {
            ["_id"] = DocumentId,
            ["snapshotJson"] = JsonSerializer.Serialize(snapshot, JsonOptions)
        };
        var filter = Builders<BsonDocument>.Filter.Eq("_id", DocumentId);
        return _collection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
