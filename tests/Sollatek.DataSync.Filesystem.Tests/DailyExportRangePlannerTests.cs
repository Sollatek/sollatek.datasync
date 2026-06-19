using Sollatek.DataSync.Execution;
using Sollatek.DataSync.Export;

namespace Sollatek.DataSync.Tests;

public sealed class DailyExportRangePlannerTests
{
    [Fact]
    public void Split_ReturnsSingleRangeWhenStartAndEndAreOnSameDay()
    {
        var range = new SyncDateRange(
            new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero));

        var days = DailyExportRangePlanner.Split(range);

        var day = Assert.Single(days);
        Assert.Equal(new DateOnly(2026, 6, 18), day.Day);
        Assert.Equal(range.Start, day.Range.Start);
        Assert.Equal(range.End, day.Range.End);
    }

    [Fact]
    public void Split_CutsRangeAtUtcDayBoundaries()
    {
        var range = new SyncDateRange(
            new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 3, 0, 0, TimeSpan.Zero));

        var days = DailyExportRangePlanner.Split(range);

        Assert.Collection(
            days,
            first =>
            {
                Assert.Equal(new DateOnly(2026, 6, 18), first.Day);
                Assert.Equal(new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero), first.Range.Start);
                Assert.Equal(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), first.Range.End);
            },
            second =>
            {
                Assert.Equal(new DateOnly(2026, 6, 19), second.Day);
                Assert.Equal(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), second.Range.Start);
                Assert.Equal(new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), second.Range.End);
            },
            third =>
            {
                Assert.Equal(new DateOnly(2026, 6, 20), third.Day);
                Assert.Equal(new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), third.Range.Start);
                Assert.Equal(new DateTimeOffset(2026, 6, 20, 3, 0, 0, TimeSpan.Zero), third.Range.End);
            });
    }

    [Fact]
    public void Split_DoesNotCreateEmptyRangeWhenEndIsAtMidnight()
    {
        var range = new SyncDateRange(
            new DateTimeOffset(2026, 6, 18, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));

        var days = DailyExportRangePlanner.Split(range);

        Assert.Equal([new DateOnly(2026, 6, 18), new DateOnly(2026, 6, 19)], days.Select(x => x.Day));
    }
}
