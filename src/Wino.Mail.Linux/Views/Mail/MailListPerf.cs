using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// Timing stamps of the mail list for the Debug performance actions. Every recording method is compiled out of a
/// Release build, so no call and no state remains there. Main thread only.
/// </summary>
internal static class MailListPerf
{
    private const int RingSize = 64;

    private static readonly (MailListApplyKind Kind, int Rows, double Milliseconds)[] _applies = new (MailListApplyKind, int, double)[RingSize];
    private static int _applyCount;

    /// <summary>Stopwatch timestamp set by a Debug action right before it asks for a folder.</summary>
    public static long SwitchRequested;

    public static int ViewGeneration { get; private set; }

    public static long ViewCreatedAt { get; private set; }

    public static long FirstRowsAt { get; private set; }

    public static int FirstRowCount { get; private set; }

    public static long FirstFrameAt { get; private set; }

    public static long InitializationDoneAt { get; private set; }

    public static long LastSelectionTicks { get; private set; }

    public static int LastSelectionItems { get; private set; }

    [Conditional("DEBUG")]
    public static void ViewCreated(MailListView view)
    {
        ViewGeneration++;
        ViewCreatedAt = Stopwatch.GetTimestamp();
        FirstRowsAt = FirstFrameAt = InitializationDoneAt = 0;
        FirstRowCount = 0;
    }

    /// <summary>The first apply that left rows; arms a tick callback for the end of the first frame that shows them.</summary>
    [Conditional("DEBUG")]
    public static void FirstRows(MailListView view, int rows)
    {
        if (FirstRowsAt != 0) return;
        FirstRowsAt = Stopwatch.GetTimestamp();
        FirstRowCount = rows;
        var generation = ViewGeneration;
        var ticks = 0;
        // A tick runs before its frame is laid out and painted: the second tick comes after the frame that drew the rows.
        view.List.AddTickCallback((_, _) =>
        {
            if (++ticks < 2) return generation == ViewGeneration;
            if (generation == ViewGeneration && FirstFrameAt == 0) FirstFrameAt = Stopwatch.GetTimestamp();
            return false;
        });
    }

    [Conditional("DEBUG")]
    public static void InitializationDone(MailListView view) => InitializationDoneAt = Stopwatch.GetTimestamp();

    [Conditional("DEBUG")]
    public static void Applied(MailListApplyKind kind, int rows, long startedTimestamp)
    {
        _applies[_applyCount % RingSize] = (kind, rows, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
        _applyCount++;
    }

    [Conditional("DEBUG")]
    public static void SelectionPublished(int items, long startedTimestamp)
    {
        LastSelectionItems = items;
        LastSelectionTicks = Stopwatch.GetTimestamp() - startedTimestamp;
    }

    /// <summary>The last applies as <c>kind rows ms</c>, then count, mean and max per kind.</summary>
    public static string Describe()
    {
        var text = new StringBuilder();
        var count = Math.Min(_applyCount, RingSize);
        var stats = new Dictionary<MailListApplyKind, (int Count, double Sum, double Max)>();
        for (var index = _applyCount - count; index < _applyCount; index++)
        {
            var (kind, rows, milliseconds) = _applies[index % RingSize];
            text.Append(CultureInfo.InvariantCulture, $"{kind} {rows} {milliseconds:F2}\n");
            var current = stats.GetValueOrDefault(kind);
            stats[kind] = (current.Count + 1, current.Sum + milliseconds, Math.Max(current.Max, milliseconds));
        }
        text.Append(CultureInfo.InvariantCulture, $"total {_applyCount}\n");
        foreach (var (kind, (n, sum, max)) in stats)
            text.Append(CultureInfo.InvariantCulture, $"kind={kind} count={n} mean_ms={sum / n:F2} max_ms={max:F2}\n");
        return text.ToString();
    }
}
