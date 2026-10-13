using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Calendar;
using Wino.Mail.UIKit.Views.Calendar;

namespace Wino.Mail.UIKit.Pad;

public sealed partial class PadPageRegistry
{
    /// <summary>
    /// Calendar: the calendar page is the content column; event details go to the reader column (the right-side details
    /// zone); the composer is a rendering-frame route the calendar page takes as its own frame (IRenderingFrameHost)
    /// and shows in place over the surface.
    /// </summary>
    partial void RegisterCalendarPages()
    {
        Register<CalendarPageViewController>(WinoPage.CalendarPage, UIKitPageHost.ShellContent);
        Register<EventDetailsPageViewController>(WinoPage.EventDetailsPage, UIKitPageHost.RenderingFrame);
        Register<CalendarEventComposePageViewController>(WinoPage.CalendarEventComposePage, UIKitPageHost.RenderingFrame);
    }
}
