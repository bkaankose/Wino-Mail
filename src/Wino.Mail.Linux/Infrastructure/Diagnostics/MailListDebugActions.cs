#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug measurements of the mail list (<see cref="MailListView.DebugCurrent"/>); every report is written atomically.
/// <c>debug-perf-folder-switch</c> <c>as</c> <c>[report, folderA, folderB, count?]</c>: alternates the two sidebar folders
/// and times request to view, first rows, first frame and initialization. <c>debug-perf-scroll</c> <c>as</c>
/// <c>[report, seconds?, pixelsPerFrame?]</c>: scrolls from the top on the frame clock and reports frame times, loads,
/// cells and memory. <c>debug-perf-apply</c> <c>s</c>: the last applies. <c>debug-maillist-state</c> <c>s</c>: rows,
/// sections, sticky header, selection, cells and the store-order check. <c>debug-maillist-scroll</c> <c>as</c>
/// <c>[fraction or row key]</c>.
/// </summary>
internal static class MailListDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-perf-folder-switch", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out var _);
            if (arguments.Length < 3) throw new ArgumentException("Expected [report, folderA, folderB, count?].");
            var count = arguments.Length > 3 ? int.Parse(arguments[3], CultureInfo.InvariantCulture) : 6;
            _ = FolderSwitchAsync(services, arguments[0], arguments[1], arguments[2], count);
        });
        Add(application, "debug-perf-scroll", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out var _);
            if (arguments.Length < 1) throw new ArgumentException("Expected [report, seconds?, pixelsPerFrame?].");
            var seconds = arguments.Length > 1 ? double.Parse(arguments[1], CultureInfo.InvariantCulture) : 10;
            var step = arguments.Length > 2 ? double.Parse(arguments[2], CultureInfo.InvariantCulture) : 60;
            Scroll(Current(), arguments[0], seconds, step);
        });
        Add(application, "debug-perf-apply", "s", parameter => DebugActions.WriteText(parameter.GetString(out var _), MailListPerf.Describe()));
        Add(application, "debug-maillist-state", "s", parameter => DebugActions.WriteText(parameter.GetString(out var _), Current().DebugState()));
        Add(application, "debug-maillist-scroll", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out var _);
            if (arguments.Length < 1) throw new ArgumentException("Expected [fraction or key].");
            Current().DebugScrollTo(arguments[0]);
            Serilog.Log.Information("Debug action: mail list scrolled to {Target}", arguments[0]);
        });
    }

    private static MailListView Current() => MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");

    private static void Add(Adw.Application application, string name, string type, Action<GLib.Variant> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New(type));
        action.OnActivate += (_, args) =>
        {
            try
            {
                if (args.Parameter is not { } parameter) throw new ArgumentException("A parameter is required.");
                run(parameter);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }

    private static async Task FolderSwitchAsync(Func<IServiceProvider?> services, string report, string folderA, string folderB, int count)
    {
        try
        {
            var shell = services()?.GetService<GtkNavigationService>()?.Shell ?? throw new InvalidOperationException("The shell is not shown.");
            var sidebar = shell.Sidebar;
            var text = new StringBuilder();
            var firstRows = new List<double>();
            var firstFrames = new List<double>();
            var startWithB = string.Equals(MailListView.DebugCurrent?.DebugFolderName, folderA, StringComparison.OrdinalIgnoreCase);
            for (var index = 0; index < count; index++)
            {
                var folder = (index % 2 == 0) == startWithB ? folderB : folderA;
                var item = SidebarDebugActions.Resolve(sidebar, folder);
                var generation = MailListPerf.ViewGeneration;
                var requested = Stopwatch.GetTimestamp();
                MailListPerf.SwitchRequested = requested;
                var invoke = sidebar.InvokeAsync(item);
                var waited = Stopwatch.StartNew();
                while (waited.ElapsedMilliseconds < 15_000)
                {
                    await Task.Delay(5);
                    if (MailListPerf.ViewGeneration == generation) continue;
                    if (MailListPerf.InitializationDoneAt == 0) continue;
                    if (MailListPerf.FirstFrameAt != 0 || MailListView.DebugCurrent is { RowCount: 0 }) break;
                }
                await invoke;
                double Since(long stamp) => stamp == 0 ? -1 : Stopwatch.GetElapsedTime(requested, stamp).TotalMilliseconds;
                var rowsMs = Since(MailListPerf.FirstRowsAt);
                var frameMs = Since(MailListPerf.FirstFrameAt);
                if (rowsMs >= 0) firstRows.Add(rowsMs);
                if (frameMs >= 0) firstFrames.Add(frameMs);
                text.Append(CultureInfo.InvariantCulture,
                    $"switch folder={folder} rows={MailListPerf.FirstRowCount} view_ms={Since(MailListPerf.ViewCreatedAt):F1} first_rows_ms={rowsMs:F1} first_frame_ms={frameMs:F1} init_ms={Since(MailListPerf.InitializationDoneAt):F1}\n");
                await Task.Delay(400);
            }
            text.Append(CultureInfo.InvariantCulture, $"median first_rows_ms={Median(firstRows):F1} first_frame_ms={Median(firstFrames):F1}\n");
            DebugActions.WriteText(report, text.ToString());
            Serilog.Log.Information("Debug action: folder switch timing written to {Report}", report);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-perf-folder-switch failed."); }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return -1;
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    private static void Scroll(MailListView view, string report, double seconds, double step)
    {
        var adjustment = view.Scroller.GetVadjustment();
        adjustment.SetValue(adjustment.GetLower());
        var frames = new List<double>();
        var rowsStart = view.RowCount;
        var loads = 0;
        var wasLoading = view.DebugIsLoadingMore;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var managedBefore = GC.GetTotalMemory(false);
        long lastFrame = 0;
        var started = Stopwatch.GetTimestamp();
        var lastTick = Stopwatch.GetTimestamp();
        var finished = false;
        var stalls = 0;

        void Finish(string reason)
        {
            if (finished) return;
            finished = true;
            frames.Sort();
            double Percentile(double p) => frames.Count == 0 ? 0 : frames[Math.Min(frames.Count - 1, (int)Math.Ceiling(p * frames.Count) - 1)];
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"reason={reason}\nframes={frames.Count}\nmean_ms={(frames.Count == 0 ? 0 : frames.Average()):F2}\n");
            text.Append(CultureInfo.InvariantCulture, $"p50_ms={Percentile(0.5):F2}\np95_ms={Percentile(0.95):F2}\nmax_ms={(frames.Count == 0 ? 0 : frames[^1]):F2}\n");
            text.Append(CultureInfo.InvariantCulture, $"over_33ms={frames.Count(static f => f > 33.4)}\nrows_start={rowsStart}\nrows_end={view.RowCount}\nload_more_calls={loads}\n");
            text.Append(CultureInfo.InvariantCulture, $"cells={view.Cells.CellCount}\nbound={view.Cells.BoundCount}\n");
            text.Append(CultureInfo.InvariantCulture, $"gc0={GC.CollectionCount(0) - gc0}\ngc1={GC.CollectionCount(1) - gc1}\ngc2={GC.CollectionCount(2) - gc2}\n");
            text.Append(CultureInfo.InvariantCulture, $"managed_mb_before={managedBefore / 1048576.0:F1}\nmanaged_mb_after={GC.GetTotalMemory(false) / 1048576.0:F1}\nrss_mb={Environment.WorkingSet / 1048576.0:F1}\n");
            text.Append(CultureInfo.InvariantCulture, $"stall_frames={stalls}\nscroll={adjustment.GetValue():F0}/{adjustment.GetUpper():F0}\n");
            DebugActions.WriteText(report, text.ToString());
            Serilog.Log.Information("Debug action: scroll timing written to {Report}", report);
        }

        view.List.AddTickCallback((_, clock) =>
        {
            if (finished) return false;
            lastTick = Stopwatch.GetTimestamp();
            var frameTime = clock.GetFrameTime();
            if (lastFrame != 0) frames.Add((frameTime - lastFrame) / 1000.0);
            lastFrame = frameTime;
            var loading = view.DebugIsLoadingMore;
            if (loading && !wasLoading) loads++;
            wasLoading = loading;
            var before = adjustment.GetValue();
            var end = adjustment.GetUpper() - adjustment.GetPageSize();
            adjustment.SetValue(Math.Min(end, before + step));
            if (before >= end - 0.5)
            {
                stalls++;
                if (!loading && !view.DebugCanLoadMore && stalls > 30)
                {
                    Finish("end");
                    return false;
                }
            }
            else stalls = 0;
            if (Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds)
            {
                Finish("time");
                return false;
            }
            return true;
        });

        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 500, () =>
        {
            if (finished) return false;
            if (Stopwatch.GetElapsedTime(lastTick).TotalSeconds < 3) return true;
            Finish("no frame for 3 s (screen blank or window hidden)");
            return false;
        });
    }
}
#endif
