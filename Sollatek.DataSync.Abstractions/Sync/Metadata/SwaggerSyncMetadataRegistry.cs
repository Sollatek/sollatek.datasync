#nullable enable

using System.Text.Json;

namespace Sollatek.DataSync.Sync.Metadata;

public sealed class SwaggerSyncMetadataRegistry
{
    private const int SupportedVersion = 1;
    private const string ExtensionName = "x-sollatek-sync";

    private SwaggerSyncMetadataRegistry(
        IReadOnlyDictionary<string, SwaggerSyncEntityMetadata> entities,
        IReadOnlyList<SwaggerSyncEntityMetadata> orderedEntities,
        Dictionary<string, Dictionary<string, SwaggerSyncSchemaMetadata>> schemasByDocument,
        IReadOnlyList<SwaggerSyncSchemaMetadata> orderedSchemas)
    {
        Entities = entities;
        OrderedEntities = orderedEntities;
        SchemasByDocument = schemasByDocument.ToDictionary(
            x => x.Key,
            x => (IReadOnlyDictionary<string, SwaggerSyncSchemaMetadata>)x.Value,
            StringComparer.OrdinalIgnoreCase);
        OrderedSchemas = orderedSchemas;
    }

    public IReadOnlyDictionary<string, SwaggerSyncEntityMetadata> Entities { get; }

    public IReadOnlyList<SwaggerSyncEntityMetadata> OrderedEntities { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SwaggerSyncSchemaMetadata>> SchemasByDocument { get; }

    public IReadOnlyList<SwaggerSyncSchemaMetadata> OrderedSchemas { get; }

    public static SwaggerSyncMetadataRegistry Load(IEnumerable<SwaggerSyncDocumentSource> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var entities = new Dictionary<string, SwaggerSyncEntityMetadata>(StringComparer.OrdinalIgnoreCase);
        var entityOrder = new List<string>();
        var schemasByDocument =
            new Dictionary<string, Dictionary<string, SwaggerSyncSchemaMetadata>>(StringComparer.OrdinalIgnoreCase);
        var schemaOrder = new List<(string DocumentName, string SchemaName)>();

        foreach (var document in documents)
        {
            LoadDocument(document, entities, entityOrder, schemasByDocument, schemaOrder);
        }

        return new SwaggerSyncMetadataRegistry(
            entities,
            entityOrder.Select(key => entities[key]).ToArray(),
            schemasByDocument,
            schemaOrder
                .Select(key => schemasByDocument[key.DocumentName][key.SchemaName])
                .ToArray());
    }

    public bool ContainsEntity(string key)
    {
        return Entities.ContainsKey(key);
    }

    public SwaggerSyncEntityMetadata GetEntity(string key)
    {
        if (Entities.TryGetValue(key, out var entity))
        {
            return entity;
        }

        throw new InvalidOperationException($"Unknown sync entity '{key}'.");
    }

    public SwaggerSyncSchemaMetadata GetSchema(string schemaName)
    {
        var matches = SchemasByDocument
            .SelectMany(document => document.Value
                .Where(schema => string.Equals(schema.Key, schemaName, StringComparison.OrdinalIgnoreCase))
                .Select(schema => (DocumentName: document.Key, Schema: schema.Value)))
            .ToArray();

        if (matches.Length == 1)
        {
            return matches[0].Schema;
        }

        if (matches.Length > 1)
        {
            var documentNames = string.Join(
                "', '",
                matches.Select(x => x.DocumentName).Order(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"Ambiguous sync read schema '{schemaName}' is defined in Swagger documents '{documentNames}'. " +
                "Specify the Swagger document name.");
        }

        throw new InvalidOperationException($"Unknown sync read schema '{schemaName}'.");
    }

    public SwaggerSyncSchemaMetadata GetSchema(string documentName, string schemaName)
    {
        if (SchemasByDocument.TryGetValue(documentName, out var documentSchemas) &&
            documentSchemas.TryGetValue(schemaName, out var schema))
        {
            return schema;
        }

        throw new InvalidOperationException(
            $"Unknown sync read schema '{schemaName}' in Swagger document '{documentName}'.");
    }

    public IReadOnlyList<string> ValidateReferences()
    {
        var errors = new List<string>();

        foreach (var entity in Entities.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var reference in entity.References)
            {
                if (!ContainsEntity(reference.TargetEntity))
                {
                    errors.Add(
                        $"Sync entity '{entity.Key}' references unknown target entity '{reference.TargetEntity}' from '{reference.Source}'.");
                }
            }
        }

        return errors;
    }

    private static void LoadDocument(
        SwaggerSyncDocumentSource source,
        IDictionary<string, SwaggerSyncEntityMetadata> entities,
        ICollection<string> entityOrder,
        IDictionary<string, Dictionary<string, SwaggerSyncSchemaMetadata>> schemasByDocument,
        ICollection<(string DocumentName, string SchemaName)> schemaOrder)
    {
        if (string.IsNullOrWhiteSpace(source.DocumentName))
        {
            throw new InvalidOperationException("Swagger sync document name is required.");
        }

        if (string.IsNullOrWhiteSpace(source.Json))
        {
            throw new InvalidOperationException($"Swagger document '{source.DocumentName}' is empty.");
        }

        using var document = JsonDocument.Parse(source.Json);
        var root = document.RootElement;

        if (!root.TryGetProperty(ExtensionName, out var sync))
        {
            throw new InvalidOperationException(
                $"Swagger document '{source.DocumentName}' is missing '{ExtensionName}'.");
        }

        var version = sync.TryGetProperty("version", out var versionElement)
            ? versionElement.GetInt32()
            : 0;

        if (version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported {ExtensionName} version '{version}' in swagger document '{source.DocumentName}'. Supported version is '{SupportedVersion}'.");
        }

        var operationsById = ReadOperations(root, source.DocumentName);

        if (sync.TryGetProperty("entities", out var entitiesElement) &&
            entitiesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entityProperty in entitiesElement.EnumerateObject())
            {
                var entity = ReadEntity(
                    root,
                    source.DocumentName,
                    entityProperty.Name,
                    entityProperty.Value,
                    operationsById);

                if (entities.TryGetValue(entity.Key, out var existing))
                {
                    entities[entity.Key] = Merge(existing, entity);
                    continue;
                }

                entities.Add(entity.Key, entity);
                entityOrder.Add(entity.Key);
            }
        }

        if (!sync.TryGetProperty("schemas", out var schemasElement) ||
            schemasElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!schemasByDocument.TryGetValue(source.DocumentName, out var documentSchemas))
        {
            documentSchemas = new Dictionary<string, SwaggerSyncSchemaMetadata>(StringComparer.OrdinalIgnoreCase);
            schemasByDocument.Add(source.DocumentName, documentSchemas);
        }

        foreach (var schemaProperty in schemasElement.EnumerateObject())
        {
            var schema = ReadSchema(
                root,
                source.DocumentName,
                schemaProperty.Name,
                schemaProperty.Value,
                operationsById);

            if (documentSchemas.TryGetValue(schema.SchemaName, out var existing))
            {
                documentSchemas[schema.SchemaName] = Merge(existing, schema);
                continue;
            }

            documentSchemas.Add(schema.SchemaName, schema);
            schemaOrder.Add((source.DocumentName, schema.SchemaName));
        }
    }

    private static SwaggerSyncEntityMetadata ReadEntity(
        JsonElement root,
        string documentName,
        string key,
        JsonElement element,
        IReadOnlyDictionary<string, IReadOnlyList<SwaggerSyncOperationMetadata>> operationsById)
    {
        var operationId = GetOptionalString(element, "operationId");
        var operationIds = GetStringArray(element, "operationIds");
        if (operationIds.Count == 0 && !string.IsNullOrWhiteSpace(operationId))
        {
            operationIds = [operationId];
        }

        var schema = GetOptionalString(element, "schema");

        return new SwaggerSyncEntityMetadata
        {
            Key = key,
            Schema = schema,
            OperationId = operationId,
            OperationIds = operationIds,
            Operations = ResolveOperations(operationIds, operationsById),
            MetadataSource = GetOptionalString(element, "metadataSource"),
            Table = GetOptionalString(element, "table"),
            Collection = GetOptionalString(element, "collection"),
            PrimaryKey = GetStringArray(element, "primaryKey"),
            ScalarFields = ReadScalarFields(root, schema),
            Watermark = ReadWatermark(element),
            References = ReadReferences(element),
            DocumentNames = [documentName]
        };
    }

    private static SwaggerSyncSchemaMetadata ReadSchema(
        JsonElement root,
        string documentName,
        string key,
        JsonElement element,
        IReadOnlyDictionary<string, IReadOnlyList<SwaggerSyncOperationMetadata>> operationsById)
    {
        var schemaName = GetOptionalString(element, "schemaName") ?? key;
        var operationId = GetOptionalString(element, "operationId");
        var operationIds = GetStringArray(element, "operationIds");
        if (operationIds.Count == 0 && !string.IsNullOrWhiteSpace(operationId))
        {
            operationIds = [operationId];
        }

        var schema = GetOptionalString(element, "schema");

        return new SwaggerSyncSchemaMetadata
        {
            SchemaName = schemaName,
            Schema = schema,
            Type = GetOptionalString(element, "type"),
            OperationId = operationId,
            OperationIds = operationIds,
            Operations = ResolveOperations(operationIds, operationsById),
            MetadataSource = GetOptionalString(element, "metadataSource"),
            PrimaryKey = GetStringArray(element, "primaryKey"),
            ScalarFields = ReadScalarFields(root, schema),
            Watermark = ReadWatermark(element),
            References = ReadReferences(element),
            DocumentNames = [documentName]
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SwaggerSyncOperationMetadata>> ReadOperations(
        JsonElement root,
        string documentName)
    {
        var operations = new Dictionary<string, List<SwaggerSyncOperationMetadata>>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("paths", out var pathsElement) ||
            pathsElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, IReadOnlyList<SwaggerSyncOperationMetadata>>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var pathProperty in pathsElement.EnumerateObject())
        {
            if (pathProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var methodProperty in pathProperty.Value.EnumerateObject())
            {
                if (methodProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var operationId = GetOptionalString(methodProperty.Value, "operationId");
                if (string.IsNullOrWhiteSpace(operationId))
                {
                    continue;
                }

                if (!operations.TryGetValue(operationId, out var matchingOperations))
                {
                    matchingOperations = [];
                    operations.Add(operationId, matchingOperations);
                }

                matchingOperations.Add(new SwaggerSyncOperationMetadata
                {
                    OperationId = operationId,
                    Method = methodProperty.Name.ToLowerInvariant(),
                    Path = pathProperty.Name,
                    DocumentName = documentName
                });
            }
        }

        return operations.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<SwaggerSyncOperationMetadata>)x.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SwaggerSyncOperationMetadata> ResolveOperations(
        IReadOnlyList<string> operationIds,
        IReadOnlyDictionary<string, IReadOnlyList<SwaggerSyncOperationMetadata>> operationsById)
    {
        var operations = new List<SwaggerSyncOperationMetadata>();

        foreach (var operationId in operationIds)
        {
            if (operationsById.TryGetValue(operationId, out var matchingOperations))
            {
                operations.AddRange(matchingOperations);
            }
        }

        return operations;
    }

    private static SwaggerSyncWatermarkMetadata? ReadWatermark(JsonElement entity)
    {
        if (!entity.TryGetProperty("watermark", out var watermarkElement) ||
            watermarkElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new SwaggerSyncWatermarkMetadata
        {
            Field = GetRequiredString(watermarkElement, "field"),
            TieBreakers = GetStringArray(watermarkElement, "tieBreakers")
        };
    }

    private static IReadOnlyList<SwaggerSyncReferenceMetadata> ReadReferences(JsonElement entity)
    {
        if (!entity.TryGetProperty("references", out var referencesElement) ||
            referencesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var references = new List<SwaggerSyncReferenceMetadata>();
        foreach (var reference in referencesElement.EnumerateArray())
        {
            references.Add(new SwaggerSyncReferenceMetadata
            {
                Source = GetRequiredString(reference, "source"),
                LocalColumn = GetRequiredString(reference, "localColumn"),
                TargetEntity = GetRequiredString(reference, "targetEntity"),
                TargetKey = GetOptionalString(reference, "targetKey") ?? "id",
                Nullability = GetOptionalString(reference, "nullability"),
                Enforce = GetOptionalString(reference, "enforce"),
                OnDelete = GetOptionalString(reference, "onDelete"),
                FlatFallbackColumns = GetStringArray(reference, "flatFallbackColumns")
            });
        }

        return references;
    }

    private static SwaggerSyncEntityMetadata Merge(
        SwaggerSyncEntityMetadata existing,
        SwaggerSyncEntityMetadata incoming)
    {
        RequireSame(existing.Key, "primaryKey", existing.PrimaryKey, incoming.PrimaryKey);
        RequireSame(existing.Key, "table", existing.Table, incoming.Table);
        RequireSame(existing.Key, "collection", existing.Collection, incoming.Collection);
        RequireSame(existing.Key, existing.Watermark, incoming.Watermark);

        var operationIds = existing.OperationIds
            .Concat(incoming.OperationIds)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var documentNames = existing.DocumentNames
            .Concat(incoming.DocumentNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var operations = MergeOperations(existing.Operations, incoming.Operations);
        var references = MergeReferences(existing.Key, existing.References, incoming.References);
        var scalarFields = MergeScalarFields(existing.Key, existing.ScalarFields, incoming.ScalarFields);

        return existing with
        {
            OperationIds = operationIds,
            Operations = operations,
            DocumentNames = documentNames,
            ScalarFields = scalarFields,
            References = references
        };
    }

    private static SwaggerSyncSchemaMetadata Merge(
        SwaggerSyncSchemaMetadata existing,
        SwaggerSyncSchemaMetadata incoming)
    {
        RequireSame(existing.SchemaName, "schema", existing.Schema, incoming.Schema);
        RequireSame(existing.SchemaName, "type", existing.Type, incoming.Type);
        RequireSame(existing.SchemaName, "primaryKey", existing.PrimaryKey, incoming.PrimaryKey);
        RequireSame(existing.SchemaName, existing.Watermark, incoming.Watermark);

        var operationIds = existing.OperationIds
            .Concat(incoming.OperationIds)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var documentNames = existing.DocumentNames
            .Concat(incoming.DocumentNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var operations = MergeOperations(existing.Operations, incoming.Operations);
        var references = MergeReferences(existing.SchemaName, existing.References, incoming.References);
        var scalarFields = MergeScalarFields(existing.SchemaName, existing.ScalarFields, incoming.ScalarFields);

        return existing with
        {
            OperationIds = operationIds,
            Operations = operations,
            DocumentNames = documentNames,
            ScalarFields = scalarFields,
            References = references
        };
    }

    private static IReadOnlyList<SwaggerSyncOperationMetadata> MergeOperations(
        IEnumerable<SwaggerSyncOperationMetadata> existing,
        IEnumerable<SwaggerSyncOperationMetadata> incoming)
    {
        return existing
            .Concat(incoming)
            .DistinctBy(x => $"{x.DocumentName}|{x.Method}|{x.Path}|{x.OperationId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<SwaggerSyncReferenceMetadata> MergeReferences(
        string entityKey,
        IEnumerable<SwaggerSyncReferenceMetadata> existing,
        IEnumerable<SwaggerSyncReferenceMetadata> incoming)
    {
        var references = new Dictionary<string, SwaggerSyncReferenceMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in existing.Concat(incoming))
        {
            if (!references.TryGetValue(reference.Source, out var previous))
            {
                references.Add(reference.Source, reference);
                continue;
            }

            if (!ReferenceEquivalent(previous, reference))
            {
                throw new InvalidOperationException(
                    $"Conflicting sync metadata for entity '{entityKey}' reference '{reference.Source}'.");
            }
        }

        return references.Values.ToArray();
    }

    private static IReadOnlyList<SwaggerSyncScalarFieldMetadata> MergeScalarFields(
        string entityKey,
        IReadOnlyList<SwaggerSyncScalarFieldMetadata> existing,
        IReadOnlyList<SwaggerSyncScalarFieldMetadata> incoming)
    {
        if (existing.Count == 0)
        {
            return incoming;
        }

        if (incoming.Count == 0)
        {
            return existing;
        }

        if (existing.Count != incoming.Count)
        {
            throw new InvalidOperationException(
                $"Conflicting sync metadata for entity '{entityKey}': scalar fields differ.");
        }

        foreach (var pair in existing.Zip(incoming))
        {
            if (!ScalarFieldEquivalent(pair.First, pair.Second))
            {
                throw new InvalidOperationException(
                    $"Conflicting sync metadata for entity '{entityKey}': scalar field '{pair.First.Source}' differs.");
            }
        }

        return existing;
    }

    private static bool ReferenceEquivalent(
        SwaggerSyncReferenceMetadata left,
        SwaggerSyncReferenceMetadata right)
    {
        return StringEquals(left.LocalColumn, right.LocalColumn) &&
               StringEquals(left.TargetEntity, right.TargetEntity) &&
               StringEquals(left.TargetKey, right.TargetKey) &&
               StringSequenceEquals(left.FlatFallbackColumns, right.FlatFallbackColumns);
    }

    private static bool ScalarFieldEquivalent(
        SwaggerSyncScalarFieldMetadata left,
        SwaggerSyncScalarFieldMetadata right)
    {
        return StringEquals(left.Source, right.Source) &&
               StringEquals(left.LocalColumn, right.LocalColumn) &&
               StringEquals(left.Type, right.Type) &&
               StringEquals(left.Format, right.Format) &&
               left.IsNullable == right.IsNullable;
    }

    private static IReadOnlyList<SwaggerSyncScalarFieldMetadata> ReadScalarFields(
        JsonElement root,
        string? schemaReference)
    {
        var schemaName = GetSchemaName(schemaReference);
        if (schemaName == null ||
            !TryGetComponentSchema(root, schemaName, out var schema) ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var fields = new List<SwaggerSyncScalarFieldMetadata>();
        foreach (var property in properties.EnumerateObject())
        {
            if (!TryReadScalarProperty(property.Value, out var type, out var format, out var isNullable))
            {
                continue;
            }

            fields.Add(new SwaggerSyncScalarFieldMetadata
            {
                Source = property.Name,
                LocalColumn = ToStorageColumnName(property.Name),
                Type = type,
                Format = format,
                IsNullable = isNullable
            });
        }

        return fields;
    }

    private static bool TryGetComponentSchema(
        JsonElement root,
        string schemaName,
        out JsonElement schema)
    {
        schema = default;
        return root.TryGetProperty("components", out var components) &&
               components.TryGetProperty("schemas", out var schemas) &&
               schemas.TryGetProperty(schemaName, out schema) &&
               schema.ValueKind == JsonValueKind.Object;
    }

    private static string? GetSchemaName(string? schemaReference)
    {
        if (string.IsNullOrWhiteSpace(schemaReference))
        {
            return null;
        }

        const string prefix = "#/components/schemas/";
        return schemaReference.StartsWith(prefix, StringComparison.Ordinal)
            ? schemaReference[prefix.Length..]
            : schemaReference;
    }

    private static bool TryReadScalarProperty(
        JsonElement property,
        out string type,
        out string? format,
        out bool isNullable)
    {
        type = "";
        format = null;
        isNullable = property.TryGetProperty("nullable", out var nullableElement) &&
                     nullableElement.ValueKind == JsonValueKind.True;

        if (property.TryGetProperty("$ref", out _) ||
            !property.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = typeElement.GetString();
        if (candidate is not ("string" or "integer" or "number" or "boolean"))
        {
            return false;
        }

        type = candidate;
        format = GetOptionalString(property, "format");
        return true;
    }

    private static string ToStorageColumnName(string path)
    {
        return string.Join("_", path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToSnakeCase));
    }

    private static string ToSnakeCase(string value)
    {
        var chars = new List<char>(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) &&
                index > 0 &&
                chars.Count > 0 &&
                chars[^1] != '_')
            {
                chars.Add('_');
            }

            chars.Add(char.ToLowerInvariant(current));
        }

        return new string(chars.ToArray());
    }

    private static void RequireSame(
        string entityKey,
        string fieldName,
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (!StringSequenceEquals(left, right))
        {
            throw new InvalidOperationException(
                $"Conflicting sync metadata for entity '{entityKey}': {fieldName} differs.");
        }
    }

    private static void RequireSame(
        string entityKey,
        string fieldName,
        string? left,
        string? right)
    {
        if (!StringEquals(left, right))
        {
            throw new InvalidOperationException(
                $"Conflicting sync metadata for entity '{entityKey}': {fieldName} differs.");
        }
    }

    private static void RequireSame(
        string entityKey,
        SwaggerSyncWatermarkMetadata? left,
        SwaggerSyncWatermarkMetadata? right)
    {
        if (WatermarkEquivalent(left, right))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Conflicting sync metadata for entity '{entityKey}': watermark differs.");
    }

    private static bool WatermarkEquivalent(
        SwaggerSyncWatermarkMetadata? left,
        SwaggerSyncWatermarkMetadata? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return StringEquals(left.Field, right.Field) &&
               StringSequenceEquals(left.TieBreakers, right.TieBreakers);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return GetOptionalString(element, propertyName)
               ?? throw new InvalidOperationException($"Sync metadata property '{propertyName}' is required.");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.GetString();
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
    }

    private static bool StringEquals(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StringSequenceEquals(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    }
}
