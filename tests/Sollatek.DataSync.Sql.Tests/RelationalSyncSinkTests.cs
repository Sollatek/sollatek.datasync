using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Relational;

namespace Sollatek.DataSync.Tests;

public sealed class RelationalSyncSinkTests
{
    [Fact]
    public async Task WriteAsync_ExecutesOneBatchUpsertPerWriteInsideTransaction()
    {
        var connection = new RecordingDbConnection();
        var sink = new RelationalSyncSink(StorageProvider.Postgres, () => connection);
        var table = AssetTable();
        var rows = Rows(
            new RelationalRow(
                "assets",
                "assets",
                new Dictionary<string, object?>
                {
                    ["id"] = 42L,
                    ["owner_customer_id"] = "customer-1"
                }),
            new RelationalRow(
                "assets",
                "assets",
                new Dictionary<string, object?>
                {
                    ["id"] = 43L,
                    ["owner_customer_id"] = "customer-2"
                }));

        await sink.WriteAsync(table, rows, CancellationToken.None);

        Assert.True(connection.Opened);
        Assert.True(connection.Transaction?.Committed);
        var executed = Assert.Single(connection.ExecutedCommands);
        Assert.Contains("INSERT INTO \"assets\"", executed.Sql);
        Assert.Contains("VALUES ($1, $2), ($3, $4)", executed.Sql);
        Assert.True(executed.HadTransaction);
        Assert.Equal(["42", "customer-1", "43", "customer-2"], executed.Parameters.Select(x => x.Value).ToArray());
    }

    [Fact]
    public async Task WriteAsync_DoesNotApplySchemaChangesDuringRowWrites()
    {
        var connection = new RecordingDbConnection();
        var sink = new RelationalSyncSink(
            StorageProvider.Postgres,
            () => connection,
            StorageSchemaMode.ApplySafeChanges);
        var table = AssetTable();
        var rows = Rows(
            new RelationalRow(
                "assets",
                "assets",
                new Dictionary<string, object?>
                {
                    ["id"] = 42L,
                    ["owner_customer_id"] = "customer-1"
                }));

        await sink.WriteAsync(table, rows, CancellationToken.None);

        var executed = Assert.Single(connection.ExecutedCommands);
        Assert.StartsWith("INSERT INTO \"assets\"", executed.Sql);
        Assert.True(executed.HadTransaction);
    }

    [Fact]
    public async Task PrepareAsync_SkipsSafeSchemaChangesWhenManifestMatches()
    {
        var connection = new RecordingDbConnection();
        var manifestStore = new RecordingRelationalSchemaManifestStore(isCurrent: true);
        var sink = new RelationalSyncSink(
            StorageProvider.Postgres,
            () => connection,
            StorageSchemaMode.ApplySafeChanges,
            manifestStore);
        var manifest = AssetManifest();

        await sink.PrepareAsync(manifest, [AssetTable()], CancellationToken.None);

        Assert.Empty(connection.ExecutedCommands);
        Assert.Single(manifestStore.CheckedManifests);
        Assert.Empty(manifestStore.SavedManifests);
    }

    [Fact]
    public async Task PrepareAsync_AppliesSafeSchemaChangesAndSavesChangedManifest()
    {
        var connection = new RecordingDbConnection();
        var manifestStore = new RecordingRelationalSchemaManifestStore(isCurrent: false);
        var sink = new RelationalSyncSink(
            StorageProvider.Postgres,
            () => connection,
            StorageSchemaMode.ApplySafeChanges,
            manifestStore);
        var manifest = AssetManifest();

        await sink.PrepareAsync(manifest, [AssetTable()], CancellationToken.None);

        var executed = Assert.Single(connection.ExecutedCommands);
        Assert.StartsWith("CREATE TABLE IF NOT EXISTS", executed.Sql);
        Assert.False(executed.HadTransaction);
        Assert.Single(manifestStore.CheckedManifests);
        Assert.Equal([manifest], manifestStore.SavedManifests);
    }

    [Fact]
    public async Task PrepareAsync_AddsMissingFlexibleForeignKeys()
    {
        var connection = new RecordingDbConnection();
        connection.ScalarResults.Enqueue(0L);
        var manifestStore = new RecordingRelationalSchemaManifestStore(isCurrent: false);
        var sink = new RelationalSyncSink(
            StorageProvider.Postgres,
            () => connection,
            StorageSchemaMode.ApplySafeChanges,
            manifestStore);

        await sink.PrepareAsync(AssetManifest(), [AssetTableWithForeignKey()], CancellationToken.None);

        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.StartsWith("CREATE TABLE IF NOT EXISTS", connection.ExecutedCommands[0].Sql);
        Assert.Contains("pg_constraint", connection.ExecutedCommands[1].Sql);
        Assert.StartsWith("ALTER TABLE \"assets\"", connection.ExecutedCommands[2].Sql);
        Assert.Contains("fk_assets_owner_customer_id", connection.ExecutedCommands[2].Sql);
    }

    [Fact]
    public async Task PrepareAsync_SkipsExistingFlexibleForeignKeys()
    {
        var connection = new RecordingDbConnection();
        connection.ScalarResults.Enqueue(1L);
        var manifestStore = new RecordingRelationalSchemaManifestStore(isCurrent: false);
        var sink = new RelationalSyncSink(
            StorageProvider.Postgres,
            () => connection,
            StorageSchemaMode.ApplySafeChanges,
            manifestStore);

        await sink.PrepareAsync(AssetManifest(), [AssetTableWithForeignKey()], CancellationToken.None);

        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.StartsWith("CREATE TABLE IF NOT EXISTS", connection.ExecutedCommands[0].Sql);
        Assert.Contains("pg_constraint", connection.ExecutedCommands[1].Sql);
        Assert.DoesNotContain(connection.ExecutedCommands, command =>
            command.Sql.StartsWith("ALTER TABLE \"assets\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsync_ValidateModeThrowsWhenTableIsMissing()
    {
        var connection = new RecordingDbConnection();
        connection.ScalarResults.Enqueue(0L);
        var sink = new RelationalSyncSink(StorageProvider.Postgres, () => connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.PrepareAsync(AssetManifest(), [AssetTable()], CancellationToken.None));

        Assert.Contains("Relational table 'assets' does not exist", exception.Message);
        Assert.Contains("information_schema.tables", connection.ExecutedCommands.Single().Sql);
    }

    [Fact]
    public async Task PrepareAsync_ValidateModeThrowsWhenColumnIsMissing()
    {
        var connection = new RecordingDbConnection();
        connection.ScalarResults.Enqueue(1L);
        connection.ScalarResults.Enqueue(1L);
        connection.ScalarResults.Enqueue(0L);
        var sink = new RelationalSyncSink(StorageProvider.Postgres, () => connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.PrepareAsync(AssetManifest(), [AssetTable()], CancellationToken.None));

        Assert.Contains("Relational column 'owner_customer_id' does not exist", exception.Message);
        Assert.Equal(3, connection.ExecutedCommands.Count);
    }

    [Fact]
    public async Task PrepareAsync_ValidateModeAcceptsExistingTableColumnsAndForeignKeys()
    {
        var connection = new RecordingDbConnection();
        connection.ScalarResults.Enqueue(1L);
        connection.ScalarResults.Enqueue(1L);
        connection.ScalarResults.Enqueue(1L);
        connection.ScalarResults.Enqueue(1L);
        var sink = new RelationalSyncSink(StorageProvider.Postgres, () => connection);

        await sink.PrepareAsync(AssetManifest(), [AssetTableWithForeignKey()], CancellationToken.None);

        Assert.Equal(4, connection.ExecutedCommands.Count);
        Assert.Contains("information_schema.tables", connection.ExecutedCommands[0].Sql);
        Assert.Contains("information_schema.columns", connection.ExecutedCommands[1].Sql);
        Assert.Contains("information_schema.columns", connection.ExecutedCommands[2].Sql);
        Assert.Contains("pg_constraint", connection.ExecutedCommands[3].Sql);
    }

    private static async IAsyncEnumerable<RelationalRow> Rows(params RelationalRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static RelationalTablePlan AssetTable()
    {
        return new RelationalTablePlan(
            "assets",
            "assets",
            [
                new RelationalColumnPlan("id", "id", RelationalColumnRole.PrimaryKey),
                new RelationalColumnPlan("owner_customer_id", "ownerCustomer.id", RelationalColumnRole.ReferenceFlatValue)
            ],
            ["id"],
            []);
    }

    private static RelationalTablePlan AssetTableWithForeignKey()
    {
        var foreignKey = new RelationalForeignKeyPlan(
            "owner_customer_id",
            "customers",
            "customers",
            "id",
            IsNullable: true,
            RelationalForeignKeyDeleteBehavior.SetNull);

        return new RelationalTablePlan(
            "assets",
            "assets",
            [
                new RelationalColumnPlan("id", "id", RelationalColumnRole.PrimaryKey),
                new RelationalColumnPlan("owner_customer_id", "ownerCustomer.id", RelationalColumnRole.ReferenceForeignKey)
            ],
            ["id"],
            [foreignKey]);
    }

    private static SchemaManifest AssetManifest()
    {
        return new SchemaManifest(
            SchemaManifest.CurrentVersion,
            StorageProvider.Postgres,
            "sync-plan-hash",
            "schema-hash");
    }

    private sealed record ExecutedCommand(
        string Sql,
        bool HadTransaction,
        IReadOnlyList<ExecutedParameter> Parameters);

    private sealed record ExecutedParameter(string Name, object? Value);

    private sealed class RecordingDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public bool Opened { get; private set; }

        public RecordingDbTransaction? Transaction { get; private set; }

        public Queue<object?> ScalarResults { get; } = [];

        public List<ExecutedCommand> ExecutedCommands { get; } = [];

        [AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "datasync";

        public override string DataSource => "recording";

        public override string ServerVersion => "1";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
            _state = ConnectionState.Closed;
        }

        public override void Open()
        {
            Opened = true;
            _state = ConnectionState.Open;
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            Open();
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            Transaction = new RecordingDbTransaction(this, isolationLevel);
            return Transaction;
        }

        protected override DbCommand CreateDbCommand()
        {
            return new RecordingDbCommand(this);
        }
    }

    private sealed class RecordingDbTransaction(
        RecordingDbConnection connection,
        IsolationLevel isolationLevel) : DbTransaction
    {
        public bool Committed { get; private set; }

        public override IsolationLevel IsolationLevel { get; } = isolationLevel;

        protected override DbConnection DbConnection { get; } = connection;

        public override void Commit()
        {
            Committed = true;
        }

        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }

        public override void Rollback()
        {
        }
    }

    private sealed class RecordingDbCommand(RecordingDbConnection connection) : DbCommand
    {
        private readonly RecordingDbParameterCollection _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = "";

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; } = connection;

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            RecordExecution();
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExecuteNonQuery());
        }

        public override object? ExecuteScalar()
        {
            RecordExecution();
            return connection.ScalarResults.Count > 0 ? connection.ScalarResults.Dequeue() : null;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ExecuteScalar());
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter()
        {
            return new RecordingDbParameter();
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            throw new NotSupportedException();
        }

        private void RecordExecution()
        {
            connection.ExecutedCommands.Add(new ExecutedCommand(
                CommandText ?? "",
                DbTransaction is not null,
                _parameters
                    .Cast<DbParameter>()
                    .Select(x => new ExecutedParameter(x.ParameterName, x.Value))
                    .ToArray()));
        }
    }

    private sealed class RecordingDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = "";

        [AllowNull]
        public override string SourceColumn { get; set; } = "";

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class RecordingDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];

        public override int Count => _parameters.Count;

        public override object SyncRoot => this;

        public override int Add(object value)
        {
            _parameters.Add((DbParameter)value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear()
        {
            _parameters.Clear();
        }

        public override bool Contains(object value)
        {
            return _parameters.Contains((DbParameter)value);
        }

        public override bool Contains(string value)
        {
            return IndexOf(value) >= 0;
        }

        public override void CopyTo(Array array, int index)
        {
            ((ICollection)_parameters).CopyTo(array, index);
        }

        public override IEnumerator GetEnumerator()
        {
            return _parameters.GetEnumerator();
        }

        public override int IndexOf(object value)
        {
            return _parameters.IndexOf((DbParameter)value);
        }

        public override int IndexOf(string parameterName)
        {
            return _parameters.FindIndex(x =>
                string.Equals(x.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        public override void Insert(int index, object value)
        {
            _parameters.Insert(index, (DbParameter)value);
        }

        public override void Remove(object value)
        {
            _parameters.Remove((DbParameter)value);
        }

        public override void RemoveAt(int index)
        {
            _parameters.RemoveAt(index);
        }

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
            {
                RemoveAt(index);
            }
        }

        protected override DbParameter GetParameter(int index)
        {
            return _parameters[index];
        }

        protected override DbParameter GetParameter(string parameterName)
        {
            return _parameters[IndexOf(parameterName)];
        }

        protected override void SetParameter(int index, DbParameter value)
        {
            _parameters[index] = value;
        }

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
            {
                _parameters.Add(value);
                return;
            }

            _parameters[index] = value;
        }
    }

    private sealed class RecordingRelationalSchemaManifestStore(bool isCurrent) : IRelationalSchemaManifestStore
    {
        public List<SchemaManifest> CheckedManifests { get; } = [];

        public List<SchemaManifest> SavedManifests { get; } = [];

        public Task<bool> IsCurrentAsync(
            StorageProvider provider,
            DbConnection connection,
            SchemaManifest manifest,
            CancellationToken cancellationToken)
        {
            CheckedManifests.Add(manifest);
            return Task.FromResult(isCurrent);
        }

        public Task SaveAsync(
            StorageProvider provider,
            DbConnection connection,
            SchemaManifest manifest,
            CancellationToken cancellationToken)
        {
            SavedManifests.Add(manifest);
            return Task.CompletedTask;
        }
    }
}
