#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public sealed record RelationalTablePlan(
    string EntityKey,
    string TableName,
    IReadOnlyList<RelationalColumnPlan> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<RelationalForeignKeyPlan> ForeignKeys);
