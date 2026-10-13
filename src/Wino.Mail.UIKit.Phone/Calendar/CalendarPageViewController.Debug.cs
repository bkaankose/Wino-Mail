#if DEBUG
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>iPhone bridge commands: calagenda on|off, calheader (toggles the month picker), calcalendars (the filter sheet).</summary>
public sealed partial class CalendarPageViewController
{
    protected override void RegisterIdiomDebugCommands()
    {
        UIKitDebugBridge.Register("calagenda", args =>
        {
            if (Released) return Task.FromResult("calendar page released");
            SetAgenda(args.Length == 0 ? !_agendaOn : args[0].Equals("on", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult($"agenda={_agendaOn} days={_agendaDays.Count} rows={_agendaDays.Sum(day => day.Items.Count)}");
        });
        UIKitDebugBridge.Register("calheader", _ =>
        {
            if (Released) return Task.FromResult("calendar page released");
            SetHeaderExpanded(!_headerExpanded);
            return Task.FromResult("expanded=" + _headerExpanded);
        });
        UIKitDebugBridge.Register("calcalendars", _ =>
        {
            if (Released) return Task.FromResult("calendar page released");
            ShowCalendars();
            return Task.FromResult("ok");
        });
    }

    protected override string DebugLayoutInfo()
        => $"agenda={_agendaOn} header={_headerExpanded} title='{NavigationItem.Title}' stack={string.Join(">", NavigationController?.ViewControllers?.Select(page => page.GetType().Name) ?? Array.Empty<string>())}";
}
#endif
