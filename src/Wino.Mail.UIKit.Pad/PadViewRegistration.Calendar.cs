using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Pad.Calendar;

namespace Wino.Mail.UIKit.Pad;

public static partial class PadViewRegistration
{
    /// <summary>The iPad calendar page.</summary>
    static partial void RegisterCalendarViews(IServiceCollection services)
    {
        services.AddTransient<CalendarPageViewController>();
    }
}
