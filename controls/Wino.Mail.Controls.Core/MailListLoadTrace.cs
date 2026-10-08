using System.Diagnostics;
using System.Text;

namespace Wino.Mail.Controls.Core;

/// <summary>
/// Stages of a single mail list load, in the order they are expected to occur.
/// </summary>
public enum MailListLoadStage
{
    /// <summary>A folder, filter, sorting or search change asked for a new page.</summary>
    Requested,

    /// <summary>Folder pivots were resolved and the load context is final.</summary>
    PivotsResolved,

    /// <summary>The database page query was handed to the mail service.</summary>
    QueryStarted,

    /// <summary>The mail service returned hydrated mail copies.</summary>
    QueryCompleted,

    /// <summary>View models for the page were constructed.</summary>
    ViewModelsPrepared,

    /// <summary>The store was asked to publish the page to the UI thread.</summary>
    StorePublishStarted,

    /// <summary>The store applied the page on the UI thread.</summary>
    StoreApplied,

    /// <summary>The projection began turning stored items into rows and groups.</summary>
    ProjectionRebuildStarted,

    /// <summary>The projection finished publishing rows and groups.</summary>
    ProjectionRebuildCompleted,

    /// <summary>The list realized its first container for the new page.</summary>
    FirstContainerRealized,

    /// <summary>The first composition frame after the new page was published.</summary>
    FirstFrameRendered,
}

/// <summary>
/// Collects wall-clock marks for one mail list load so the cost can be attributed to the
/// database, view-model construction, collection propagation or template realization.
/// Deliberately free of any logging dependency: the owner reads <see cref="Marks"/> and
/// emits whatever its own logger wants.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is ambient on purpose. Marks originate in three assemblies that do
/// not otherwise reference each other, and a load is strictly serialized by generation, so
/// threading a trace object through every call site would cost far more than it explains.
/// Every mark is first-write-wins and a no-op when no trace is active.
/// </remarks>
public sealed class MailListLoadTrace
{
    private static readonly int StageCount = Enum.GetValues<MailListLoadStage>().Length;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly double[] _elapsedMilliseconds;
    private readonly bool[] _marked;
    private int _reported;

    public MailListLoadTrace(long generation)
    {
        Generation = generation;
        _elapsedMilliseconds = new double[StageCount];
        _marked = new bool[StageCount];
        Mark(MailListLoadStage.Requested);
    }

    /// <summary>
    /// The trace for the load currently in flight, or null when nothing is being traced.
    /// </summary>
    public static MailListLoadTrace? Current { get; private set; }

    /// <summary>
    /// Raised once the last stage of a load is marked. The owner reports from here because
    /// the final frame renders after the load's own async flow has already returned.
    /// </summary>
    public event Action<MailListLoadTrace>? Completed;

    /// <summary>Load generation this trace belongs to.</summary>
    public long Generation { get; }

    /// <summary>Number of mails handed to the store for this load.</summary>
    public int ItemCount { get; set; }

    /// <summary>Number of projected rows produced for this load.</summary>
    public int RowCount { get; set; }

    /// <summary>Starts tracing a new load and makes it the ambient trace.</summary>
    public static MailListLoadTrace Begin(long generation)
    {
        var trace = new MailListLoadTrace(generation);
        Current = trace;
        return trace;
    }

    /// <summary>Clears the ambient trace when it is still the given one.</summary>
    public static void End(MailListLoadTrace? trace)
    {
        if (ReferenceEquals(Current, trace))
        {
            Current = null;
        }
    }

    /// <summary>Marks a stage on the ambient trace, if any.</summary>
    public static void MarkCurrent(MailListLoadStage stage) => Current?.Mark(stage);

    /// <summary>Records the first occurrence of a stage. Later marks are ignored.</summary>
    public void Mark(MailListLoadStage stage)
    {
        var index = (int)stage;
        if (index < 0 || index >= StageCount || _marked[index])
        {
            return;
        }

        _elapsedMilliseconds[index] = _stopwatch.Elapsed.TotalMilliseconds;
        _marked[index] = true;

        if (stage == MailListLoadStage.FirstFrameRendered)
        {
            Completed?.Invoke(this);
        }
    }

    /// <summary>
    /// Claims the right to report this trace. Returns true exactly once, so a trace reported
    /// from its completion callback is never reported again by a fallback path.
    /// </summary>
    public bool TryBeginReport() => Interlocked.Exchange(ref _reported, 1) == 0;

    /// <summary>Milliseconds from the request to the given stage, or null when unmarked.</summary>
    public double? GetElapsed(MailListLoadStage stage)
    {
        var index = (int)stage;
        return index >= 0 && index < StageCount && _marked[index]
            ? _elapsedMilliseconds[index]
            : null;
    }

    /// <summary>
    /// Every marked stage with its elapsed milliseconds, ordered by when it actually happened.
    /// Stages do not always occur in declaration order: the projection rebuilds synchronously
    /// inside the store's dispatched mutation, so it completes before the store reports applied.
    /// </summary>
    public IReadOnlyList<(MailListLoadStage Stage, double ElapsedMilliseconds)> Marks
    {
        get
        {
            var marks = new List<(MailListLoadStage Stage, double ElapsedMilliseconds)>(StageCount);
            for (var index = 0; index < StageCount; index++)
            {
                if (_marked[index])
                {
                    marks.Add(((MailListLoadStage)index, _elapsedMilliseconds[index]));
                }
            }

            marks.Sort(static (left, right) =>
                left.ElapsedMilliseconds.CompareTo(right.ElapsedMilliseconds));

            return marks;
        }
    }

    /// <summary>
    /// Per-stage deltas rendered as "Stage +1.2ms", suitable for a single structured log line.
    /// </summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        var previous = 0d;

        foreach (var (stage, elapsed) in Marks)
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(stage).Append(" +").Append((elapsed - previous).ToString("F1")).Append("ms");
            previous = elapsed;
        }

        return builder.Length == 0 ? "no stages recorded" : builder.ToString();
    }
}
