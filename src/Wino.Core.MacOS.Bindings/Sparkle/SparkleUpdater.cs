namespace Wino.Core.MacOS.Bindings.Sparkle;

/// <summary>
/// Sparkle's standard updater for builds distributed outside the Mac App Store (the DMG). It reads
/// SUFeedURL and SUPublicEDKey from Info.plist, schedules its own background checks and shows its own
/// update windows. Mac App Store builds do not contain Sparkle or this type. Call from the main thread.
/// </summary>
public static class SparkleUpdater
{
    private static SPUStandardUpdaterController? _controller;

    /// <summary>Starts the updater once. Later calls do nothing.</summary>
    public static void Start() => _controller ??= new SPUStandardUpdaterController(true, null, null);

    public static bool IsStarted => _controller is not null;

    /// <summary>False while an update session is already running; Sparkle then ignores new checks.</summary>
    public static bool CanCheckForUpdates => _controller?.Updater.CanCheckForUpdates ?? false;

    /// <summary>Shows Sparkle's update window for a user-initiated check.</summary>
    public static void CheckForUpdates() => _controller?.CheckForUpdates(null);

    public static bool AutomaticallyChecksForUpdates
    {
        get => _controller?.Updater.AutomaticallyChecksForUpdates ?? false;
        set
        {
            if (_controller is { } controller) controller.Updater.AutomaticallyChecksForUpdates = value;
        }
    }

    /// <summary>False when the user cannot write to the app's location; Sparkle then asks before installing.</summary>
    public static bool AllowsAutomaticUpdates => _controller?.Updater.AllowsAutomaticUpdates ?? false;

    public static bool AutomaticallyDownloadsUpdates
    {
        get => _controller?.Updater.AutomaticallyDownloadsUpdates ?? false;
        set
        {
            if (_controller is { } controller) controller.Updater.AutomaticallyDownloadsUpdates = value;
        }
    }
}
