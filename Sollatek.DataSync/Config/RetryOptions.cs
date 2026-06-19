#nullable enable

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Sollatek.DataSync.Execution;

namespace Sollatek.DataSync.Config;

public sealed record RetryOptions
{
    public int MaxTries { get; init; } = 3;

    public TimeSpan Period { get; init; } = TimeSpan.FromMinutes(5);

    public RetryDelayFunction DelayFunction { get; init; } = RetryDelayFunction.Linear;

    public static RetryOptions Default { get; } = new();

    public static RetryOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new RetryOptions
        {
            MaxTries = GetPositiveInt(configuration, "Retry:maxTries", Default.MaxTries),
            Period = GetPositiveTimeSpan(configuration, "Retry:period", Default.Period),
            DelayFunction = GetDelayFunction(configuration)
        };
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

    private static RetryDelayFunction GetDelayFunction(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("Retry:delayFunction");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return Default.DelayFunction;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "fixed" => RetryDelayFunction.Fixed,
            "linear" => RetryDelayFunction.Linear,
            _ => throw new InvalidOperationException("Retry:delayFunction must be one of: fixed, linear.")
        };
    }
}
