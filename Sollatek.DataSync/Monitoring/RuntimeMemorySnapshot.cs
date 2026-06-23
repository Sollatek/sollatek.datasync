#nullable enable

using System.Diagnostics;

namespace Sollatek.DataSync.Monitoring;

internal static class RuntimeMemorySnapshot
{
    public static SyncRunStatus Capture(SyncRunStatus status)
    {
        using var process = Process.GetCurrentProcess();
        return status with
        {
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
            WorkingSetBytes = process.WorkingSet64,
            PrivateMemoryBytes = process.PrivateMemorySize64,
            PeakWorkingSetBytes = process.PeakWorkingSet64
        };
    }
}
