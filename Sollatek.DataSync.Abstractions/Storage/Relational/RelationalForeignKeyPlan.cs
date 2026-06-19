#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public sealed record RelationalForeignKeyPlan(
    string ColumnName,
    string TargetEntity,
    string TargetTable,
    string TargetColumn,
    bool IsNullable,
    RelationalForeignKeyDeleteBehavior OnDelete);
