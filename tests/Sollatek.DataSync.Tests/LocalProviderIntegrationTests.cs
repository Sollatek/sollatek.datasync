using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MongoDB.Driver;
using MySqlConnector;
using Npgsql;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage;
using Sollatek.DataSync.Storage.Document;
using Sollatek.DataSync.Storage.Relational;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Tests;

public sealed class LocalProviderIntegrationTests
{
    private const string RunFlag = "DATASYNC_RUN_LOCAL_PROVIDER_TESTS";

    [Theory]
    [InlineData(StorageProvider.Postgres)]
    [InlineData(StorageProvider.MySql)]
    [InlineData(StorageProvider.SqlServer)]
    public async Task RelationalSink_UpsertsRowsAgainstLocalDockerProvider(StorageProvider provider)
    {
        if (!ShouldRun())
        {
            return;
        }

        var connectionString = await EnsureRelationalDatabaseAsync(provider);
        var tableName = $"datasync_integration_{Guid.NewGuid():N}";
        var table = AssetTable(tableName);
        var sink = new RelationalSyncSink(
            provider,
            connectionString,
            StorageSchemaMode.ApplySafeChanges);
        var manifest = new SchemaManifest(
            SchemaManifest.CurrentVersion,
            provider,
            $"plan-{tableName}",
            $"schema-{tableName}");

        await sink.PrepareAsync(manifest, [table], CancellationToken.None);
        await sink.WriteAsync(table, Rows(Row(tableName, "1", "A")), CancellationToken.None);
        await sink.WriteAsync(table, Rows(Row(tableName, "1", "B")), CancellationToken.None);

        var storedSerial = await ReadSerialAsync(provider, connectionString, tableName);

        Assert.Equal("B", storedSerial);
    }

    [Fact]
    public async Task MongoSyncSink_ReplacesDocumentAgainstLocalDockerProvider()
    {
        if (!ShouldRun())
        {
            return;
        }

        var connectionString = GetMongoConnectionString();
        var mongoUrl = MongoUrl.Create(connectionString);
        var client = new MongoClient(mongoUrl);
        var database = client.GetDatabase(mongoUrl.DatabaseName);
        var collectionName = $"datasync_integration_{Guid.NewGuid():N}";
        var sink = new MongoSyncSink(
            new MongoDocumentWriter(database),
            new MongoSchemaManifestStore(database));
        var metadata = AssetMetadata(collectionName);

        await sink.PrepareAsync(
            new SchemaManifest(
                SchemaManifest.CurrentVersion,
                StorageProvider.Mongo,
                $"plan-{collectionName}",
                $"schema-{collectionName}"),
            CancellationToken.None);
        await sink.WriteAsync(metadata, JsonRows("""{ "id": "1", "serial": "A" }"""), CancellationToken.None);
        await sink.WriteAsync(metadata, JsonRows("""{ "id": "1", "serial": "B" }"""), CancellationToken.None);

        var collection = database.GetCollection<MongoDB.Bson.BsonDocument>(collectionName);
        var stored = await collection
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "1"))
            .FirstAsync();

        Assert.Equal("B", stored["serial"].AsString);
    }

    private static bool ShouldRun()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(RunFlag),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> EnsureRelationalDatabaseAsync(StorageProvider provider)
    {
        var connectionString = GetRelationalConnectionString(provider);
        if (provider == StorageProvider.SqlServer)
        {
            await EnsureSqlServerDatabaseAsync(connectionString);
        }

        await WaitForRelationalConnectionAsync(provider, connectionString);
        return connectionString;
    }

    private static string GetRelationalConnectionString(StorageProvider provider)
    {
        return provider switch
        {
            StorageProvider.Postgres => Environment.GetEnvironmentVariable("DATASYNC_POSTGRES_CONNECTION")
                                        ?? "Host=localhost;Port=5433;Database=sollatek_datasync;Username=postgres;Password=datasync;Ssl Mode=Disable",
            StorageProvider.MySql => Environment.GetEnvironmentVariable("DATASYNC_MYSQL_CONNECTION")
                                     ?? "Server=localhost;Port=3307;Database=sollatek_datasync;User ID=datasync;Password=datasync;SslMode=Disabled",
            StorageProvider.SqlServer => Environment.GetEnvironmentVariable("DATASYNC_SQLSERVER_CONNECTION")
                                         ?? "Server=localhost,14333;Database=sollatek_datasync;User Id=sa;Password=DataSync_Local_12345;Encrypt=False;TrustServerCertificate=True",
            _ => throw new InvalidOperationException($"Provider '{provider}' is not relational.")
        };
    }

    private static string GetMongoConnectionString()
    {
        return Environment.GetEnvironmentVariable("DATASYNC_MONGO_CONNECTION")
               ?? "mongodb://root:datasync_root@localhost:27018/sollatek_datasync?authSource=admin";
    }

    private static async Task EnsureSqlServerDatabaseAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await WaitForConnectionAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{databaseName.Replace("'", "''", StringComparison.Ordinal)}') IS NULL
            BEGIN
                CREATE DATABASE [{databaseName.Replace("]", "]]", StringComparison.Ordinal)}];
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForRelationalConnectionAsync(
        StorageProvider provider,
        string connectionString)
    {
        DbConnection connection = provider switch
        {
            StorageProvider.Postgres => new NpgsqlConnection(connectionString),
            StorageProvider.MySql => new MySqlConnection(connectionString),
            StorageProvider.SqlServer => new SqlConnection(connectionString),
            _ => throw new InvalidOperationException($"Provider '{provider}' is not relational.")
        };
        await using (connection)
        {
            await WaitForConnectionAsync(connection);
        }
    }

    private static async Task WaitForConnectionAsync(DbConnection connection)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException(
            "Timed out waiting for the local Docker database to accept connections.",
            lastError);
    }

    private static async Task<string?> ReadSerialAsync(
        StorageProvider provider,
        string connectionString,
        string tableName)
    {
        DbConnection connection = provider switch
        {
            StorageProvider.Postgres => new NpgsqlConnection(connectionString),
            StorageProvider.MySql => new MySqlConnection(connectionString),
            StorageProvider.SqlServer => new SqlConnection(connectionString),
            _ => throw new InvalidOperationException($"Provider '{provider}' is not relational.")
        };
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = provider switch
            {
                StorageProvider.Postgres => $"SELECT \"serial\" FROM \"{tableName}\" WHERE \"id\" = '1';",
                StorageProvider.MySql => $"SELECT `serial` FROM `{tableName}` WHERE `id` = '1';",
                StorageProvider.SqlServer => $"SELECT [serial] FROM [{tableName}] WHERE [id] = '1';",
                _ => throw new InvalidOperationException($"Provider '{provider}' is not relational.")
            };

            return (string?)await command.ExecuteScalarAsync();
        }
    }

    private static RelationalTablePlan AssetTable(string tableName)
    {
        return new RelationalTablePlan(
            "assets",
            tableName,
            [
                new RelationalColumnPlan("id", "id", RelationalColumnRole.PrimaryKey),
                new RelationalColumnPlan("serial", "serial", RelationalColumnRole.Scalar)
            ],
            ["id"],
            []);
    }

    private static RelationalRow Row(string tableName, string id, string serial)
    {
        return new RelationalRow(
            "assets",
            tableName,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["serial"] = serial
            });
    }

    private static async IAsyncEnumerable<RelationalRow> Rows(params RelationalRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<JsonElement> JsonRows(params string[] rows)
    {
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row);
            yield return document.RootElement.Clone();
        }

        await Task.CompletedTask;
    }

    private static SwaggerSyncEntityMetadata AssetMetadata(string collection)
    {
        return new SwaggerSyncEntityMetadata
        {
            Key = "assets",
            Collection = collection,
            OperationIds = ["Assets_Get"],
            PrimaryKey = ["id"],
            References = [],
            DocumentNames = ["data-v1"]
        };
    }
}
