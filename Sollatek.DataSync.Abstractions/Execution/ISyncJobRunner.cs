#nullable enable

namespace Sollatek.DataSync.Execution;

public interface ISyncJobRunner
{
    Task RunAsync(
        string runId,
        IReadOnlyList<SyncJob> jobs,
        CancellationToken cancellationToken);
}
