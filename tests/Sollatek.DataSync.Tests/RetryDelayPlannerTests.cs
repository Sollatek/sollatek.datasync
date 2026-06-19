using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Tests;

public sealed class RetryDelayPlannerTests
{
    [Fact]
    public void GetNextRetryTime_UsesFixedDelay()
    {
        var now = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
        var options = new RetryOptions
        {
            MaxTries = 3,
            Period = TimeSpan.FromMinutes(5),
            DelayFunction = RetryDelayFunction.Fixed
        };

        var next = RetryDelayPlanner.GetNextRetryTime(now, tryNumber: 2, options);

        Assert.Equal(now.AddMinutes(5), next);
    }

    [Fact]
    public void GetNextRetryTime_UsesLinearDelay()
    {
        var now = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);
        var options = new RetryOptions
        {
            MaxTries = 3,
            Period = TimeSpan.FromMinutes(5),
            DelayFunction = RetryDelayFunction.Linear
        };

        var next = RetryDelayPlanner.GetNextRetryTime(now, tryNumber: 2, options);

        Assert.Equal(now.AddMinutes(10), next);
    }

    [Fact]
    public void GetNextRetryTime_RejectsInvalidTryNumber()
    {
        var options = RetryOptions.Default;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RetryDelayPlanner.GetNextRetryTime(DateTimeOffset.UtcNow, tryNumber: 0, options));

        Assert.Contains("tryNumber must be greater than 0", exception.Message);
    }
}
