#nullable enable

using System.Data.Common;
using System.Globalization;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage.Relational;

public sealed class RelationalSyncTargetDataStore : ISyncTargetDataStore
{
    private readonly StorageProvider _provider;
    private readonly Func<DbConnection> _createConnection;

    public RelationalSyncTargetDataStore(
        StorageProvider provider,
        string connectionString)
        : this(provider, () => RelationalConnectionFactory.Create(provider, connectionString))
    {
    }

    public RelationalSyncTargetDataStore(
        StorageProvider provider,
        Func<DbConnection> createConnection)
    {
        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }

        _provider = provider;
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
    }

    public async Task<bool> HasStoredDataAsync(
        SwaggerSyncEntityMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var tableName = metadata.Table ?? metadata.Key;
        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, tableName, cancellationToken))
        {
            return false;
        }

        var command = RelationalTargetDataCommandBuilder.BuildHasAnyRows(_provider, tableName);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        var result = await dbCommand.ExecuteScalarAsync(cancellationToken);
        return result is not null and not DBNull;
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

        var table = RelationalSchemaPlanner.Plan(metadata, [metadata.Key]);
        var watermarkColumn = table.Columns.FirstOrDefault(column =>
            string.Equals(column.Source, metadata.Watermark.Field, StringComparison.OrdinalIgnoreCase));
        if (watermarkColumn == null)
        {
            return null;
        }

        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, table.TableName, cancellationToken))
        {
            return null;
        }

        var command = RelationalTargetDataCommandBuilder.BuildGetLatestWatermark(
            _provider,
            table.TableName,
            watermarkColumn.Name);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        var value = await dbCommand.ExecuteScalarAsync(cancellationToken);
        return ReadDateTimeOffset(value, metadata.Key, metadata.Watermark.Field);
    }

    private async Task<bool> TableExistsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var command = RelationalSchemaValidationCommandBuilder.BuildTableExists(_provider, tableName);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        var result = await dbCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        object? value,
        string entityKey,
        string watermarkField)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToUniversalTime();
        }

        if (value is DateTime dateTime)
        {
            return dateTime.Kind switch
            {
                DateTimeKind.Local => new DateTimeOffset(dateTime).ToUniversalTime(),
                DateTimeKind.Utc => new DateTimeOffset(dateTime),
                _ => new DateTimeOffset(
                    DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
                    TimeSpan.Zero)
            };
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                text,
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
