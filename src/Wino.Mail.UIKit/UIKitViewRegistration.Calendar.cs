using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Views.Calendar;

namespace Wino.Mail.UIKit;

public static partial class UIKitViewRegistration
{
    /// <summary>
    /// The idiom-neutral Calendar pages: event details (pushed on iPhone, the reader column on iPad) and the event
    /// composer, which the calendar page shows as its own rendering frame (a full-screen sheet on iPhone, in place on
    /// iPad). The calendar page itself is per idiom.
    /// </summary>
    static partial void RegisterCalendarViews(IServiceCollection services)
    {
        services.AddTransient<EventDetailsPageViewController>();
        services.AddTransient<CalendarEventComposePageViewController>();
#if DEBUG
        services.AddSingleton<Wino.Mail.ViewModels.Lifecycle.IApplicationLifecycleHook, CalendarDebugCommands>();
#endif
    }
}
