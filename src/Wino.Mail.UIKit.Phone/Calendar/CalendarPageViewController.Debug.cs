#if DEBUG
using System.Globalization;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// iPhone bridge commands: calagenda on|off, calheader (toggles the date selector), calpanel (describes it), calview
/// Day|Week|WorkWeek|Month|list|calendars (picks a view menu item), calviewmenu (lists the menu), calcapsule (the Today
/// capsule state; "tap" taps it), calmonthtap YYYY-MM-DD [double] (taps or double-taps a compact month cell), calstrip
/// [YYYY-MM-DD] (describes the week strip or taps a day), calheadertap N (taps the Nth Week header day), calopenday.
/// </summary>
public sealed partial class CalendarPageViewController
{
    protected override void RegisterIdiomDebugCommands()
    {
        Register("calagenda", args =>
        {
            SetAgenda(args.Length == 0 ? !_agendaOn : args[0].Equals("on", StringComparison.OrdinalIgnoreCase));
            return $"agenda={_agendaOn} days={_agendaDays.Count} rows={_agendaDays.Sum(day => day.Items.Count)}";
        });
        Register("calheader", _ =>
        {
            TogglePanel();
            return "panel=" + (_panel?.IsOpen == true);
        });
        Register("calpanel", _ => _panel?.Describe() ?? "no panel");
        Register("calview", args =>
        {
            if (args.Length == 0) return "usage: calview Day|Week|WorkWeek|Month|list|calendars";
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    SetAgenda(!_agendaOn);
                    break;
                case "calendars":
                    OpenCalendars();
                    break;
                default:
                    PickView(Enum.Parse<CalendarDisplayType>(args[0], true));
                    break;
            }
            return $"type={_displayType} agenda={_agendaOn}";
        });
        Register("calviewmenu", _ =>
        {
            if (_viewItem?.Menu is not { } menu) return "no menu";
            var lines = menu.Children.OfType<UIMenu>().SelectMany(group => group.Children)
                .Select(child => child is UIAction action && action.State == UIMenuElementState.On ? "[x] " + child.Title : child.Title);
            return string.Join(" | ", lines) + $" glyph={(_agendaOn ? "list" : _displayType.ToString())}";
        });
        Register("calcapsule", args =>
        {
            if (_capsule is not { } capsule) return "no capsule";
            if (args.Length > 0 && args[0] == "tap") TodayCapsuleTapped();
            return $"shown={capsule.IsShown} direction={capsule.Direction} frame={capsule.Frame}";
        });
        Register("calmonthtap", args =>
        {
            if (args.Length == 0 || !DateOnly.TryParse(args[0], CultureInfo.InvariantCulture, out var date)) return "usage: calmonthtap YYYY-MM-DD [double]";
            if (args.Length > 1 && args[1] == "double") OpenDay(date);
            else SurfaceSlotSelected(Surface, new Wino.Mail.Controls.UIKit.Calendar.CalendarSlotClickedEventArgs(date.ToDateTime(TimeOnly.MinValue), CoreGraphics.CGRect.Empty, true));
            return $"selected={_monthSelection:yyyy-MM-dd} compact={_page?.IsCompactMonth} list={_monthDays.Sum(day => day.Items.Count)} title='{_monthDayTitle.Text}'";
        });
        Register("calstrip", args =>
        {
            if (_page is not { } page) return "no page";
            if (args.Length > 0 && DateOnly.TryParse(args[0], CultureInfo.InvariantCulture, out var date)) StripDateTapped(date);
            return $"visible={page.IsStripVisible} selected={page.Strip.SelectedDate:yyyy-MM-dd} weekStart={page.Strip.WeekStart:yyyy-MM-dd}";
        });
        Register("calpinch", args => args.Length > 0 && double.TryParse(args[0], CultureInfo.InvariantCulture, out var height)
            ? Surface.DebugPinch(height) : "usage: calpinch HOURHEIGHT");
        Register("calheadertap", args =>
        {
            int index = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 0;
            var dates = _range?.Dates ?? [];
            if (index < 0 || index >= dates.Count) return $"only {dates.Count} days";
            OpenDay(dates[index]);
            return "opened " + dates[index].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        });
    }

    private void Register(string name, Func<string[], string> handler)
        => UIKitDebugBridge.Register(name, args => Task.FromResult(Released ? "calendar page released" : handler(args)));

    protected override string DebugLayoutInfo()
        => $"agenda={_agendaOn} panel={_panel?.IsOpen == true} type={_displayType} title='{_titleButton?.AccessibilityLabel}' " +
           $"left=[{string.Join(",", (NavigationItem.LeftBarButtonItems ?? []).Select(static item => item.CustomView?.GetType().Name ?? item.AccessibilityLabel))}] " +
           $"strip={_page?.IsStripVisible} compactMonth={_page?.IsCompactMonth} monthSel={_monthSelection:yyyy-MM-dd} capsule={_capsule?.IsShown}:{_capsule?.Direction} " +
           $"stack={string.Join(">", NavigationController?.ViewControllers?.Select(page => page.GetType().Name) ?? Array.Empty<string>())}";
}
#endif
