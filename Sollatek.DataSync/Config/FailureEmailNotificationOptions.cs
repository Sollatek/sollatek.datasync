#nullable enable

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Sollatek.DataSync.Config;

public sealed record FailureEmailNotificationOptions
{
    public static FailureEmailNotificationOptions Default { get; } = new();

    public bool Enabled { get; init; }

    public string? SmtpHost { get; init; }

    public int SmtpPort { get; init; } = 587;

    public bool EnableSsl { get; init; } = true;

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? From { get; init; }

    public IReadOnlyList<string> To { get; init; } = [];

    public string SubjectPrefix { get; init; } = "[DataSync]";

    public static FailureEmailNotificationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Notifications:failureEmail");
        if (!section.Exists())
        {
            return Default;
        }

        var enabled = GetOptionalBoolean(section, "enabled") ?? HasAnyConfiguredValue(section);
        if (!enabled)
        {
            return Default;
        }

        var options = new FailureEmailNotificationOptions
        {
            Enabled = true,
            SmtpHost = GetTrimmedValue(section, "smtpHost"),
            SmtpPort = GetPositiveInt(section, "smtpPort", Default.SmtpPort),
            EnableSsl = GetOptionalBoolean(section, "enableSsl") ?? Default.EnableSsl,
            Username = GetTrimmedValue(section, "username"),
            Password = GetTrimmedValue(section, "password"),
            From = GetTrimmedValue(section, "from"),
            To = GetRecipients(section),
            SubjectPrefix = GetTrimmedValue(section, "subjectPrefix") ?? Default.SubjectPrefix
        };

        Validate(options);
        return options;
    }

    private static void Validate(FailureEmailNotificationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SmtpHost))
        {
            throw new InvalidOperationException("Notifications:failureEmail:smtpHost must be configured when failure email notifications are enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.From))
        {
            throw new InvalidOperationException("Notifications:failureEmail:from must be configured when failure email notifications are enabled.");
        }

        if (options.To.Count == 0)
        {
            throw new InvalidOperationException("Notifications:failureEmail:to must contain at least one recipient when failure email notifications are enabled.");
        }
    }

    private static bool HasAnyConfiguredValue(IConfigurationSection section)
    {
        return section.GetChildren().Any(child =>
            !string.Equals(child.Key, "enabled", StringComparison.OrdinalIgnoreCase) &&
            (!string.IsNullOrWhiteSpace(child.Value) || child.GetChildren().Any()));
    }

    private static string? GetTrimmedValue(
        IConfiguration configuration,
        string key)
    {
        var value = configuration.GetValue<string>(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int GetPositiveInt(
        IConfiguration configuration,
        string key,
        int defaultValue)
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

        throw new InvalidOperationException($"Notifications:failureEmail:{key} must be greater than 0.");
    }

    private static bool? GetOptionalBoolean(
        IConfiguration configuration,
        string key)
    {
        var configuredValue = configuration.GetValue<string>(key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return null;
        }

        if (bool.TryParse(configuredValue, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Notifications:failureEmail:{key} must be true or false.");
    }

    private static IReadOnlyList<string> GetRecipients(IConfigurationSection section)
    {
        var toSection = section.GetSection("to");
        var childRecipients = toSection
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        if (childRecipients.Length > 0)
        {
            return childRecipients;
        }

        var configuredValue = toSection.Value;
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return [];
        }

        return configuredValue
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
