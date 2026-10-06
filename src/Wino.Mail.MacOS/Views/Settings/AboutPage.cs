using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

public sealed class AboutPage : NSView
{
    public AboutPage(params NSView[] content) => Layout.Fill(Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, content), this, 28);
}
