#if !WINO_APPSTORE
using AppKit;
using Wino.Core.Domain;
using Wino.Core.MacOS.Bindings.Sparkle;

namespace Wino.Mail.MacOS;

/// <summary>
/// Updates for builds outside the Mac App Store (DMG): Sparkle checks the feed in Sparkle.plist daily
/// and shows its own update window. Mac App Store builds compile none of this; the App Store updates them.
/// </summary>
public sealed partial class AppDelegate
{
    partial void UpdatesLaunching() => SparkleUpdater.Start();

    partial void AddUpdateMenuItems(NSMenu application)
        => application.AddItem(new NSMenuItem(Translator.MacOSMenu_CheckForUpdates, (_, _) => SparkleUpdater.CheckForUpdates()));
}
#endif
