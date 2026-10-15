using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// The Calendar mode's routes (UIKit PhonePageRegistry.Calendar and PadPageRegistry.Calendar): the calendar page is the
/// mode's shell content and takes new ranges in place; the event details are a rendering-frame page (full screen on
/// compact widths, the details pane elsewhere); the event editor is pushed full screen on every width.
/// </summary>
public static class CalendarPageRoutes
{
    public static AndroidPageRegistry RegisterCalendarPages(this AndroidPageRegistry registry)
    {
        registry.Register<CalendarPageFragment, CalendarPageViewModel>(WinoPage.CalendarPage, AndroidPageHost.ShellContent,
            initialize: CalendarPageFragment.InitializeAsync, renavigate: CalendarPageFragment.RenavigateAsync);
        registry.Register<EventDetailsFragment, EventDetailsPageViewModel>(WinoPage.EventDetailsPage, AndroidPageHost.RenderingFrame,
            initialize: EventDetailsFragment.InitializeAsync);
        registry.Register<CalendarEventComposeFragment, CalendarEventComposePageViewModel>(WinoPage.CalendarEventComposePage, AndroidPageHost.Push,
            initialize: static (viewModel, mode, parameter) => viewModel.InitializeNavigationAsync(mode, parameter));
        return registry;
    }
}
