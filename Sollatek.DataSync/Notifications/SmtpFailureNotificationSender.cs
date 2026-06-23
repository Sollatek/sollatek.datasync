#nullable enable

using System.Net;
using System.Net.Mail;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Notifications;

public sealed class SmtpFailureNotificationSender : IFailureNotificationSender
{
    private readonly FailureEmailNotificationOptions _options;
    private readonly ILogger<SmtpFailureNotificationSender> _logger;

    public SmtpFailureNotificationSender(
        FailureEmailNotificationOptions options,
        ILogger<SmtpFailureNotificationSender> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SendAsync(
        FailureNotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var mailMessage = BuildMailMessage(message);
        using var smtpClient = new SmtpClient(_options.SmtpHost!, _options.SmtpPort)
        {
            EnableSsl = _options.EnableSsl
        };
        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            smtpClient.Credentials = new NetworkCredential(_options.Username, _options.Password ?? string.Empty);
        }

        await smtpClient.SendMailAsync(mailMessage, cancellationToken);
        _logger.LogInformation(
            "Sent sync failure notification email for run {RunId} to {RecipientCount} recipients.",
            message.RunId,
            _options.To.Count);
    }

    private MailMessage BuildMailMessage(FailureNotificationMessage message)
    {
        var mailMessage = new MailMessage
        {
            From = new MailAddress(_options.From!),
            Subject = BuildSubject(message),
            Body = BuildBody(message),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        foreach (var recipient in _options.To)
        {
            mailMessage.To.Add(recipient);
        }

        return mailMessage;
    }

    private string BuildSubject(FailureNotificationMessage message)
    {
        return $"{_options.SubjectPrefix} sync failed after {message.TryNumber}/{message.MaxTries} tries";
    }

    private static string BuildBody(FailureNotificationMessage message)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DataSync failed after exhausting the configured retry attempts.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Run ID: {message.RunId}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Entity: {message.EntityKey ?? "(none)"}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Try: {message.TryNumber}/{message.MaxTries}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Range end UTC: {message.RangeEndUtc:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Failed at UTC: {message.FailedAtUtc:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Machine: {Environment.MachineName}");
        builder.AppendLine();
        builder.AppendLine("Error:");
        builder.AppendLine(message.Error);
        return builder.ToString();
    }
}
