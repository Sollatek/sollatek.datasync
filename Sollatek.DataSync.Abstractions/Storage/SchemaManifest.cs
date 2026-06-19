#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using Sollatek.DataSync.Config;
using Sollatek.DataSync.Storage.Relational;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage;

public sealed record SchemaManifest(
    int Version,
    StorageProvider Provider,
    string SyncPlanHash,
    string SchemaHash)
{
    public const int CurrentVersion = 1;

    public static SchemaManifest Create(
        StorageProvider provider,
        IReadOnlyList<SwaggerSyncEntityMetadata> selectedEntities)
    {
        ArgumentNullException.ThrowIfNull(selectedEntities);

        var entities = selectedEntities.ToArray();
        EnsureUniqueEntityKeys(entities);

        return new SchemaManifest(
            CurrentVersion,
            provider,
            ComputeHash(writer => WriteSyncPlanPayload(writer, entities)),
            ComputeHash(writer => WriteSchemaPayload(writer, provider, entities)));
    }

    private static void EnsureUniqueEntityKeys(
        IReadOnlyList<SwaggerSyncEntityMetadata> selectedEntities)
    {
        var duplicate = selectedEntities
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicate != null)
        {
            throw new InvalidOperationException(
                $"SyncPlan contains duplicate entity '{duplicate.Key}'.");
        }
    }

    private static void WriteSyncPlanPayload(
        Utf8JsonWriter writer,
        IReadOnlyList<SwaggerSyncEntityMetadata> selectedEntities)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", CurrentVersion);
        writer.WriteStartArray("entities");
        foreach (var entity in selectedEntities)
        {
            writer.WriteStringValue(entity.Key);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSchemaPayload(
        Utf8JsonWriter writer,
        StorageProvider provider,
        IReadOnlyList<SwaggerSyncEntityMetadata> selectedEntities)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", CurrentVersion);
        writer.WriteString("provider", provider.ToString());
        writer.WriteStartArray("selectedEntities");
        foreach (var entityKey in selectedEntities
                     .Select(x => x.Key)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStringValue(entityKey);
        }

        writer.WriteEndArray();

        if (provider is StorageProvider.SqlServer or StorageProvider.Postgres or StorageProvider.MySql)
        {
            WriteRelationalSchemaPayload(writer, selectedEntities);
        }
        else
        {
            WriteDocumentSchemaPayload(writer, selectedEntities);
        }

        writer.WriteEndObject();
    }

    private static void WriteRelationalSchemaPayload(
        Utf8JsonWriter writer,
        IReadOnlyList<SwaggerSyncEntityMetadata> selectedEntities)
    {
        var selectedEntityKeys = selectedEntities.Select(x => x.Key).ToArray();
        var knownEntities = selectedEntities.ToDictionary(
            x => x.Key,
            StringComparer.OrdinalIgnoreCase);
        var tablePlans = selectedEntities
            .Select(entity => RelationalSchemaPlanner.Plan(entity, selectedEntityKeys, knownEntities))
            .OrderBy(x => x.EntityKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        writer.WriteStartArray("relationalTables");
        foreach (var table in tablePlans)
        {
            WriteRelationalTablePayload(writer, table);
        }

        writer.WriteEndArray();
    }

    private static void WriteRelationalTablePayload(
        Utf8JsonWriter writer,
        RelationalTablePlan table)
    {
        writer.WriteStartObject();
        writer.WriteString("entityKey", table.EntityKey);
        writer.WriteString("tableName", table.TableName);

        writer.WriteStartArray("columns");
        foreach (var column in table.Columns
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStartObject();
            writer.WriteString("name", column.Name);
            writer.WriteString("source", column.Source);
            writer.WriteString("role", column.Role.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("primaryKeyColumns");
        foreach (var primaryKeyColumn in table.PrimaryKeyColumns)
        {
            writer.WriteStringValue(primaryKeyColumn);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("foreignKeys");
        foreach (var foreignKey in table.ForeignKeys
                     .OrderBy(x => x.ColumnName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.TargetTable, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStartObject();
            writer.WriteString("columnName", foreignKey.ColumnName);
            writer.WriteString("targetEntity", foreignKey.TargetEntity);
            writer.WriteString("targetTable", foreignKey.TargetTable);
            writer.WriteString("targetColumn", foreignKey.TargetColumn);
            writer.WriteBoolean("isNullable", foreignKey.IsNullable);
            writer.WriteString("onDelete", foreignKey.OnDelete.ToString());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDocumentSchemaPayload(
        Utf8JsonWriter writer,
        IReadOnlyList<SwaggerSyncEntityMetadata> selectedEntities)
    {
        writer.WriteStartArray("entities");
        foreach (var entity in selectedEntities.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStartObject();
            writer.WriteString("key", entity.Key);
            writer.WriteString("table", entity.Table);

            writer.WriteStartArray("primaryKey");
            foreach (var primaryKey in entity.PrimaryKey)
            {
                writer.WriteStringValue(primaryKey);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("scalarFields");
            foreach (var field in entity.ScalarFields.OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("source", field.Source);
                writer.WriteString("localColumn", field.LocalColumn);
                writer.WriteString("type", field.Type);
                writer.WriteString("format", field.Format);
                writer.WriteBoolean("isNullable", field.IsNullable);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (entity.Watermark != null)
            {
                writer.WriteStartObject("watermark");
                writer.WriteString("field", entity.Watermark.Field);
                writer.WriteStartArray("tieBreakers");
                foreach (var tieBreaker in entity.Watermark.TieBreakers)
                {
                    writer.WriteStringValue(tieBreaker);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteStartArray("references");
            foreach (var reference in entity.References.OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("source", reference.Source);
                writer.WriteString("localColumn", reference.LocalColumn);
                writer.WriteString("targetEntity", reference.TargetEntity);
                writer.WriteString("targetKey", reference.TargetKey);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string ComputeHash(Action<Utf8JsonWriter> writePayload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writePayload(writer);
        }

        var hash = SHA256.HashData(stream.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
