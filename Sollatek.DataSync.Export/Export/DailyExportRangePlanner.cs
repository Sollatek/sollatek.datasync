#nullable enable

using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Export;

public static class DailyExportRangePlanner
{
    public static IReadOnlyList<DailyExportRange> Split(SyncDateRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        var end = range.End.ToUniversalTime();
        var cursor = range.Start.ToUniversalTime();
        var days = new List<DailyExportRange>();

        while (cursor < end)
        {
            var day = DateOnly.FromDateTime(cursor.UtcDateTime);
            var nextDay = new DateTimeOffset(
                day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1),
                TimeSpan.Zero);
            var segmentEnd = nextDay < end ? nextDay : end;

            days.Add(new DailyExportRange(
                day,
                new SyncDateRange(cursor, segmentEnd)));
            cursor = segmentEnd;
        }

        return days;
    }
}
