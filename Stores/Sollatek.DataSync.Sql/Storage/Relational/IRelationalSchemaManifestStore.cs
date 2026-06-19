#nullable enable

using System.Data.Common;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public interface IRelationalSchemaManifestStore
{
    Task<bool> IsCurrentAsync(
        StorageProvider provider,
        DbConnection connection,
        SchemaManifest manifest,
        CancellationToken cancellationToken);

    Task SaveAsync(
        StorageProvider provider,
        DbConnection connection,
        SchemaManifest manifest,
        CancellationToken cancellationToken);
}
