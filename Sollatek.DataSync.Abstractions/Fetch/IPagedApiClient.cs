#nullable enable

using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Sync.Metadata;

namespace Sollatek.DataSync.Fetch;

public interface IPagedApiClient
{
    Task<PagedApiPage> GetPageAsync(
        SwaggerSyncEntityMetadata metadata,
        SyncDateRange range,
        int top,
        int skip,
        CancellationToken cancellationToken);
}
