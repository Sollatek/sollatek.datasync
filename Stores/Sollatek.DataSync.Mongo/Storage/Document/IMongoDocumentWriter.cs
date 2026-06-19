#nullable enable

using MongoDB.Bson;

namespace Sollatek.DataSync.Storage.Document;

public interface IMongoDocumentWriter
{
    Task UpsertAsync(
        string collectionName,
        BsonDocument document,
        CancellationToken cancellationToken);
}
