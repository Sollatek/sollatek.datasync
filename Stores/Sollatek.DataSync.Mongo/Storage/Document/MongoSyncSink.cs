#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage.Document;

public sealed class MongoSyncSink : IDocumentSyncSink
{
    private readonly IMongoDocumentWriter _writer;
    private readonly IMongoSchemaManifestStore _schemaManifestStore;

    public MongoSyncSink(
        IMongoDocumentWriter writer,
        IMongoSchemaManifestStore schemaManifestStore)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _schemaManifestStore = schemaManifestStore ?? throw new ArgumentNullException(nameof(schemaManifestStore));
    }

    public async Task PrepareAsync(
        SchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (await _schemaManifestStore.IsCurrentAsync(manifest, cancellationToken))
        {
            return;
        }

        await _schemaManifestStore.SaveAsync(manifest, cancellationToken);
    }

    public async Task WriteAsync(
        SwaggerSyncEntityMetadata metadata,
        IAsyncEnumerable<JsonElement> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rows);

        var collectionName = !string.IsNullOrWhiteSpace(metadata.Collection)
            ? metadata.Collection
            : metadata.Key;

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var document = MongoDocumentMapper.Map(metadata, row);
            await _writer.UpsertAsync(collectionName, document, cancellationToken);
        }
    }
}
