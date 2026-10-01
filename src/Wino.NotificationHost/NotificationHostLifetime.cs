namespace Wino.NotificationHost;

internal static class NotificationHostLifetime
{
    public const int TimeoutExitCode = 2;

    public static Timer Start(TimeSpan maximumLifetime)
        // Exit directly: notification calls or shutdown logging may themselves be blocked.
        => new(static _ => Environment.Exit(TimeoutExitCode), null, maximumLifetime, Timeout.InfiniteTimeSpan);
}
