#nullable enable

namespace Sollatek.DataSync.Execution;

public sealed record SyncDateRange
{
    public SyncDateRange(
        DateTimeOffset start,
        DateTimeOffset end,
        bool IncludeEndFilter = true)
    {
        if (end < start)
        {
            throw new ArgumentException("Sync date range end must be greater than or equal to start.");
        }

        Start = start;
        End = end;
        this.IncludeEndFilter = IncludeEndFilter;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public bool IncludeEndFilter { get; }
}
