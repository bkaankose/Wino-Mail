using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account calendar settings views and ViewModels. Owned by the calendar work (WS5).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterCalendarSettingsViews(IServiceCollection services)
    {
        // Same transient lifetime as the WinUI registration (App.xaml.cs).
        services.AddTransient<CalendarAccountSettingsPageViewModel>();
        services.AddTransient<CalendarAccountSettingsPageViewController>();

        // "Calendar Account Settings…" on the shell pane's calendar rows (and the debug command).
        CalendarAccountSettingsEntryPoints.Register();
    }
}
