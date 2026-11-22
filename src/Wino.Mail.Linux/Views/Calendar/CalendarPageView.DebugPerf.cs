#if DEBUG
using System.Diagnostics;
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs: <c>perf type T</c>, <c>perf next</c>, <c>perf load</c>, <c>perf scroll</c>,
/// <c>hour-height n</c>, <c>working-hours on|off</c>, <c>overflow</c>, <c>overflow-close</c>.
/// </summary>
internal sealed partial class CalendarPageView
{
    private void RegisterPerfDebugVerbs(WinoCalendarSurface surface)
    {
        Bindings.Own(CalendarDebugActions.AddVerb("perf", args => args.Length == 0 ? Task.FromResult("usage: perf type <Type>|next|load|scroll|navigate <count> [-1]") : args[0] switch
        {
            "type" when args.Length > 1 && Enum.TryParse<CalendarDisplayType>(args[1], true, out var type) => Task.FromResult(TimedType(surface, type)),
            "next" => Task.FromResult(TimedNext(surface)),
            "load" => PerfLoadAsync(surface),
            "scroll" => CalendarPerfProbe.ScrollAsync(surface),
            "navigate" => PerfNavigateAsync(surface, args[1..]),
            _ => Task.FromResult("unknown perf verb")
        }));
        EventHandler relaid = (_, _) => OnTimingRelaid(surface);
        surface.Relaid += relaid;
        Bindings.Own(new Wino.Presentation.Linux.ActionDisposable(() => surface.Relaid -= relaid));
        Bindings.Own(CalendarDebugActions.AddVerb("hour-height", args =>
        {
            if (args.Length == 0 || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return "usage: hour-height n";
            _shell.PreferencesService.HourHeight = height;
            return "ok";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("working-hours", args =>
        {
            _shell.PreferencesService.IsWorkingHoursEnabled = args.Length > 0 && args[0] == "on";
            return "ok";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("overflow", _ => $"open={surface.IsOverflowOpen} tiles={surface.OverflowTileCount}"));
        Bindings.Own(CalendarDebugActions.AddVerb("overflow-close", _ =>
        {
            surface.DebugCloseOverflow();
            return "closed";
        }));
    }

    // Static: a display type change navigates to a new calendar page, which reports the timing the old one started.
    private static long _timingStart;
    private static string? _timingLabel;
    private static CalendarDisplayType? _timingType;
    private static DateOnly? _timingStartDate;
    private static bool _timingPainted;

    /// <summary>
    /// Log-based timing (the verbs return at once): <c>Calendar timing: &lt;label&gt; shown &lt;ms&gt;</c> at the first painted frame after the
    /// surface laid out the requested period, then <c>loaded &lt;ms&gt;</c> at the frame after the next relayout (its items).
    /// </summary>
    private void StartTiming(WinoCalendarSurface surface, string label, CalendarDisplayType? type, DateOnly? startDate)
    {
        _timingStart = Stopwatch.GetTimestamp();
        _timingLabel = label;
        _timingType = type;
        _timingStartDate = startDate;
        _timingPainted = false;
    }

    private void OnTimingRelaid(WinoCalendarSurface surface)
    {
        if (_timingLabel is not { } label) return;
        if (_timingType is { } type && surface.VisibleRange?.DisplayType != type) return;
        if (_timingStartDate is { } start && ViewModel.CurrentVisibleRange?.StartDate == start) return;
        var phase = _timingPainted ? "loaded" : "shown";
        var begin = _timingStart;
        if (_timingPainted) _timingLabel = null;
        _timingPainted = true;
        // Relaid runs inside the surface's own tick callback; a tick callback added there does not run, so it is added a turn later.
        Wino.Presentation.Linux.MainLoop.Post(() => surface.Root.AddTickCallback((_, _) =>
        {
            Serilog.Log.Information("Calendar timing: {Label} {Phase} {Ms:0.0} ms tiles={Tiles}", label, phase, Stopwatch.GetElapsedTime(begin).TotalMilliseconds, surface.ActiveTileCount);
            return false;
        }));
    }

    private string TimedType(WinoCalendarSurface surface, CalendarDisplayType type)
    {
        StartTiming(surface, "type " + type, type, null);
        ViewModel.StatePersistanceService.CalendarDisplayType = type;
        return "started";
    }

    private string TimedNext(WinoCalendarSurface surface)
    {
        _timingLabel = null;
        var begin = Stopwatch.GetTimestamp();
        NavigatePeriods(1);
        var jump = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        surface.Root.AddTickCallback((_, _) =>
        {
            Serilog.Log.Information("Calendar timing: next shown {Ms:0.0} ms jump={Jump:0.0} tiles={Tiles}", Stopwatch.GetElapsedTime(begin).TotalMilliseconds, jump, surface.ActiveTileCount);
            return false;
        });
        return "started";
    }

    private async Task<string> PerfLoadAsync(WinoCalendarSurface surface)
    {
        var clock = Stopwatch.StartNew();
        var relaid = CalendarPerfProbe.NextRelaidAsync(surface, () => true, 3000);
        await ViewModel.ReloadCurrentVisibleRangeAsync();
        await CalendarPerfProbe.MainAsync();
        var load = clock.Elapsed.TotalMilliseconds;
        var ok = await relaid;
        await CalendarPerfProbe.NextFrameAsync(surface);
        return FormattableString.Invariant($"load={load:0.0}ms relaid={(ok ? clock.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) : "timeout")}ms items={ViewModel.CalendarItems.Count} tiles={surface.ActiveTileCount}");
    }
}
#endif
