using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class AccountSetupProgressPage : NSView
{
    public AccountSetupProgressPage(params NSView[] content) => Layout.Fill(Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, content), this, 28);
}
