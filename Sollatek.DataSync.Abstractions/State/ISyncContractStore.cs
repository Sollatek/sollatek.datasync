#nullable enable

using Sollatek.DataSync.Sync.Contract;

namespace Sollatek.DataSync.State;

public interface ISyncContractStore
{
    Task<SyncContractSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        SyncContractSnapshot snapshot,
        CancellationToken cancellationToken);
}
