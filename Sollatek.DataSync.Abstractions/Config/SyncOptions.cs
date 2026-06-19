#nullable enable

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record SyncOptions
{
    public required DateTimeOffset StartFrom { get; init; }

    public bool RunOnStartup { get; init; } = true;

    public bool StopWhenFinished { get; init; }

    public TimeSpan RunInterval { get; init; } = TimeSpan.FromHours(6);

    public int MaxPageSize { get; init; } = 500;

    public static SyncOptions FromConfiguration(
        IConfiguration configuration,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var currentTime = now ?? DateTimeOffset.UtcNow;

        return new SyncOptions
        {
            StartFrom = GetStartFrom(configuration, currentTime),
            RunOnStartup = GetBool(configuration, "Sync:runOnStartup", defaultValue: true),
            StopWhenFinished = GetBool(configuration, "Sync:stopWhenFinished", defaultValue: false),
            RunInterval = GetPositiveTimeSpan(configuration, "Sync:runInterval", TimeSpan.FromHours(6)),
            MaxPageSize = GetPositiveInt(configuration, "Sync:maxPageSize", 500)
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
