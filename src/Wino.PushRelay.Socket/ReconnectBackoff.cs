#nullable enable
using System;

namespace Wino.PushRelay.Socket;

/// <summary>The desktop relay retry schedule: a few quick attempts, then doubling to a ceiling, always with ±50 % jitter.</summary>
public static class ReconnectBackoff
{
    public static TimeSpan Delay(TimeSpan[]? quickDelays, TimeSpan ceiling, int failedAttempts)
    {
        var quick = quickDelays is { Length: > 0 } configured ? configured : [TimeSpan.FromSeconds(5)];
        if (failedAttempts < quick.Length)
            return quick[failedAttempts];

        var doublings = Math.Min(failedAttempts - quick.Length + 1, 16);
        var delay = TimeSpan.FromTicks(Math.Min(ceiling.Ticks, quick[^1].Ticks << doublings));
        return delay < quick[^1] ? quick[^1] : delay;
    }

    public static TimeSpan Jitter(TimeSpan delay)
        => TimeSpan.FromMilliseconds(Math.Max(100, delay.TotalMilliseconds * (0.5 + Random.Shared.NextDouble())));
}
