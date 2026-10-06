using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

public sealed class MailListPage : NSView
{
    public MailListPage(params NSView[] content) => Wino.Presentation.AppKit.Layout.Fill(Wino.Presentation.AppKit.Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, content), this, 12);
}
