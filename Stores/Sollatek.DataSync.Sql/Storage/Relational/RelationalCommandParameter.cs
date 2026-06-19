#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public sealed record RelationalCommandParameter(
    string Name,
    string Placeholder,
    object? Value);
