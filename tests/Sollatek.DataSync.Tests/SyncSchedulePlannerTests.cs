using Sollatek.DataSync.Config;
using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Tests;

public sealed class SyncSchedulePlannerTests
{
    [Fact]
    public void GetStartupPlan_DailyHistoricalOnlyEndsAtCurrentUtcDayStart()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            StartupMode = SyncStartupMode.HistoricalOnly,
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Daily,
                Time = new TimeOnly(1, 0)
            }
        };

        var plan = SyncSchedulePlanner.GetStartupPlan(
            options,
            new DateTimeOffset(2026, 6, 22, 13, 30, 0, TimeSpan.Zero));

        Assert.True(plan.ShouldRun);
        Assert.Equal(TimeSpan.Zero, plan.Delay);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero), plan.RangeEnd);
    }

    [Fact]
    public void GetStartupPlan_WeeklyHistoricalOnlyEndsAtCurrentWeekStart()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            StartupMode = SyncStartupMode.HistoricalOnly,
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Weekly,
                DayOfWeek = DayOfWeek.Monday,
                Time = new TimeOnly(1, 0)
            }
        };

        var plan = SyncSchedulePlanner.GetStartupPlan(
            options,
            new DateTimeOffset(2026, 6, 24, 13, 30, 0, TimeSpan.Zero));

        Assert.True(plan.ShouldRun);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero), plan.RangeEnd);
    }

    [Fact]
    public void GetNextPlan_DailySchedulesNextConfiguredTime()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Daily,
                Time = new TimeOnly(1, 0)
            }
        };

        var plan = SyncSchedulePlanner.GetNextPlan(
            options,
            new DateTimeOffset(2026, 6, 22, 0, 30, 0, TimeSpan.Zero));

        Assert.True(plan.ShouldRun);
        Assert.Equal(TimeSpan.FromMinutes(30), plan.Delay);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero), plan.RangeEnd);
    }

    [Fact]
    public void GetNextPlan_HourlySchedulesNextConfiguredMinute()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Hourly,
                Minute = 15
            }
        };

        var plan = SyncSchedulePlanner.GetNextPlan(
            options,
            new DateTimeOffset(2026, 6, 22, 10, 5, 0, TimeSpan.Zero));

        Assert.True(plan.ShouldRun);
        Assert.Equal(TimeSpan.FromMinutes(10), plan.Delay);
        Assert.Equal(new DateTimeOffset(2026, 6, 22, 10, 0, 0, TimeSpan.Zero), plan.RangeEnd);
    }

    [Fact]
    public void GetNextPlan_MonthlySchedulesNextConfiguredDay()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 3, 22, 0, 0, 0, TimeSpan.Zero),
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Monthly,
                DayOfMonth = 1,
                Time = new TimeOnly(2, 0)
            }
        };

        var plan = SyncSchedulePlanner.GetNextPlan(
            options,
            new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

        Assert.True(plan.ShouldRun);
        Assert.Equal(TimeSpan.FromHours(14), plan.Delay);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), plan.RangeEnd);
    }

    [Fact]
    public void GetCurrentCompletedPeriodEnd_DailyReturnsCurrentUtcDayStart()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Daily,
                Time = new TimeOnly(1, 0)
            }
        };

        var completed = SyncSchedulePlanner.GetCurrentCompletedPeriodEnd(
            options,
            new DateTimeOffset(2026, 6, 23, 4, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero), completed);
    }

    [Fact]
    public void GetLagPeriods_DailyRoundsUpPartialPeriods()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Daily
            }
        };

        var periods = SyncSchedulePlanner.GetLagPeriods(
            options,
            new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, periods);
    }

    [Fact]
    public void GetLagPeriods_MonthlyCountsMonthBoundaries()
    {
        var options = new SyncOptions
        {
            StartFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Schedule = new SyncScheduleOptions
            {
                Mode = SyncScheduleMode.Monthly
            }
        };

        var periods = SyncSchedulePlanner.GetLagPeriods(
            options,
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, periods);
    }
}
