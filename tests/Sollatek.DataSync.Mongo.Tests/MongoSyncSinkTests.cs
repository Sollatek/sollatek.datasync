using System.Text.Json;
using MongoDB.Bson;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Document;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class MongoSyncSinkTests
{
    [Fact]
    public async Task WriteAsync_MapsRowsAndUpsertsIntoMetadataCollection()
    {
        var writer = new RecordingMongoDocumentWriter();
        var sink = new MongoSyncSink(writer, new RecordingMongoSchemaManifestStore(isCurrent: true));

        await sink.WriteAsync(
            AssetMetadata(),
            Rows("""{ "id": 42, "name": "Asset 1" }"""),
            CancellationToken.None);

        var write = Assert.Single(writer.Writes);
        Assert.Equal("assets", write.Collection);
        Assert.Equal("42", write.Document["_id"].AsString);
        Assert.Equal("Asset 1", write.Document["name"].AsString);
    }

    [Fact]
    public async Task PrepareAsync_SkipsSaveWhenManifestMatches()
    {
        var manifestStore = new RecordingMongoSchemaManifestStore(isCurrent: true);
        var sink = new MongoSyncSink(new RecordingMongoDocumentWriter(), manifestStore);
        var manifest = AssetManifest();

        await sink.PrepareAsync(manifest, CancellationToken.None);

        Assert.Equal([manifest], manifestStore.CheckedManifests);
        Assert.Empty(manifestStore.SavedManifests);
    }

    [Fact]
    public async Task PrepareAsync_SavesChangedManifest()
    {
        var manifestStore = new RecordingMongoSchemaManifestStore(isCurrent: false);
        var sink = new MongoSyncSink(new RecordingMongoDocumentWriter(), manifestStore);
        var manifest = AssetManifest();

        await sink.PrepareAsync(manifest, CancellationToken.None);

        Assert.Equal([manifest], manifestStore.CheckedManifests);
        Assert.Equal([manifest], manifestStore.SavedManifests);
    }

    private static async IAsyncEnumerable<JsonElement> Rows(params string[] rows)
    {
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row);
            yield return document.RootElement.Clone();
        }

        await Task.CompletedTask;
    }

    private static SwaggerSyncEntityMetadata AssetMetadata()
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            Collection = "assets",
            OperationIds = ["Assets_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }

    private static SchemaManifest AssetManifest()
    {
        return new SchemaManifest(
            SchemaManifest.CurrentVersion,
            StorageProvider.Mongo,
            "sync-plan-hash",
            "schema-hash");
    }

    private sealed class RecordingMongoDocumentWriter : IMongoDocumentWriter
    {
        public List<(string Collection, BsonDocument Document)> Writes { get; } = [];

        public Task UpsertAsync(
            string collectionName,
            BsonDocument document,
            CancellationToken cancellationToken)
        {
            Writes.Add((collectionName, document));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMongoSchemaManifestStore(bool isCurrent) : IMongoSchemaManifestStore
    {
        public List<SchemaManifest> CheckedManifests { get; } = [];

        public List<SchemaManifest> SavedManifests { get; } = [];

        public Task<bool> IsCurrentAsync(
            SchemaManifest manifest,
            CancellationToken cancellationToken)
        {
            CheckedManifests.Add(manifest);
            return Task.FromResult(isCurrent);
        }

        public Task SaveAsync(
            SchemaManifest manifest,
            CancellationToken cancellationToken)
        {
            SavedManifests.Add(manifest);
            return Task.CompletedTask;
        }
    }
}
