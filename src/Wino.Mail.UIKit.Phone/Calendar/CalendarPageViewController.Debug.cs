#if DEBUG
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>iPhone bridge commands: calagenda on|off, calheader (toggles the date panel).</summary>
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
            TogglePanel();
            return Task.FromResult("panel=" + (_panel?.IsOpen == true));
        });
    }

    protected override string DebugLayoutInfo()
        => $"agenda={_agendaOn} panel={_panel?.IsOpen == true} type={_displayType} title='{NavigationItem.Title}' stack={string.Join(">", NavigationController?.ViewControllers?.Select(page => page.GetType().Name) ?? Array.Empty<string>())}";
}
#endif
