#nullable enable

using System.Data.Common;
using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public sealed class RelationalSchemaManifestStore : IRelationalSchemaManifestStore
{
    public async Task<bool> IsCurrentAsync(
        StorageProvider provider,
        DbConnection connection,
        SchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(manifest);

        await ExecuteAsync(
            provider,
            connection,
            RelationalSchemaManifestCommandBuilder.BuildCreateTableIfMissing(provider),
            cancellationToken);

        var command = RelationalSchemaManifestCommandBuilder.BuildIsCurrent(provider, manifest);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            provider,
            connection,
            transaction: null,
            command);

        var result = await dbCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    public async Task SaveAsync(
        StorageProvider provider,
        DbConnection connection,
        SchemaManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(manifest);

        await ExecuteAsync(
            provider,
            connection,
            RelationalSchemaManifestCommandBuilder.BuildSave(provider, manifest),
            cancellationToken);
    }

    private static async Task ExecuteAsync(
        StorageProvider provider,
        DbConnection connection,
        RelationalCommand command,
        CancellationToken cancellationToken)
    {
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            provider,
            connection,
            transaction: null,
            command);

        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
