using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Calendar;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Calendar mode routes. Owned by the Calendar feature. The calendar page is shell content; event
/// details and the composer route to the rendering frame, which the calendar page implements as
/// its right-hand details zone (details) and a sheet (compose).
/// </summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterCalendarPages()
    {
        Register<CalendarPageViewController>(WinoPage.CalendarPage, MacPageHost.ShellContent);
        Register<EventDetailsPageViewController>(WinoPage.EventDetailsPage, MacPageHost.RenderingFrame);
        Register<CalendarEventComposePageViewController>(WinoPage.CalendarEventComposePage, MacPageHost.RenderingFrame);
    }
}
