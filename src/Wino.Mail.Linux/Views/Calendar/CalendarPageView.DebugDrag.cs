#if DEBUG
using System.Globalization;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of drag to move and resize (macOS DebugDrag): <c>drag n minutes [days] [commit]</c>, <c>resize n minutes [commit]</c>,
/// <c>drag-commit</c>, <c>drag-cancel</c>, <c>drag-state</c>. They run the surface's begin, move and end steps a pointer drag runs.
/// </summary>
internal sealed partial class CalendarPageView
{
    private void RegisterDragDebugVerbs(WinoCalendarSurface surface)
    {
        Bindings.Own(CalendarDebugActions.AddVerb("drag", args => WithItem(args, item =>
        {
            var minutes = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
            var days = args.Length > 2 && int.TryParse(args[2], out var d) ? d : 0;
            return Drag(surface, item, days, minutes, resize: false, commit: args.Contains("commit"));
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("resize", args => WithItem(args, item =>
        {
            var minutes = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
            return Drag(surface, item, 0, minutes, resize: true, commit: args.Contains("commit"));
        })));
        Bindings.Own(CalendarDebugActions.AddVerb("drag-commit", _ =>
        {
            var state = surface.DescribeDrag();
            surface.FinishPreviewDrag(commit: true);
            return state + " -> committed";
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("drag-cancel", _ =>
        {
            surface.FinishPreviewDrag(commit: false);
            return surface.DescribeDrag();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("drag-state", _ => surface.DescribeDrag()));
    }

    private static string Drag(WinoCalendarSurface surface, Wino.Mail.ViewModels.Calendar.Data.CalendarItemViewModel item, int days, double minutes, bool resize, bool commit)
    {
        var before = $"'{item.Title}' {item.StartDate:yyyy-MM-dd HH:mm}..{item.EndDate:yyyy-MM-dd HH:mm}";
        if (!surface.PreviewDrag(item, days, minutes, resize)) return $"no tile for {before}";
        if (!surface.IsDraggingItem) return $"refused {before} ({CalendarTileMapper.DragRefusal(item)})";
        var target = surface.CurrentDragTarget is { } t ? $"{t.Start:yyyy-MM-dd HH:mm}..{t.End:yyyy-MM-dd HH:mm}" : "-";
        if (commit) surface.FinishPreviewDrag(commit: true);
        return $"{before} -> {target} ({(commit ? "committed" : "preview")})";
    }
}
#endif
