using System;
using System.Collections.Concurrent;

namespace Wino.Services;

public sealed class WinoTelemetryDeduplicator
{
    private readonly ConcurrentDictionary<string, DeduplicationState> _states = new(StringComparer.Ordinal);

    public bool ShouldSend(
        string? key,
        TimeSpan? window,
        DateTimeOffset now,
        out int suppressedRepeatCount)
    {
        suppressedRepeatCount = 0;

        if (string.IsNullOrWhiteSpace(key) || window is null || window <= TimeSpan.Zero)
        {
            return true;
        }

        while (true)
        {
            if (!_states.TryGetValue(key, out var existing))
            {
                if (_states.TryAdd(key, new DeduplicationState(now, 0)))
                {
                    return true;
                }

                continue;
            }

            if (now - existing.LastSentAt < window)
            {
                var suppressed = existing with { SuppressedRepeatCount = existing.SuppressedRepeatCount + 1 };
                if (_states.TryUpdate(key, suppressed, existing))
                {
                    return false;
                }

                continue;
            }

            var replacement = new DeduplicationState(now, 0);
            if (_states.TryUpdate(key, replacement, existing))
            {
                suppressedRepeatCount = existing.SuppressedRepeatCount;
                return true;
            }
        }
    }

    private sealed record DeduplicationState(DateTimeOffset LastSentAt, int SuppressedRepeatCount);
}
