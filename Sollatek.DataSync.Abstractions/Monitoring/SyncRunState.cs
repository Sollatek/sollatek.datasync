#nullable enable

namespace Sollatek.DataSync.Monitoring;

public enum SyncRunState
{
    Idle,
    Running,
    ProcessingEntity,
    WaitingToRetry,
    Failed,
    Succeeded
}
