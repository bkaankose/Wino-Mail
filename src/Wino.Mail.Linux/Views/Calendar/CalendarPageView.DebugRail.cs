#if DEBUG
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of the rail: <c>rail</c>, <c>rail &lt;position&gt;</c>, <c>rail +d|-d</c>, <c>rail settle</c>,
/// <c>animations on|off</c>, <c>pan units [velocity]</c>, <c>wheel notches [shift]</c>, <c>press strip x y</c>, <c>key Left|Right|Up|Down|Home|Escape</c>; <c>perf navigate &lt;count&gt; [-1]</c> (registered with the other perf verbs).
/// </summary>
internal sealed partial class CalendarPageView
{
    private void RegisterRailDebugVerbs(WinoCalendarSurface surface)
    {
        Bindings.Own(CalendarDebugActions.AddVerb("rail", args =>
        {
            if (args.Length > 0)
            {
                var arg = args[0];
                if (arg == "settle") surface.DebugSettleRail();
                else if ((arg.StartsWith('+') || arg.StartsWith('-')) && double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
                    surface.DebugSetRailPosition(surface.DebugRailPosition + delta);
                else if (double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var position)) surface.DebugSetRailPosition(position);
                else return "usage: rail [<position>|+d|-d|settle]";
            }
            return $"{surface.DescribeRail()} title='{_toolbar.RangeText}' vmRange={ViewModel.CurrentVisibleRange?.StartDate:yyyy-MM-dd}..{ViewModel.CurrentVisibleRange?.EndDate:yyyy-MM-dd}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("animations", args =>
        {
            var settings = Gtk.Settings.GetDefault();
            if (settings is null) return "no settings";
            if (args.Length > 0) settings.GtkEnableAnimations = args[0] == "on";
            return $"animations={settings.GtkEnableAnimations}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("pan", args =>
        {
            if (args.Length == 0 || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var units)) return "usage: pan <units> [velocity px/ms]";
            var velocity = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return surface.DebugPan(units, velocity);
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("wheel", args =>
        {
            if (args.Length == 0 || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var notches)) return "usage: wheel <notches> [shift]";
            return $"consumed={surface.DebugWheel(notches, args.Length > 1 && args[1] == "shift")}";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("press", args =>
        {
            if (args.Length < 3 || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return "usage: press <Header|AllDay|Timed|Month> <x> <y>";
            return surface.DebugPress(args[0], x, y);
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("key", args =>
        {
            if (args.Length == 0) return "usage: key Left|Right|Up|Down|Home|Escape";
            uint keyval = args[0] switch
            {
                "Left" => Gdk.Constants.KEY_Left,
                "Right" => Gdk.Constants.KEY_Right,
                "Up" => Gdk.Constants.KEY_Up,
                "Down" => Gdk.Constants.KEY_Down,
                "Home" => Gdk.Constants.KEY_Home,
                "Escape" => Gdk.Constants.KEY_Escape,
                _ => 0
            };
            return keyval == 0 ? "unknown key" : $"handled={surface.DebugKey(keyval, 0)}";
        }));
    }

    /// <summary>
    /// <c>perf navigate &lt;count&gt; [-1]</c>: glides <c>count</c> periods one after the other. Per step: glide ms (call to settle),
    /// settle to the ViewModel range, and to the relayout after the loaded window changed; for the run: frame intervals,
    /// managed bytes per frame and the tile counts.
    /// </summary>
    private async Task<string> PerfNavigateAsync(WinoCalendarSurface surface, string[] args)
    {
        var count = args.Length > 0 && int.TryParse(args[0], out var parsed) ? Math.Clamp(parsed, 1, 200) : 10;
        var direction = args.Length > 1 && args[1] == "-1" ? -1 : 1;
        var log = new CalendarFrameLog();
        surface.FrameLog = log;
        long allocated = 0, lastBytes = -1, frames = 0, quietAllocated = 0, quietFrames = 0, lastTick = 0;
        int slowAfterShift = 0, slowQuiet = 0;
        var shiftedLastFrame = false;
        var lastShifts = surface.DebugShiftCount;
        var running = true;
        surface.Root.AddTickCallback((_, clock) =>
        {
            if (!surface.DebugIsGliding)
            {
                lastBytes = -1;
                lastTick = 0;
                return running;
            }
            var now = clock.GetFrameTime();
            var shifted = surface.DebugShiftCount != lastShifts;
            if (lastTick > 0 && now - lastTick > 25_000)
            {
                if (shifted || shiftedLastFrame) slowAfterShift++;
                else slowQuiet++;
            }
            lastTick = now;
            shiftedLastFrame = shifted;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            if (lastBytes >= 0)
            {
                allocated += bytes - lastBytes;
                frames++;
                if (surface.DebugShiftCount == lastShifts)
                {
                    quietAllocated += bytes - lastBytes;
                    quietFrames++;
                }
            }
            lastShifts = surface.DebugShiftCount;
            lastBytes = GC.GetAllocatedBytesForCurrentThread();
            return running;
        });
        var shifts = surface.DebugShiftCount;
        surface.DebugResetShiftCost();
        var text = new StringBuilder();
        var glides = new List<double>();
        try
        {
            for (var step = 0; step < count; step++)
            {
                var settled = new TaskCompletionSource<long>();
                EventHandler<CalendarPeriodEventArgs> onSettled = (_, _) => settled.TrySetResult(Stopwatch.GetTimestamp());
                surface.PeriodSettled += onSettled;
                var rangeChanged = new TaskCompletionSource<long>();
                var windowChanged = new TaskCompletionSource<long>();
                var relaidAfterWindow = new TaskCompletionSource<long>();
                PropertyChangedEventHandler onChanged = (_, e) =>
                {
                    if (e.PropertyName == nameof(CalendarPageViewModel.CurrentVisibleRange)) rangeChanged.TrySetResult(Stopwatch.GetTimestamp());
                    else if (e.PropertyName == nameof(CalendarPageViewModel.LoadedDateWindow)) windowChanged.TrySetResult(Stopwatch.GetTimestamp());
                };
                EventHandler onRelaid = (_, _) =>
                {
                    if (windowChanged.Task.IsCompleted) relaidAfterWindow.TrySetResult(Stopwatch.GetTimestamp());
                };
                ViewModel.PropertyChanged += onChanged;
                surface.Relaid += onRelaid;
                log.Break();
                var begin = Stopwatch.GetTimestamp();
                try
                {
                    NavigatePeriods(direction);
                    var settledAt = await WithTimeout(settled.Task, 2000);
                    await CalendarPerfProbe.MainAsync();
                    var rangeAt = await WithTimeout(rangeChanged.Task, 3000);
                    await CalendarPerfProbe.MainAsync();
                    var windowAt = await WithTimeout(windowChanged.Task, 1000);
                    await CalendarPerfProbe.MainAsync();
                    var relaidAt = windowAt > 0 ? await WithTimeout(relaidAfterWindow.Task, 500) : 0;
                    var relaid = relaidAt > 0;
                    var glide = settledAt > 0 ? Stopwatch.GetElapsedTime(begin, settledAt).TotalMilliseconds : -1;
                    glides.Add(glide);
                    text.Append(CultureInfo.InvariantCulture, $"step {step}: glide={glide:0.0} settle->range={(settledAt > 0 && rangeAt > 0 ? Stopwatch.GetElapsedTime(settledAt, rangeAt).TotalMilliseconds : -1):0.0} ")
                        .Append(CultureInfo.InvariantCulture, $"settle->window={(windowAt > 0 ? Stopwatch.GetElapsedTime(settledAt, windowAt).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) : "unchanged")} settle->relaid={(relaid ? Stopwatch.GetElapsedTime(settledAt, relaidAt).TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) : "-")}").AppendLine();
                }
                finally
                {
                    surface.PeriodSettled -= onSettled;
                    surface.Relaid -= onRelaid;
                    ViewModel.PropertyChanged -= onChanged;
                }
            }
        }
        finally
        {
            running = false;
            surface.FrameLog = null;
        }
        var (framesLogged, p50, p95, max, over) = log.Summarize();
        glides.Sort();
        text.Insert(0, FormattableString.Invariant(
            $"steps={count} glideMedian={(glides.Count > 0 ? glides[glides.Count / 2] : 0):0.0} frames={framesLogged} p50={p50:0.0}ms p95={p95:0.0}ms max={max:0.0}ms over25={over} slowAfterShift={slowAfterShift} slowQuiet={slowQuiet} windowShifts={surface.DebugShiftCount - shifts} shiftCpuMs={surface.DebugShiftMillisecondsTotal / Math.Max(1, surface.DebugShiftCount - shifts):0.0}/{surface.DebugShiftMillisecondsMax:0.0} bytesPerFrame={(frames == 0 ? 0 : allocated / frames)} bytesPerFrameWithoutShift={(quietFrames == 0 ? 0 : quietAllocated / quietFrames)} active={surface.ActiveTileCount} spare={surface.SpareTileCount}\n"));
        return text.ToString();
    }

    private static async Task<long> WithTimeout(Task<long> task, uint milliseconds)
    {
        var timeout = CalendarPerfProbe.DelayAsync(milliseconds);
        return await Task.WhenAny(task, timeout) == task ? task.Result : 0;
    }
}
#endif
