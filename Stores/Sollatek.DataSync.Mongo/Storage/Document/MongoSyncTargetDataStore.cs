#nullable enable

using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage.Document;

public sealed class MongoSyncTargetDataStore : ISyncTargetDataStore
{
    private readonly IMongoDatabase _database;

    public MongoSyncTargetDataStore(IMongoDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> HasStoredDataAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var collectionName = !string.IsNullOrWhiteSpace(metadata.Collection)
            ? metadata.Collection
            : metadata.Key;
        var collection = _database.GetCollection<BsonDocument>(collectionName);
        var document = await collection
            .Find(Builders<BsonDocument>.Filter.Empty)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        return document is not null;
    }

    public async Task<DateTimeOffset?> GetLatestStoredWatermarkAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.Watermark == null)
        {
            return null;
        }

        var collectionName = !string.IsNullOrWhiteSpace(metadata.Collection)
            ? metadata.Collection
            : metadata.Key;
        var collection = _database.GetCollection<BsonDocument>(collectionName);
        var document = await collection
            .Find(Builders<BsonDocument>.Filter.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending(metadata.Watermark.Field))
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        if (document == null ||
            !TryReadPath(document, metadata.Watermark.Field, out var value))
        {
            return null;
        }

        return ReadDateTimeOffset(value, metadata.Key, metadata.Watermark.Field);
    }

    private static bool TryReadPath(
        BsonDocument source,
        string path,
        out BsonValue value)
    {
        value = source;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!value.IsBsonDocument ||
                !value.AsBsonDocument.TryGetValue(segment, out value))
            {
                value = BsonNull.Value;
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        BsonValue value,
        string entityKey,
        string watermarkField)
    {
        if (value.IsBsonNull)
        {
            return null;
        }

        if (value.IsBsonDateTime)
        {
            return new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero);
        }

        if (value.IsString &&
            DateTimeOffset.TryParse(
                value.AsString,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Stored watermark '{watermarkField}' for sync entity '{entityKey}' is not a valid date/time value.");
    }
}
