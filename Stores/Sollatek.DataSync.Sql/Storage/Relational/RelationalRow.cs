#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public sealed record RelationalRow(
    string EntityKey,
    string TableName,
    IReadOnlyDictionary<string, object?> Values);
