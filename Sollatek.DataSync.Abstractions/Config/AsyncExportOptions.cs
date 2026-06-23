#nullable enable

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record AsyncExportOptions
{
    private int _maxSubmissions = 60;
    private TimeSpan _submissionWindow = TimeSpan.FromHours(1);

    public string Format { get; init; } = "Parquet";

    public string StatePath { get; init; } = ".artifacts/async-exports";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public int MaxParallelRequests { get; init; } = 10;

    public int MaxSubmissions
    {
        get => _maxSubmissions;
        init => _maxSubmissions = value;
    }

    public TimeSpan SubmissionWindow
    {
        get => _submissionWindow;
        init => _submissionWindow = value;
    }

    public int MaxSubmissionsPerHour
    {
        get => _maxSubmissions;
        init
        {
            _maxSubmissions = value;
            _submissionWindow = TimeSpan.FromHours(1);
        }
    }

    public TimeSpan RateLimitRetryDelay { get; init; } = TimeSpan.FromHours(1);

    public static AsyncExportOptions FromConfiguration(
        IConfiguration configuration,
        string? fallbackFormat = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var statePath = configuration.GetValue<string>("AsyncExport:statePath");

        return new AsyncExportOptions
        {
            Format = GetExportFormat(configuration, fallbackFormat),
            StatePath = string.IsNullOrWhiteSpace(statePath) ? ".artifacts/async-exports" : statePath,
            PollInterval = GetPositiveTimeSpan(
                configuration,
                "AsyncExport:pollInterval",
                TimeSpan.FromMinutes(1)),
            MaxParallelRequests = GetPositiveInt(configuration, "AsyncExport:maxParallelRequests", 10),
            MaxSubmissions = GetMaxSubmissions(configuration),
            SubmissionWindow = GetPositiveTimeSpan(
                configuration,
                "AsyncExport:submissionWindow",
                TimeSpan.FromHours(1)),
            RateLimitRetryDelay = GetPositiveTimeSpan(
                configuration,
                "AsyncExport:rateLimitRetryDelay",
                TimeSpan.FromHours(1))
        };
    }

    private static string GetExportFormat(IConfiguration configuration, string? fallbackFormat)
    {
        const string key = "AsyncExport:format";
        var configuredValue = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return ExportFormatNames.NormalizePortalFormat(
                fallbackFormat,
                key,
                ExportFormatNames.Parquet);
        }

        return ExportFormatNames.NormalizePortalFormat(configuredValue, key, ExportFormatNames.Parquet);
    }

    private static int GetMaxSubmissions(IConfiguration configuration)
    {
        var configuredValue = configuration.GetValue<string>("AsyncExport:maxSubmissions");
        return string.IsNullOrWhiteSpace(configuredValue)
            ? GetPositiveInt(configuration, "AsyncExport:maxSubmissionsPerHour", 60)
            : ParsePositiveInt(configuredValue, "AsyncExport:maxSubmissions");
    }

    private static int GetPositiveInt(IConfiguration configuration, string key, int defaultValue)
    {
        var configuredValue = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        return ParsePositiveInt(configuredValue, key);
    }

    private static int ParsePositiveInt(string configuredValue, string key)
    {
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
}
