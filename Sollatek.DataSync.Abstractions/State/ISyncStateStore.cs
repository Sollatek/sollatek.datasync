#nullable enable

namespace Sollatek.DataSync.State;

public interface ISyncStateStore
{
    Task<DateTimeOffset?> GetLastSuccessfulEndAsync(
        string entityKey,
        CancellationToken cancellationToken);

    Task SaveSuccessfulEndAsync(
        string entityKey,
        DateTimeOffset end,
        CancellationToken cancellationToken);
}
