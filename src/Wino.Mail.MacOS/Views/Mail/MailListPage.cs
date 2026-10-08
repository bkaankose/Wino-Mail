using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>Root view of the mail page. Hosts the list/reader split; the theme backdrop shows behind both panes.</summary>
public sealed class MailListPage : WinoSurfaceView
{
    public MailListPage()
    {
        Fill = null; // the shell paints the theme backdrop behind both panes
        AccessibilityElement = false;
    }
}
