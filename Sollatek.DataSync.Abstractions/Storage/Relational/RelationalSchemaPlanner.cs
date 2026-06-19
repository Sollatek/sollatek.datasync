#nullable enable

using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Storage.Relational;

public static class RelationalSchemaPlanner
{
    public static RelationalTablePlan Plan(
        SwaggerSyncEntityMetadata entity,
        IEnumerable<string> selectedEntityKeys,
        IReadOnlyDictionary<string, SwaggerSyncEntityMetadata>? knownEntities = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var selected = selectedEntityKeys?.ToArray() ?? [];
        var columns = new List<RelationalColumnPlan>();
        var foreignKeys = new List<RelationalForeignKeyPlan>();

        foreach (var primaryKey in entity.PrimaryKey)
        {
            columns.Add(new RelationalColumnPlan(
                RelationalColumnName.FromMetadataPath(primaryKey),
                primaryKey,
                RelationalColumnRole.PrimaryKey));
        }

        foreach (var reference in entity.References)
        {
            var decision = SyncReferenceStoragePlanner.Decide(reference, selected);
            var useForeignKey = decision.Mode == SyncReferenceStorageMode.ForeignKey &&
                !string.Equals(decision.TargetEntity, entity.Key, StringComparison.OrdinalIgnoreCase);
            var role = useForeignKey
                ? RelationalColumnRole.ReferenceForeignKey
                : RelationalColumnRole.ReferenceFlatValue;

            columns.Add(new RelationalColumnPlan(
                decision.LocalColumn,
                reference.Source,
                role));

            if (useForeignKey)
            {
                foreignKeys.Add(BuildForeignKey(decision, knownEntities));
            }
        }

        if (entity.Watermark != null)
        {
            AddScalarColumnIfMissing(columns, entity.Watermark.Field);
        }

        foreach (var field in entity.ScalarFields)
        {
            var columnName = string.IsNullOrWhiteSpace(field.LocalColumn)
                ? RelationalColumnName.FromMetadataPath(field.Source)
                : field.LocalColumn;

            AddScalarColumnIfMissing(columns, field.Source, columnName);
        }

        EnsureUniqueColumnNames(entity, columns);

        return new RelationalTablePlan(
            entity.Key,
            entity.Table ?? entity.Key,
            columns,
            columns
                .Where(x => x.Role == RelationalColumnRole.PrimaryKey)
                .Select(x => x.Name)
                .ToArray(),
            foreignKeys);
    }

    private static void AddScalarColumnIfMissing(
        ICollection<RelationalColumnPlan> columns,
        string source,
        string? columnName = null)
    {
        var resolvedColumnName = string.IsNullOrWhiteSpace(columnName)
            ? RelationalColumnName.FromMetadataPath(source)
            : columnName;

        if (columns.Any(x => string.Equals(x.Name, resolvedColumnName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        columns.Add(new RelationalColumnPlan(
            resolvedColumnName,
            source,
            RelationalColumnRole.Scalar));
    }

    private static RelationalForeignKeyPlan BuildForeignKey(
        SyncReferenceStorageDecision decision,
        IReadOnlyDictionary<string, SwaggerSyncEntityMetadata>? knownEntities)
    {
        var targetTable = decision.TargetEntity;
        if (knownEntities?.TryGetValue(decision.TargetEntity, out var targetEntity) == true &&
            !string.IsNullOrWhiteSpace(targetEntity.Table))
        {
            targetTable = targetEntity.Table;
        }

        return new RelationalForeignKeyPlan(
            decision.LocalColumn,
            decision.TargetEntity,
            targetTable,
            RelationalColumnName.FromMetadataPath(decision.TargetKey),
            IsNullable: true,
            RelationalForeignKeyDeleteBehavior.NoAction);
    }

    private static void EnsureUniqueColumnNames(
        SwaggerSyncEntityMetadata entity,
        IReadOnlyList<RelationalColumnPlan> columns)
    {
        var duplicate = columns
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate relational column '{duplicate.Key}' planned for sync entity '{entity.Key}'.");
        }
    }
}
