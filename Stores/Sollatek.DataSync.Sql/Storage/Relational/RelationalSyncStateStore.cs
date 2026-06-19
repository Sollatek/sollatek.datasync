#nullable enable

using System.Data.Common;
using System.Globalization;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;

namespace Sollatek.DataSync.Storage.Relational;

public sealed class RelationalSyncStateStore : ISyncStateStore
{
    private readonly StorageProvider _provider;
    private readonly Func<DbConnection> _createConnection;

    public RelationalSyncStateStore(
        StorageProvider provider,
        string connectionString)
        : this(provider, () => RelationalConnectionFactory.Create(provider, connectionString))
    {
    }

    public RelationalSyncStateStore(
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

    public async Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
        string entityKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var command = RelationalSyncStateCommandBuilder.BuildGetLastSuccessfulEnd(_provider, entityKey);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        var value = await dbCommand.ExecuteScalarAsync(cancellationToken);
        if (value is null || value == DBNull.Value)
        {
            return null;
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
                out var end))
        {
            return end;
        }

        throw new InvalidOperationException(
            $"Stored sync state for entity '{entityKey}' has an invalid last_successful_end_utc value.");
    }

    public async Task SaveSuccessfulEndAsync(
        string entityKey,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var command = RelationalSyncStateCommandBuilder.BuildSaveSuccessfulEnd(
            _provider,
            entityKey,
            end,
            DateTimeOffset.UtcNow);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureTableAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var command = RelationalSyncStateCommandBuilder.BuildCreateTableIfMissing(_provider);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
