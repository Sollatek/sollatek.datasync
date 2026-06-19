#nullable enable

using System.Text.Json;

namespace Sollatek.DataSync.Fetch;

public sealed record PagedApiPage(
    IReadOnlyList<JsonElement> Rows,
    int TotalCount,
    int TotalPages);
