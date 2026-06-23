#nullable enable

namespace Sollatek.DataSync.Notifications;

public sealed record FailureNotificationMessage(
    string RunId,
    string? EntityKey,
    string Error,
    int TryNumber,
    int MaxTries,
    DateTimeOffset RangeEndUtc,
    DateTimeOffset FailedAtUtc);
