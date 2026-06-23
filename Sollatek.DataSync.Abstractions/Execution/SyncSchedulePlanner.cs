#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Execution;

public static class SyncSchedulePlanner
{
    public static SyncScheduleRunPlan GetStartupPlan(
        SyncOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.StartupMode == SyncStartupMode.Disabled)
        {
            var next = GetNextPlan(options, now);
            return next with { ShouldRun = false };
        }

        return new SyncScheduleRunPlan(
            ShouldRun: true,
            Delay: TimeSpan.Zero,
            RangeEnd: GetCurrentCompletedPeriodEnd(options, now));
    }

    public static SyncScheduleRunPlan GetNextPlan(
        SyncOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);

        var nextDueAt = GetNextDueAt(options, now);
        return new SyncScheduleRunPlan(
            ShouldRun: true,
            Delay: GetPositiveDelay(nextDueAt - now),
            RangeEnd: GetCompletedPeriodEndForDueTime(options, nextDueAt));
    }

    public static DateTimeOffset GetCurrentCompletedPeriodEnd(
        SyncOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GetCurrentCompletedPeriodEndCore(options, now);
    }

    public static int GetLagPeriods(
        SyncOptions options,
        DateTimeOffset completedRangeEnd,
        DateTimeOffset expectedCompletedRangeEnd)
    {
        ArgumentNullException.ThrowIfNull(options);

        var start = completedRangeEnd.ToUniversalTime();
        var end = expectedCompletedRangeEnd.ToUniversalTime();
        if (end <= start)
        {
            return 0;
        }

        var duration = end - start;
        return options.Schedule.Mode switch
        {
            SyncScheduleMode.FixedDelay => CeilingPeriodCount(duration, options.RunInterval),
            SyncScheduleMode.Hourly => CeilingPeriodCount(duration, TimeSpan.FromHours(1)),
            SyncScheduleMode.Daily => CeilingPeriodCount(duration, TimeSpan.FromDays(1)),
            SyncScheduleMode.Weekly => CeilingPeriodCount(duration, TimeSpan.FromDays(7)),
            SyncScheduleMode.Monthly => CountMonthBoundaries(start, end),
            _ => throw new InvalidOperationException("Unsupported sync schedule mode.")
        };
    }

    private static DateTimeOffset GetNextDueAt(SyncOptions options, DateTimeOffset now)
    {
        var utcNow = now.ToUniversalTime();
        return options.Schedule.Mode switch
        {
            SyncScheduleMode.FixedDelay => utcNow + options.RunInterval,
            SyncScheduleMode.Hourly => GetNextHourlyDueAt(options.Schedule, utcNow),
            SyncScheduleMode.Daily => GetNextDailyDueAt(options.Schedule, utcNow),
            SyncScheduleMode.Weekly => GetNextWeeklyDueAt(options.Schedule, utcNow),
            SyncScheduleMode.Monthly => GetNextMonthlyDueAt(options.Schedule, utcNow),
            _ => throw new InvalidOperationException("Unsupported sync schedule mode.")
        };
    }

    private static DateTimeOffset GetCurrentCompletedPeriodEndCore(SyncOptions options, DateTimeOffset now)
    {
        var utcNow = now.ToUniversalTime();
        return options.Schedule.Mode switch
        {
            SyncScheduleMode.FixedDelay => utcNow,
            SyncScheduleMode.Hourly => TruncateToHour(utcNow),
            SyncScheduleMode.Daily => StartOfDay(utcNow),
            SyncScheduleMode.Weekly => StartOfWeek(utcNow, options.Schedule.DayOfWeek),
            SyncScheduleMode.Monthly => StartOfMonth(utcNow),
            _ => throw new InvalidOperationException("Unsupported sync schedule mode.")
        };
    }

    private static DateTimeOffset GetCompletedPeriodEndForDueTime(
        SyncOptions options,
        DateTimeOffset dueAt)
    {
        var utcDueAt = dueAt.ToUniversalTime();
        return options.Schedule.Mode switch
        {
            SyncScheduleMode.FixedDelay => utcDueAt,
            SyncScheduleMode.Hourly => TruncateToHour(utcDueAt),
            SyncScheduleMode.Daily => StartOfDay(utcDueAt),
            SyncScheduleMode.Weekly => StartOfWeek(utcDueAt, options.Schedule.DayOfWeek),
            SyncScheduleMode.Monthly => StartOfMonth(utcDueAt),
            _ => throw new InvalidOperationException("Unsupported sync schedule mode.")
        };
    }

    private static DateTimeOffset GetNextHourlyDueAt(
        SyncScheduleOptions schedule,
        DateTimeOffset now)
    {
        var dueAt = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            schedule.Minute,
            0,
            TimeSpan.Zero);

        return dueAt > now ? dueAt : dueAt.AddHours(1);
    }

    private static DateTimeOffset GetNextDailyDueAt(
        SyncScheduleOptions schedule,
        DateTimeOffset now)
    {
        var dueAt = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            schedule.Time.Hour,
            schedule.Time.Minute,
            schedule.Time.Second,
            TimeSpan.Zero);

        return dueAt > now ? dueAt : dueAt.AddDays(1);
    }

    private static DateTimeOffset GetNextWeeklyDueAt(
        SyncScheduleOptions schedule,
        DateTimeOffset now)
    {
        var todayDueAt = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            schedule.Time.Hour,
            schedule.Time.Minute,
            schedule.Time.Second,
            TimeSpan.Zero);
        var daysUntilDueDay = ((int)schedule.DayOfWeek - (int)now.DayOfWeek + 7) % 7;
        var dueAt = todayDueAt.AddDays(daysUntilDueDay);
        return dueAt > now ? dueAt : dueAt.AddDays(7);
    }

    private static DateTimeOffset GetNextMonthlyDueAt(
        SyncScheduleOptions schedule,
        DateTimeOffset now)
    {
        var dueAt = new DateTimeOffset(
            now.Year,
            now.Month,
            schedule.DayOfMonth,
            schedule.Time.Hour,
            schedule.Time.Minute,
            schedule.Time.Second,
            TimeSpan.Zero);

        return dueAt > now ? dueAt : dueAt.AddMonths(1);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset StartOfMonth(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value, DayOfWeek firstDayOfWeek)
    {
        var startOfDay = StartOfDay(value);
        var diff = ((int)startOfDay.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return startOfDay.AddDays(-diff);
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
    }

    private static TimeSpan GetPositiveDelay(TimeSpan delay)
    {
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static int CeilingPeriodCount(TimeSpan duration, TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Max(0, Convert.ToInt32(Math.Ceiling(duration.TotalSeconds / period.TotalSeconds)));
    }

    private static int CountMonthBoundaries(DateTimeOffset start, DateTimeOffset end)
    {
        var cursor = StartOfMonth(start);
        if (cursor < start)
        {
            cursor = cursor.AddMonths(1);
        }

        var count = 0;
        while (cursor < end)
        {
            count++;
            cursor = cursor.AddMonths(1);
        }

        return count;
    }
}

public sealed record SyncScheduleRunPlan(
    bool ShouldRun,
    TimeSpan Delay,
    DateTimeOffset RangeEnd);
