#nullable enable

using System.Data.Common;
using System.Globalization;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Storage.Relational;

public sealed class RelationalSyncSink : IRelationalSyncSink
{
    private readonly StorageProvider _provider;
    private readonly Func<DbConnection> _createConnection;
    private readonly StorageSchemaMode _schemaMode;
    private readonly IRelationalSchemaManifestStore _schemaManifestStore;

    public RelationalSyncSink(
        StorageProvider provider,
        string connectionString,
        StorageSchemaMode schemaMode = StorageSchemaMode.Validate)
        : this(
            provider,
            () => RelationalConnectionFactory.Create(provider, connectionString),
            schemaMode,
            new RelationalSchemaManifestStore())
    {
    }

    public RelationalSyncSink(
        StorageProvider provider,
        Func<DbConnection> createConnection,
        StorageSchemaMode schemaMode = StorageSchemaMode.Validate)
        : this(provider, createConnection, schemaMode, new RelationalSchemaManifestStore())
    {
    }

    public RelationalSyncSink(
        StorageProvider provider,
        Func<DbConnection> createConnection,
        StorageSchemaMode schemaMode,
        IRelationalSchemaManifestStore schemaManifestStore)
    {
        if (provider is not (StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql))
        {
            throw new InvalidOperationException(
                $"Storage provider '{provider}' is not a relational provider.");
        }

        _provider = provider;
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _schemaMode = schemaMode;
        _schemaManifestStore = schemaManifestStore ?? throw new ArgumentNullException(nameof(schemaManifestStore));
    }

    public async Task PrepareAsync(
        SchemaManifest manifest,
        IReadOnlyList<RelationalTablePlan> tables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(tables);

        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);

        if (_schemaMode == StorageSchemaMode.Validate)
        {
            await ValidateSchemaAsync(connection, tables, cancellationToken);
            return;
        }

        if (_schemaMode != StorageSchemaMode.ApplySafeChanges)
        {
            throw new InvalidOperationException(
                $"Unsupported storage schema mode '{_schemaMode}'.");
        }

        if (await _schemaManifestStore.IsCurrentAsync(_provider, connection, manifest, cancellationToken))
        {
            return;
        }

        foreach (var table in tables)
        {
            var schemaCommand = RelationalSchemaCommandBuilder.BuildCreateTableIfMissing(_provider, table);
            await using var dbSchemaCommand = RelationalCommandBinder.CreateCommand(
                _provider,
                connection,
                transaction: null,
                schemaCommand);

            await dbSchemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in tables)
        {
            await ApplyMissingColumnsAsync(connection, table, cancellationToken);
        }

        foreach (var table in tables)
        {
            await ApplyMissingForeignKeysAsync(connection, table, cancellationToken);
        }

        await ValidateSchemaAsync(connection, tables, cancellationToken);
        await _schemaManifestStore.SaveAsync(_provider, connection, manifest, cancellationToken);
    }

    public async Task WriteAsync(
        RelationalTablePlan table,
        IAsyncEnumerable<RelationalRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);

        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var maxRowsPerCommand = RelationalUpsertCommandBuilder.GetMaxRowsPerCommand(_provider, table);
            var batch = new List<RelationalRow>(maxRowsPerCommand);

            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                batch.Add(row);

                if (batch.Count >= maxRowsPerCommand)
                {
                    await ExecuteBatchAsync(connection, transaction, table, batch, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await ExecuteBatchAsync(connection, transaction, table, batch, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task ExecuteBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalTablePlan table,
        IReadOnlyList<RelationalRow> batch,
        CancellationToken cancellationToken)
    {
        var command = RelationalUpsertCommandBuilder.BuildUpsert(_provider, table, batch);
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction,
            command);

        await dbCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ValidateSchemaAsync(
        DbConnection connection,
        IReadOnlyList<RelationalTablePlan> tables,
        CancellationToken cancellationToken)
    {
        foreach (var table in tables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Relational table '{table.TableName}' does not exist. Set Storage:schemaMode to applySafeChanges to let DataSync create missing tables safely, or create the table before running in validate mode.");
            }

            foreach (var column in table.Columns)
            {
                if (!await ColumnExistsAsync(connection, table, column, cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Relational column '{column.Name}' does not exist on table '{table.TableName}'. Update the table before running in validate mode, or set Storage:schemaMode to applySafeChanges to add missing nullable non-key columns safely.");
                }
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                if (!await ForeignKeyExistsAsync(connection, table, foreignKey, cancellationToken))
                {
                    var constraintName = RelationalForeignKeyCommandBuilder.GetConstraintName(table, foreignKey);
                    throw new InvalidOperationException(
                        $"Relational foreign key '{constraintName}' does not exist on table '{table.TableName}'. Set Storage:schemaMode to applySafeChanges to let DataSync add missing flexible FKs safely, or update the table before running in validate mode.");
                }
            }
        }
    }

    private async Task<bool> TableExistsAsync(
        DbConnection connection,
        RelationalTablePlan table,
        CancellationToken cancellationToken)
    {
        var command = RelationalSchemaValidationCommandBuilder.BuildTableExists(_provider, table.TableName);
        return await ExistsAsync(connection, command, cancellationToken);
    }

    private async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        RelationalTablePlan table,
        RelationalColumnPlan column,
        CancellationToken cancellationToken)
    {
        var command = RelationalSchemaValidationCommandBuilder.BuildColumnExists(
            _provider,
            table.TableName,
            column.Name);
        return await ExistsAsync(connection, command, cancellationToken);
    }

    private async Task ApplyMissingForeignKeysAsync(
        DbConnection connection,
        RelationalTablePlan table,
        CancellationToken cancellationToken)
    {
        foreach (var foreignKey in table.ForeignKeys)
        {
            if (await ForeignKeyExistsAsync(connection, table, foreignKey, cancellationToken))
            {
                continue;
            }

            var command = RelationalForeignKeyCommandBuilder.BuildAdd(_provider, table, foreignKey);
            await using var dbCommand = RelationalCommandBinder.CreateCommand(
                _provider,
                connection,
                transaction: null,
                command);

            await dbCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ApplyMissingColumnsAsync(
        DbConnection connection,
        RelationalTablePlan table,
        CancellationToken cancellationToken)
    {
        foreach (var column in table.Columns)
        {
            if (await ColumnExistsAsync(connection, table, column, cancellationToken))
            {
                continue;
            }

            if (column.Role == RelationalColumnRole.PrimaryKey)
            {
                throw new InvalidOperationException(
                    $"Relational primary-key column '{column.Name}' is missing from existing table '{table.TableName}'. DataSync will not add or change primary keys automatically; apply an explicit database migration first.");
            }

            var command = RelationalSchemaCommandBuilder.BuildAddNullableColumn(
                _provider,
                table.TableName,
                column);
            await using var dbCommand = RelationalCommandBinder.CreateCommand(
                _provider,
                connection,
                transaction: null,
                command);
            await dbCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<bool> ForeignKeyExistsAsync(
        DbConnection connection,
        RelationalTablePlan table,
        RelationalForeignKeyPlan foreignKey,
        CancellationToken cancellationToken)
    {
        var command = RelationalForeignKeyCommandBuilder.BuildExists(_provider, table, foreignKey);
        return await ExistsAsync(connection, command, cancellationToken);
    }

    private async Task<bool> ExistsAsync(
        DbConnection connection,
        RelationalCommand command,
        CancellationToken cancellationToken)
    {
        await using var dbCommand = RelationalCommandBinder.CreateCommand(
            _provider,
            connection,
            transaction: null,
            command);

        var result = await dbCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }
}
