using AppKit;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views;

public sealed class WelcomePageV2 : NSView
{
    public WelcomePageV2(NSButton getStarted, NSButton importAccount, NSButton importFile, NSTextField status)
    {
        var title = NSTextField.CreateLabel(Translator.WelcomeWindow_Title);
        title.Font = NSFont.BoldSystemFontOfSize(26);
        Wino.Presentation.AppKit.Layout.Fill(Wino.Presentation.AppKit.Layout.Stack(NSUserInterfaceLayoutOrientation.Vertical, title,
            NSTextField.CreateLabel(Translator.WelcomeWindow_AppDescription), getStarted, importAccount, importFile, status), this, 32);
    }
}
