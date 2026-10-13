using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Phone.Calendar;

namespace Wino.Mail.UIKit.Phone;

public static partial class PhoneViewRegistration
{
    /// <summary>The iPhone calendar page.</summary>
    static partial void RegisterCalendarViews(IServiceCollection services)
    {
        services.AddTransient<CalendarPageViewController>();
    }
}
