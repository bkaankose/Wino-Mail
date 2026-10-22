#if DEBUG
using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Messages;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.UIKit.Calendar;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// Debug bridge commands, the Mac set: calprev, calnext, caltoday, caltype Day|Week|WorkWeek|Month, calselect [N],
/// calinfo, calnew [ACCOUNT], calnew-notes [HTML], cal-readonly-info, cal-details N, cal-details-showas N VALUE,
/// cal-details-reminder N MINUTES, cal-attach-save [N], cal-range DAYINDEX HH:MM HH:MM [DAYINDEX2], calslot; plus the iOS
/// additions calmenu N (the context menu of the Nth visible item), calquick NAME (saves the open quick event) and
/// caldetails-close (clears the details item).
/// </summary>
public abstract partial class CalendarPageViewControllerBase
{
    private List<CalendarItemViewModel> VisibleItems()
        => ViewModel.CalendarItems.Where(item => ViewModel.CurrentVisibleRange?.Contains(item.StartDate) == true).OrderBy(item => item.StartDate).ToList();

    /// <summary>Idiom pages add their own commands here.</summary>
    protected virtual void RegisterIdiomDebugCommands() { }

    /// <summary>A short description of the idiom layout for calinfo.</summary>
    protected virtual string DebugLayoutInfo() => string.Empty;

    private void Register(string name, Func<string[], Task<string>> handler)
        => UIKitDebugBridge.Register(name, args => Released ? Task.FromResult("calendar page released") : handler(args));

    private void Register(string name, Func<string[], string> handler)
        => Register(name, args => Task.FromResult(handler(args)));

    private void RegisterDebugCommands()
    {
        Register("calprev", _ => { NavigatePeriods(-1); return "ok"; });
        Register("calnext", _ => { NavigatePeriods(1); return "ok"; });
        Register("calrail", _ => Surface.DescribeRail());
        Register("calrailpos", args => { Surface.DebugSetRailPosition(double.Parse(args[0], CultureInfo.InvariantCulture)); return Surface.DescribeRail(); });
        Register("calrailsettle", _ => { Surface.DebugSettleRail(); return "ok"; });
        Register("caltoday", _ => { GoToday(); return "ok"; });
        Register("caltype", args => { RequestDisplayType(Enum.Parse<CalendarDisplayType>(args[0], true)); return "ok"; });
        Register("calselect", args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
            var visible = VisibleItems();
            if (index < 0 || index >= visible.Count) return $"only {visible.Count} visible items";
            SelectItem(visible[index]);
            return "selected " + visible[index].Title;
        });
        Register("calinfo", _ =>
            $"range={ViewModel.CurrentVisibleRange?.StartDate}..{ViewModel.CurrentVisibleRange?.EndDate} type={ViewModel.CurrentVisibleRange?.DisplayType} items={ViewModel.CalendarItems.Count} " +
            $"active={Shell.AccountCalendarStateService.ActiveCalendars.Count()} groups={Shell.AccountCalendarStateService.GroupedAccountCalendars.Count} " +
            $"all={Shell.AccountCalendarStateService.AllCalendars.Count()} ready={ViewModel.Readiness.IsReady} surfaceItems={Surface.Items.Count} " +
            $"details={CurrentDetails?.GetType().Name} surface={Surface.Bounds.Size} {DebugLayoutInfo()}");
        Register("calnew", args =>
        {
            // The shell's NewEvent needs the calendar picker dialog; open the composer directly for a visual check.
            var start = (ViewModel.CurrentVisibleRange?.Dates.FirstOrDefault() ?? DateContext.GetToday()).ToDateTime(new TimeOnly(10, 0));
            var calendar = Shell.AccountCalendarStateService.ActiveCalendars.FirstOrDefault(item => args.Length == 0
                || (item.MailAccount?.Address?.Contains(args[0], StringComparison.OrdinalIgnoreCase) ?? false));
            Navigation?.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
            {
                SelectedCalendarId = calendar?.Id, StartDate = start, EndDate = start.AddMinutes(30), Title = "Design sync · iOS shell", Location = "Microsoft Teams"
            });
            return "ok";
        });
        Register("calnew-notes", args =>
        {
            var start = (ViewModel.CurrentVisibleRange?.Dates.FirstOrDefault() ?? DateContext.GetToday()).ToDateTime(new TimeOnly(10, 0));
            var calendar = Shell.AccountCalendarStateService.ActiveCalendars.FirstOrDefault(item => !item.IsReadOnly);
            Navigation?.Navigate(WinoPage.CalendarEventComposePage, new CalendarEventComposeNavigationArgs
            {
                SelectedCalendarId = calendar?.Id, StartDate = start, EndDate = start.AddMinutes(30), Title = "Notes check",
                NotesHtml = args.Length > 0 ? string.Join(' ', args) : "<div dir=\"ltr\"><b>Agenda</b>&nbsp;<a href=\"https://example.com\">link</a></div>\r\n"
            });
            return "ok";
        });
        Register("cal-readonly-info", _ =>
        {
            var calendars = Shell.AccountCalendarStateService.AllCalendars.Where(calendar => calendar.IsReadOnly).Select(calendar => $"{calendar.Name} ({calendar.Id})").ToList();
            var readOnlyItems = VisibleItems().Select((item, index) => (item, index)).Where(pair => CalendarTileMapper.IsReadOnly(pair.item)).Select(pair => $"{pair.index}:{pair.item.Title}").ToList();
            var quick = ViewModel.SelectedQuickEventAccountCalendar;
            return $"readOnlyCalendars=[{string.Join("; ", calendars)}] visibleReadOnlyItems=[{string.Join("; ", readOnlyItems)}] " +
                $"quickCalendar={quick?.Name} quickReadOnly={quick?.IsReadOnly} canSaveQuick={ViewModel.CanSaveQuickEvent} details={CurrentDetails?.DescribeReadOnlyState()}";
        });
        Register("cal-details", async args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
            var visible = VisibleItems();
            if (index < 0 || index >= visible.Count) return $"only {visible.Count} visible items";
            SelectItem(visible[index]);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100);
                if (CurrentDetails is { } details && ReferenceEquals(ViewModel.DisplayDetailsCalendarItemViewModel, visible[index]))
                    return details.DescribeReadOnlyState();
            }
            return "details did not open";
        });
        Register("cal-details-showas", args => CurrentDetails is { } details && args.Length > 0
            ? details.DebugChooseShowAsAsync(args[^1]) : Task.FromResult("open an event's details first (cal-details N)"));
        Register("cal-details-reminder", args => CurrentDetails is { } details && args.Length > 0 && int.TryParse(args[^1], out var minutes)
            ? details.DebugToggleReminderAsync(minutes) : Task.FromResult("open an event's details first (cal-details N)"));
        Register("cal-attach-save", args => CurrentDetails is { } details
            ? details.DebugSaveAttachment(args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0) : "open an event's details first (cal-details N)");
        Register("caldetails-close", _ => { ResetDetailsItem(); return "ok"; });
        // "cal-range DAYINDEX HH:MM HH:MM [DAYINDEX2]" simulates a range drag on the hour grid.
        Register("cal-range", args =>
        {
            if (args.Length < 3) return "usage: cal-range DAYINDEX HH:MM HH:MM [DAYINDEX2]";
            var dates = ViewModel.CurrentVisibleRange?.Dates ?? Array.Empty<DateOnly>();
            int first = int.Parse(args[0], CultureInfo.InvariantCulture);
            int last = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : first;
            if (first < 0 || last < 0 || first >= dates.Count || last >= dates.Count) return $"only {dates.Count} visible days";
            var start = dates[first].ToDateTime(TimeOnly.Parse(args[1], CultureInfo.InvariantCulture));
            var end = dates[last].ToDateTime(TimeOnly.Parse(args[2], CultureInfo.InvariantCulture));
            DebugSlot(start, end);
            return $"quick={ViewModel.SelectedQuickEventDate:g} {ViewModel.SelectedStartTimeString}–{ViewModel.SelectedEndTimeString} end={ViewModel.QuickEventSelectionEnd:g} allDay={ViewModel.IsAllDay} highlight={Surface.SelectionRange?.Start:g}..{Surface.SelectionRange?.End:g}";
        });
        Register("calslot", _ =>
        {
            var date = ViewModel.CurrentVisibleRange?.Dates.FirstOrDefault() ?? DateContext.GetToday();
            var start = date.ToDateTime(new TimeOnly(10, 0));
            DebugSlot(start, start.AddMinutes(30));
            return "ok";
        });
        Register("calquick", args => OpenQuickEventController is { } quick ? quick.DebugSave(args.Length > 0 ? string.Join(' ', args) : "Quick event") : "no quick event open");
        Register("calmenu", args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
            var visible = VisibleItems();
            if (index < 0 || index >= visible.Count) return $"only {visible.Count} visible items";
            ShowContextMenu(visible[index], new CGPoint(Surface.Bounds.GetMidX(), Surface.Bounds.GetMidY()));
            var menu = ContextMenu(visible[index]);
            return string.Join(" | ", menu.Children.Select(child => child.Title));
        });
        RegisterIdiomDebugCommands();
    }

    /// <summary>The slot path of a long-press range: a popover anchored mid-surface on iPad, the sheet on iPhone.</summary>
    private void DebugSlot(DateTime start, DateTime end)
    {
        var anchor = QuickEventAnchor(new CalendarSlotClickedEventArgs(start, new CGRect(WinoCalendarSurfaceView.HourColumnWidth, 200, 120, 26), false, end));
        if (ViewModel.CurrentVisibleRange?.DisplayType == CalendarDisplayType.Month) start = start.Date;
        OpenQuickEvent(start, end, anchor?.View, anchor?.Rect ?? CGRect.Empty);
    }
}
#endif
