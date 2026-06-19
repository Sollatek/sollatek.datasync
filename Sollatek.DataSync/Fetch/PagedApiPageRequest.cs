#nullable enable

namespace Sollatek.DataSync.Fetch;

public sealed record PagedApiPageRequest(
    string EntityKey,
    string PathAndQuery);
