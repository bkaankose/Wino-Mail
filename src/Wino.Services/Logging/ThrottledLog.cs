#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Services.Logging;

/// <summary>
/// Keeps repeated diagnostics from growing the log. The first occurrence of a key is written at once; later
/// occurrences inside the window are counted and surface as a <c>suppressed</c> count on the next line that is
/// written. Callers own the logger and the message; this class only answers "write it now?".
/// </summary>
public sealed class ThrottledLog
{
    private const int MaxTrackedKeys = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;

    public ThrottledLog(TimeSpan window, TimeProvider? timeProvider = null)
    {
        _window = window;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns true when the caller should write the line for <paramref name="key"/> now. <paramref name="suppressed"/>
    /// is how many occurrences were swallowed since the previous written line for the same key.
    /// </summary>
    public bool ShouldWrite(string key, out int suppressed)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (now - entry.LastWrittenUtc < _window)
                {
                    entry.Suppressed++;
                    suppressed = 0;
                    return false;
                }

                suppressed = entry.Suppressed;
                entry.Suppressed = 0;
                entry.LastWrittenUtc = now;
                return true;
            }

            if (_entries.Count >= MaxTrackedKeys)
                Prune(now);

            _entries[key] = new Entry { LastWrittenUtc = now };
            suppressed = 0;
            return true;
        }
    }

    /// <summary>Forgets a key so its next occurrence is written immediately (for example after the state it describes changed).</summary>
    public void Reset(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var stale = new List<string>();
        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastWrittenUtc >= _window)
                stale.Add(pair.Key);
        }

        foreach (var key in stale)
            _entries.Remove(key);

        // Still full: everything is recent. Drop the oldest half so the dictionary cannot grow without bound.
        if (_entries.Count >= MaxTrackedKeys)
        {
            var ordered = new List<KeyValuePair<string, Entry>>(_entries);
            ordered.Sort(static (a, b) => a.Value.LastWrittenUtc.CompareTo(b.Value.LastWrittenUtc));
            for (var i = 0; i < ordered.Count / 2; i++)
                _entries.Remove(ordered[i].Key);
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset LastWrittenUtc;
        public int Suppressed;
    }
}
