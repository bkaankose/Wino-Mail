using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

public sealed class AboutPage : NSView
{
    public AboutPage(params NSView[] content) => Wino.Presentation.AppKit.Layout.Fill(Wino.Presentation.AppKit.Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, content), this, 28);
}
