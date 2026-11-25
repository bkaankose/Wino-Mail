#if DEBUG
using System.Globalization;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure.Diagnostics;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Debug verbs of the quick event: <c>quick yyyy-MM-dd HH:mm HH:mm|allday</c> (the slot path), <c>quick-set name=t|location=t|allday=on|off</c>,
/// <c>quick-save</c>, <c>quick-more</c>, <c>quick-close</c>, <c>quick-state [png]</c>, <c>quick-readonly on|off</c>, <c>selection</c>,
/// <c>select-range day HH:mm day HH:mm [hold]</c> (days are indexes of the visible period; drives the press router), <c>select-cancel</c>.
/// </summary>
internal sealed partial class CalendarPageView
{
    private void RegisterQuickEventDebugVerbs(WinoCalendarSurface surface)
    {
        Bindings.Own(CalendarDebugActions.AddVerb("quick", args =>
        {
            if (args.Length < 2 || !DateTime.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return "usage: quick yyyy-MM-dd HH:mm HH:mm|allday";
            if (args[1] == "allday") return surface.DebugRaiseSlot(date, date.AddDays(1), allDay: true) + "\n" + QuickState();
            if (args.Length < 3 || !TimeSpan.TryParse(args[1], CultureInfo.InvariantCulture, out var from) || !TimeSpan.TryParse(args[2], CultureInfo.InvariantCulture, out var to))
                return "usage: quick yyyy-MM-dd HH:mm HH:mm";
            return surface.DebugRaiseSlot(date.Add(from), date.Add(to), allDay: false) + "\n" + QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("quick-set", args =>
        {
            if (_quickEvent is not { } popover) return "no quick event";
            foreach (var pair in args)
            {
                var split = pair.IndexOf('=');
                if (split < 0) continue;
                var key = pair[..split];
                var value = pair[(split + 1)..].Replace('_', ' ');
                switch (key)
                {
                    case "name": popover.DebugSetText(location: false, value); break;
                    case "location": popover.DebugSetText(location: true, value); break;
                    case "allday": popover.DebugSetAllDay(value == "on"); break;
                }
            }
            return QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("quick-save", _ =>
        {
            if (_quickEvent is not { } popover) return "no quick event";
            popover.Save();
            return QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("quick-more", _ =>
        {
            if (_quickEvent is not { } popover) return "no quick event";
            popover.MoreDetails();
            return QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("quick-close", _ =>
        {
            if (_quickEvent is not { } popover) return "no quick event";
            popover.Popover.Popdown();
            return QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("quick-state", args =>
        {
            if (args.Length > 0 && _quickEvent is { } popover) PopoverDebug.Capture(popover.Popover, args[0]);
            return QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("quick-readonly", args =>
        {
            if (ViewModel.SelectedQuickEventAccountCalendar is not { } calendar) return "no calendar";
            calendar.IsReadOnly = args.Length > 0 && args[0] == "on";
            ViewModel.SelectedQuickEventAccountCalendar = null;
            ViewModel.SelectedQuickEventAccountCalendar = calendar;
            return QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("selection", _ => surface.DescribeSelection()));
        Bindings.Own(CalendarDebugActions.AddVerb("select-range", args =>
        {
            if (args.Length < 4 || !int.TryParse(args[0], out var fromDay) || !int.TryParse(args[2], out var toDay) ||
                !TimeSpan.TryParse(args[1], CultureInfo.InvariantCulture, out var from) || !TimeSpan.TryParse(args[3], CultureInfo.InvariantCulture, out var to))
                return "usage: select-range day HH:mm day HH:mm [hold]";
            return surface.DebugSelectRange(fromDay, from, toDay, to, args.Length > 4 && args[4] == "hold") + "\n" + QuickState();
        }));
        Bindings.Own(CalendarDebugActions.AddVerb("select-cancel", _ =>
        {
            surface.CancelSlotSelection();
            return surface.DescribeSelection();
        }));
    }

    private string QuickState()
        => $"{_quickEvent?.Describe() ?? "quick=-"} vm: date={ViewModel.SelectedQuickEventDate:yyyy-MM-dd HH:mm} end={ViewModel.QuickEventSelectionEnd:yyyy-MM-dd HH:mm} start='{ViewModel.SelectedStartTimeString}' endTime='{ViewModel.SelectedEndTimeString}' allDay={ViewModel.IsAllDay} name='{ViewModel.EventName}' lastSaved={ViewModel.LastQuickCreationSucceeded} | {_surface?.DescribeSelection()}";
}
#endif
