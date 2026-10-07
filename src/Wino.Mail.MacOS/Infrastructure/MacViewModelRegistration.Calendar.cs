using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Mail.MacOS.Views.Calendar;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Calendar mode views and ViewModels. Owned by the Calendar feature. CalendarPageViewModel and
/// CalendarAppShellViewModel are shared singletons registered in MacViewModelRegistration.cs.
/// </summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterCalendarViews(IServiceCollection services)
    {
        services.AddTransient<EventDetailsPageViewModel>();
        services.AddTransient<CalendarEventComposePageViewModel>();

        services.AddTransient<CalendarPageViewController>();
        services.AddTransient<EventDetailsPageViewController>();
        services.AddTransient<CalendarEventComposePageViewController>();
    }
}
