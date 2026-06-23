#nullable enable

namespace Sollatek.DataSync.Execution;

public static class SyncDateRangeSplitter
{
    public static IReadOnlyList<SyncDateRange> Split(
        SyncDateRange range,
        TimeSpan maxRange)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (maxRange <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("maxRange must be greater than zero.");
        }

        if (range.End <= range.Start)
        {
            return [];
        }

        var ranges = new List<SyncDateRange>();
        var start = range.Start;
        while (start < range.End)
        {
            var end = start + maxRange;
            if (end > range.End)
            {
                end = range.End;
            }

            ranges.Add(new SyncDateRange(start, end, IncludeEndFilter: true));
            start = end;
        }

        return ranges;
    }
}
