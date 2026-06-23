#nullable enable

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Config;

public sealed record SyncOptions
{
    public required DateTimeOffset StartFrom { get; init; }

    public bool RunOnStartup { get; init; } = true;

    public SyncStartupMode StartupMode { get; init; } = SyncStartupMode.Immediate;

    public bool StopWhenFinished { get; init; }

    public TimeSpan RunInterval { get; init; } = TimeSpan.FromHours(6);

    public SyncScheduleOptions Schedule { get; init; } = SyncScheduleOptions.FixedDelay;

    public int MaxPageSize { get; init; } = 500;

    public TimeSpan ApiRequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public SyncTransferMode TransferMode { get; init; } = SyncTransferMode.AsyncExport;

    public static SyncOptions FromConfiguration(
        IConfiguration configuration,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var currentTime = now ?? DateTimeOffset.UtcNow;

        var startupMode = GetStartupMode(configuration);
        return new SyncOptions
        {
            StartFrom = GetStartFrom(configuration, currentTime),
            RunOnStartup = startupMode != SyncStartupMode.Disabled,
            StartupMode = startupMode,
            StopWhenFinished = GetBool(configuration, "Sync:stopWhenFinished", defaultValue: false),
            RunInterval = GetPositiveTimeSpan(configuration, "Sync:runInterval", TimeSpan.FromHours(6)),
            Schedule = GetSchedule(configuration),
            MaxPageSize = GetPositiveInt(configuration, "Sync:maxPageSize", 500),
            ApiRequestTimeout = GetPositiveTimeSpan(
                configuration,
                "Sync:apiRequestTimeout",
                TimeSpan.FromMinutes(5)),
            TransferMode = GetTransferMode(configuration)
        };
    }

    private static SyncStartupMode GetStartupMode(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("Sync:runOnStartup");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return SyncStartupMode.Immediate;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "true" => SyncStartupMode.Immediate,
            "false" => SyncStartupMode.Disabled,
            "historicalonly" => SyncStartupMode.HistoricalOnly,
            "historical-only" => SyncStartupMode.HistoricalOnly,
            _ => throw new InvalidOperationException(
                "Sync:runOnStartup must be true, false, or historicalOnly.")
        };
    }

    private static SyncScheduleOptions GetSchedule(IConfiguration configuration)
    {
        var mode = GetScheduleMode(configuration);
        var scheduleSection = configuration.GetSection("Sync:schedule");
        return mode switch
        {
            SyncScheduleMode.FixedDelay => SyncScheduleOptions.FixedDelay,
            SyncScheduleMode.Hourly => new SyncScheduleOptions
            {
                Mode = mode,
                Minute = GetMinute(scheduleSection)
            },
            SyncScheduleMode.Daily => new SyncScheduleOptions
            {
                Mode = mode,
                Time = GetTime(scheduleSection)
            },
            SyncScheduleMode.Weekly => new SyncScheduleOptions
            {
                Mode = mode,
                DayOfWeek = GetDayOfWeek(scheduleSection),
                Time = GetTime(scheduleSection)
            },
            SyncScheduleMode.Monthly => new SyncScheduleOptions
            {
                Mode = mode,
                DayOfMonth = GetDayOfMonth(scheduleSection),
                Time = GetTime(scheduleSection)
            },
            _ => throw new InvalidOperationException("Unsupported Sync:schedule:mode.")
        };
    }

    private static SyncScheduleMode GetScheduleMode(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("Sync:schedule:mode");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return SyncScheduleMode.FixedDelay;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "fixeddelay" => SyncScheduleMode.FixedDelay,
            "fixed-delay" => SyncScheduleMode.FixedDelay,
            "interval" => SyncScheduleMode.FixedDelay,
            "hourly" => SyncScheduleMode.Hourly,
            "daily" => SyncScheduleMode.Daily,
            "weekly" => SyncScheduleMode.Weekly,
            "monthly" => SyncScheduleMode.Monthly,
            _ => throw new InvalidOperationException(
                "Sync:schedule:mode must be one of: fixedDelay, hourly, daily, weekly, monthly.")
        };
    }

    private static TimeOnly GetTime(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("time");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return TimeOnly.MinValue;
        }

        if (TimeOnly.TryParse(configuredValue, CultureInfo.InvariantCulture, out var time))
        {
            return time;
        }

        throw new InvalidOperationException("Sync:schedule:time must be a valid time value.");
    }

    private static DayOfWeek GetDayOfWeek(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("dayOfWeek");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return DayOfWeek.Monday;
        }

        if (Enum.TryParse<DayOfWeek>(configuredValue, ignoreCase: true, out var dayOfWeek))
        {
            return dayOfWeek;
        }

        throw new InvalidOperationException("Sync:schedule:dayOfWeek must be a valid day of week.");
    }

    private static int GetDayOfMonth(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("dayOfMonth");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return 1;
        }

        if (int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value is >= 1 and <= 28)
        {
            return value;
        }

        throw new InvalidOperationException("Sync:schedule:dayOfMonth must be between 1 and 28.");
    }

    private static int GetMinute(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("minute");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return 0;
        }

        if (int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value is >= 0 and <= 59)
        {
            return value;
        }

        throw new InvalidOperationException("Sync:schedule:minute must be between 0 and 59.");
    }

    private static SyncTransferMode GetTransferMode(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("Sync:transferMode");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return SyncTransferMode.AsyncExport;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "asyncexport" => SyncTransferMode.AsyncExport,
            "async-export" => SyncTransferMode.AsyncExport,
            "exportasync" => SyncTransferMode.AsyncExport,
            "export-async" => SyncTransferMode.AsyncExport,
            "pagedapi" => SyncTransferMode.PagedApi,
            "paged-api" => SyncTransferMode.PagedApi,
            "paged" => SyncTransferMode.PagedApi,
            _ => throw new InvalidOperationException(
                "Sync:transferMode must be one of: asyncExport, pagedApi.")
        };
    }

    private static DateTimeOffset GetStartFrom(IConfiguration configuration, DateTimeOffset now)
    {
        var configuredValue = configuration.GetValue<string>("Sync:startFrom");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(
                configuredValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var startFrom))
        {
            return startFrom;
        }

        throw new InvalidOperationException("Sync:startFrom must be a valid date/time value.");
    }

    private static int GetPositiveInt(IConfiguration configuration, string key, int defaultValue)
    {
        var configuredValue = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        if (int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            value > 0)
        {
            return value;
        }

        throw new InvalidOperationException($"{key} must be greater than 0.");
    }

    private static TimeSpan GetPositiveTimeSpan(IConfiguration configuration, string key, TimeSpan defaultValue)
    {
        var configuredValue = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        if (TimeSpan.TryParse(configuredValue, CultureInfo.InvariantCulture, out var value) &&
            value > TimeSpan.Zero)
        {
            return value;
        }

        throw new InvalidOperationException($"{key} must be greater than 0.");
    }

    private static bool GetBool(IConfiguration configuration, string key, bool defaultValue)
    {
        var configuredValue = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        if (bool.TryParse(configuredValue, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"{key} must be true or false.");
    }
}

public sealed record SyncScheduleOptions
{
    public static SyncScheduleOptions FixedDelay { get; } = new();

    public SyncScheduleMode Mode { get; init; } = SyncScheduleMode.FixedDelay;

    public TimeOnly Time { get; init; } = TimeOnly.MinValue;

    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Monday;

    public int DayOfMonth { get; init; } = 1;

    public int Minute { get; init; }
}

public enum SyncStartupMode
{
    Immediate,
    Disabled,
    HistoricalOnly
}

public enum SyncScheduleMode
{
    FixedDelay,
    Hourly,
    Daily,
    Weekly,
    Monthly
}
