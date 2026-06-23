#nullable enable

namespace Sollatek.DataSync.Notifications;

public interface IFailureNotificationSender
{
    Task SendAsync(
        FailureNotificationMessage message,
        CancellationToken cancellationToken);
}
