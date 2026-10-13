using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Calendar;
using Wino.Mail.UIKit.Views.Calendar;

namespace Wino.Mail.UIKit.Phone;

public sealed partial class PhonePageRegistry
{
    /// <summary>
    /// Calendar: the calendar page is the Calendar tab's root; event details push on that stack; the composer is a
    /// rendering-frame route the calendar page takes as its own frame (IRenderingFrameHost) and presents as a full-screen sheet.
    /// </summary>
    partial void RegisterCalendarPages()
    {
        Register<CalendarPageViewController>(WinoPage.CalendarPage, UIKitPageHost.ShellContent);
        Register<EventDetailsPageViewController>(WinoPage.EventDetailsPage, UIKitPageHost.Push);
        Register<CalendarEventComposePageViewController>(WinoPage.CalendarEventComposePage, UIKitPageHost.RenderingFrame);
    }
}
