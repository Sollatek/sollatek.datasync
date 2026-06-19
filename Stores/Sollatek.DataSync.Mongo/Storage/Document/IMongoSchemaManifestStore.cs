#nullable enable

namespace Sollatek.DataSync.Storage.Document;

public interface IMongoSchemaManifestStore
{
    Task<bool> IsCurrentAsync(
        SchemaManifest manifest,
        CancellationToken cancellationToken);

    Task SaveAsync(
        SchemaManifest manifest,
        CancellationToken cancellationToken);
}
