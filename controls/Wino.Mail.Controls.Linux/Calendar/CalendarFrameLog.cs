#if DEBUG
namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>A preallocated ring of frame times in microseconds; recording allocates nothing.</summary>
public sealed class CalendarFrameLog
{
    private readonly long[] _frames = new long[4096];
    private int _count;

    public void Reset() => _count = 0;

    /// <summary>Marks a pause (the frame clock stopped between two runs): the interval across it is not counted.</summary>
    public void Break() => Record(0);

    public void Record(long frameTimeMicroseconds)
    {
        _frames[_count % _frames.Length] = frameTimeMicroseconds;
        _count++;
    }

    /// <summary>Frame intervals in milliseconds; allocates, call after the run.</summary>
    public (int Frames, double P50, double P95, double Max, int Over25) Summarize()
    {
        var count = Math.Min(_count, _frames.Length);
        if (count < 2) return (count, 0, 0, 0, 0);
        var start = _count > _frames.Length ? _count % _frames.Length : 0;
        var list = new List<double>(count);
        for (var index = 1; index < count; index++)
        {
            var current = _frames[(start + index) % _frames.Length];
            var previous = _frames[(start + index - 1) % _frames.Length];
            if (current == 0 || previous == 0) continue;
            list.Add((current - previous) / 1000.0);
        }
        if (list.Count == 0) return (count, 0, 0, 0, 0);
        var intervals = list.ToArray();
        var sorted = (double[])intervals.Clone();
        Array.Sort(sorted);
        var over = 0;
        foreach (var interval in intervals) if (interval > 25) over++;
        return (count, sorted[sorted.Length / 2], sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)], sorted[^1], over);
    }
}
#endif
