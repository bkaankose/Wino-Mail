using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Linux.Views.Calendar;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterCalendarPages()
    {
        Register<CalendarPageView>(WinoPage.CalendarPage, LinuxPageHost.ShellContent);
        Register<EventDetailsView>(WinoPage.EventDetailsPage, LinuxPageHost.RenderingFrame);
        Register<CalendarEventComposeView>(WinoPage.CalendarEventComposePage, LinuxPageHost.RenderingFrame);
        // The shared calendar shell navigates the composer straight away when a mode switch carries compose arguments
        // (To Do's Create calendar event, an imported .ics); Windows shows it in the calendar frame, here it covers the page.
        RequireContainer(WinoPage.CalendarEventComposePage, WinoPage.CalendarPage, static parameter => new CalendarPageNavigationArgs
        {
            NavigationDate = parameter is CalendarEventComposeNavigationArgs { StartDate: var start } && start != default ? start.Date : DateTime.Today
        });
    }
}
