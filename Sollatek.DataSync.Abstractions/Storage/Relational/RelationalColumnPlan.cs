#nullable enable

namespace Sollatek.DataSync.Storage.Relational;

public sealed record RelationalColumnPlan(
    string Name,
    string Source,
    RelationalColumnRole Role);
