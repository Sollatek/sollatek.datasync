#nullable enable

namespace Sollatek.DataSync.Notifications;

public sealed class NoopFailureNotificationSender : IFailureNotificationSender
{
    public static NoopFailureNotificationSender Instance { get; } = new();

    private NoopFailureNotificationSender()
    {
    }

    public Task SendAsync(
        FailureNotificationMessage message,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
