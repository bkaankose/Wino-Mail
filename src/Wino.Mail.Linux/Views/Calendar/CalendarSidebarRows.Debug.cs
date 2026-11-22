#if DEBUG
using System.Globalization;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>Debug verbs of the mini calendar: <c>mini</c>, <c>mini-click yyyy-MM-dd</c>, <c>mini-popover</c>.</summary>
internal sealed partial class CalendarDatePickerRow
{
    private readonly List<IDisposable> _debugVerbs = [];
    private WinoDatePickerPopover? _debugPopover;

    partial void RegisterDebugVerbs()
    {
        _debugVerbs.Add(CalendarDebugActions.AddVerb("mini", _ => Describe()));
        _debugVerbs.Add(CalendarDebugActions.AddVerb("mini-click", args =>
        {
            if (args.Length == 0 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return "usage: mini-click yyyy-MM-dd";
            _calendar.DisplayMonth = date;
            _calendar.FocusDate(date);
            if (_calendar.DayButton(date) is not { } button) return "no button for " + args[0];
            button.Activate();
            return "clicked " + args[0] + "; range " + _client.CurrentVisibleRange?.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." + _client.CurrentVisibleRange?.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }));
        _debugVerbs.Add(CalendarDebugActions.AddVerb("mini-popover", args =>
        {
            if (args.Length > 0 && args[0] == "close")
            {
                _debugPopover?.Dispose();
                _debugPopover = null;
                return "closed";
            }
            if (args.Length > 0 && args[0] == "pick" && args.Length > 1 && _debugPopover is { } open
                && DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked))
            {
                open.Calendar.DayButton(picked)?.Activate();
                return "picked " + args[1] + " visible=" + open.Popover.GetVisible();
            }
            _debugPopover?.Dispose();
            var anchor = (Gtk.Widget?)_calendar.ToggleButton ?? _calendar.Root;
            var popover = new WinoDatePickerPopover(anchor) { FirstDayOfWeek = _firstDayOfWeek(), SelectedDate = DateOnly.FromDateTime(DateTime.Today).AddDays(3) };
            popover.DateSelected += (_, date) => Serilog.Log.Information("Debug action: mini popover DateSelected {Date:yyyy-MM-dd}", date);
            _debugPopover = popover;
            popover.Popup();
            return "open selected=" + popover.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }));
    }

    partial void DisposeDebugVerbs()
    {
        foreach (var verb in _debugVerbs) verb.Dispose();
        _debugVerbs.Clear();
        _debugPopover?.Dispose();
        _debugPopover = null;
    }

    private string Describe()
    {
        var selected = _calendar.SelectedDates.Order().ToList();
        var band = selected.Count == 0 ? "-" : $"{selected[0]:yyyy-MM-dd}..{selected[^1]:yyyy-MM-dd} ({selected.Count})";
        return string.Create(CultureInfo.InvariantCulture,
            $"month={_calendar.DisplayMonth:yyyy-MM} selected={band} focused={_calendar.FocusedDate:yyyy-MM-dd} collapsed={_calendar.Collapsed} first={_calendar.FirstDayOfWeek} height={_calendar.Root.GetHeight()} width={_calendar.Root.GetWidth()}");
    }
}
#endif
