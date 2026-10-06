using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class AccountSetupProgressPage : NSView
{
    public AccountSetupProgressPage(params NSView[] content) => Wino.Presentation.AppKit.Layout.Fill(Wino.Presentation.AppKit.Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, content), this, 28);
}
