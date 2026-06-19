#nullable enable

using Sollatek.DataSync.Config;

namespace Sollatek.DataSync.Execution;

public static class RetryDelayPlanner
{
    public static DateTimeOffset GetNextRetryTime(
        DateTimeOffset now,
        int tryNumber,
        RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (tryNumber <= 0)
        {
            throw new InvalidOperationException("tryNumber must be greater than 0.");
        }

        var delay = options.DelayFunction switch
        {
            RetryDelayFunction.Fixed => options.Period,
            RetryDelayFunction.Linear => TimeSpan.FromTicks(options.Period.Ticks * tryNumber),
            _ => throw new InvalidOperationException(
                $"Unsupported retry delay function '{options.DelayFunction}'.")
        };

        return now.Add(delay);
    }
}
