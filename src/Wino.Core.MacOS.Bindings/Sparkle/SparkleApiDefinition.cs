using Foundation;
using ObjCRuntime;

namespace Wino.Core.MacOS.Bindings.Sparkle;

// The part of Sparkle 2 (Sparkle/Native/Sparkle.xcframework) that Wino uses: the standard updater
// with Sparkle's own update windows, and the user's automatic-update settings. See the Sparkle
// headers of the release named in Sparkle/Native/Sparkle.version.

[BaseType(typeof(NSObject))]
[DisableDefaultCtor]
interface SPUStandardUpdaterController
{
    // Delegates are not bound: Wino passes nil for both.
    [Export("initWithStartingUpdater:updaterDelegate:userDriverDelegate:")]
    [DesignatedInitializer]
    NativeHandle Constructor(bool startUpdater, [NullAllowed] NSObject updaterDelegate, [NullAllowed] NSObject userDriverDelegate);

    [Export("updater")]
    SPUUpdater Updater { get; }

    [Export("startUpdater")]
    void StartUpdater();

    [Export("checkForUpdates:")]
    void CheckForUpdates([NullAllowed] NSObject sender);
}

[BaseType(typeof(NSObject))]
[DisableDefaultCtor]
interface SPUUpdater
{
    [Export("automaticallyChecksForUpdates")]
    bool AutomaticallyChecksForUpdates { get; set; }

    [Export("automaticallyDownloadsUpdates")]
    bool AutomaticallyDownloadsUpdates { get; set; }

    [Export("allowsAutomaticUpdates")]
    bool AllowsAutomaticUpdates { get; }

    [Export("canCheckForUpdates")]
    bool CanCheckForUpdates { get; }

    [Export("sessionInProgress")]
    bool SessionInProgress { get; }

    [NullAllowed, Export("feedURL")]
    NSUrl FeedUrl { get; }

    [NullAllowed, Export("lastUpdateCheckDate")]
    NSDate LastUpdateCheckDate { get; }
}
