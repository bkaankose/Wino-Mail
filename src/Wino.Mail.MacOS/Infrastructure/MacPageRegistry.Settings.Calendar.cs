using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account calendar settings. Owned by the calendar work (WS5).</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterCalendarSettingsPages()
    {
        Register<CalendarAccountSettingsPageViewController>(WinoPage.CalendarAccountSettingsPage, MacPageHost.SettingsWindow);
    }
}
