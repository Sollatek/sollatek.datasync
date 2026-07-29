#nullable enable

using System.Data.Common;
using System.Text.Json;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.State;
using Sollatek.DataSync.Sync.Contract;

namespace Sollatek.DataSync.Storage.Relational;

public sealed class RelationalSyncContractStore : ISyncContractStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly StorageProvider _provider;
    private readonly Func<DbConnection> _createConnection;

    public RelationalSyncContractStore(
        StorageProvider provider,
        string connectionString)
        : this(provider, () => RelationalConnectionFactory.Create(provider, connectionString))
    {
    }

    public RelationalSyncContractStore(
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

    public async Task<SyncContractSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var command = RelationalSyncContractCommandBuilder.BuildLoad(_provider);
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

        var snapshot = JsonSerializer.Deserialize<SyncContractSnapshot>(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty,
            JsonOptions);
        if (snapshot is null)
        {
            throw new InvalidOperationException(
                "Stored relational DataSync contract is invalid.");
        }

        snapshot.Validate();
        return snapshot;
    }

    public async Task SaveAsync(
        SyncContractSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        var command = RelationalSyncContractCommandBuilder.BuildSave(
            _provider,
            JsonSerializer.Serialize(snapshot, JsonOptions));
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
        var command =
            RelationalSyncContractCommandBuilder.BuildCreateTableIfMissing(_provider);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);
        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
