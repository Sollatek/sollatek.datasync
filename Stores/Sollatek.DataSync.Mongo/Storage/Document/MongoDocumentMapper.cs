#nullable enable

using System.Text.Json;
using MongoDB.Bson;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage.Document;

public static class MongoDocumentMapper
{
    public static BsonDocument Map(
        SwaggerSyncEntityMetadata metadata,
        JsonElement source)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Cannot map sync entity '{metadata.Key}' to MongoDB because the API record is not a JSON object.");
        }

        var document = BsonDocument.Parse(source.GetRawText());
        document["_id"] = BuildId(metadata, source);

        return document;
    }

    private static BsonValue BuildId(
        SwaggerSyncEntityMetadata metadata,
        JsonElement source)
    {
        if (metadata.PrimaryKey.Count == 0)
        {
            throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' cannot be mapped to MongoDB because it has no primary key metadata.");
        }

        if (metadata.PrimaryKey.Count == 1)
        {
            return new BsonString(ReadRequiredKeyValue(metadata, source, metadata.PrimaryKey[0]));
        }

        var id = new BsonDocument();
        foreach (var keyPath in metadata.PrimaryKey)
        {
            id[keyPath.Replace('.', '_')] = ReadRequiredKeyValue(metadata, source, keyPath);
        }

        return id;
    }

    private static string ReadRequiredKeyValue(
        SwaggerSyncEntityMetadata metadata,
        JsonElement source,
        string path)
    {
        if (!TryReadPath(source, path, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' cannot be mapped to MongoDB because primary key path '{path}' is missing or null.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidOperationException(
                $"Sync entity '{metadata.Key}' primary key path '{path}' must be a scalar value for MongoDB mapping.")
        };
    }

    private static bool TryReadPath(JsonElement source, string path, out JsonElement value)
    {
        value = source;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }
}
