#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public sealed record RelationalCommand(
    string Sql,
    IReadOnlyList<RelationalCommandParameter> Parameters);
