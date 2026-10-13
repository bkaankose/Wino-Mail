using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Calendar;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Calendar mode views and ViewModels. Owned by the Calendar feature. CalendarPageViewModel and
/// CalendarAppShellViewModel are shared singletons registered by RegisterWinoViewModels.
/// </summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterCalendarViews(IServiceCollection services)
    {
        services.AddTransient<CalendarPageViewController>();
        services.AddTransient<EventDetailsPageViewController>();
        services.AddTransient<CalendarEventComposePageViewController>();
    }
}
