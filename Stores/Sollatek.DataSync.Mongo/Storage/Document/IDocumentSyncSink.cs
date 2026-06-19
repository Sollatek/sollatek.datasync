#nullable enable

using System.Text.Json;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage.Document;

public interface IDocumentSyncSink
{
    Task PrepareAsync(
        SchemaManifest manifest,
        CancellationToken cancellationToken);

    Task WriteAsync(
        SwaggerSyncEntityMetadata metadata,
        IAsyncEnumerable<JsonElement> rows,
        CancellationToken cancellationToken);
}
