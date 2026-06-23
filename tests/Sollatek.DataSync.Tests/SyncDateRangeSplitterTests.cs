using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Tests;

public sealed class SyncDateRangeSplitterTests
{
    [Fact]
    public void Split_ReturnsBoundedRangesInOrder()
    {
        var ranges = SyncDateRangeSplitter.Split(
            new SyncDateRange(
                new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero)),
            TimeSpan.FromDays(1));

        Assert.Equal(3, ranges.Count);
        Assert.Equal(new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero), ranges[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero), ranges[0].End);
        Assert.Equal(new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero), ranges[1].Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero), ranges[1].End);
        Assert.Equal(new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero), ranges[2].Start);
        Assert.Equal(new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero), ranges[2].End);
        Assert.All(ranges, range => Assert.True(range.IncludeEndFilter));
    }
}
