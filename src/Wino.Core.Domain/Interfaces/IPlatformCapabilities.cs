namespace Wino.Core.Domain.Interfaces;

public interface IPlatformCapabilities
{
    bool Printing { get; }
    bool PdfExport { get; }
    bool Smime { get; }
    bool AdditionalWindows { get; }
    bool Notifications { get; }
    bool NotificationActions { get; }
    bool StartupIntegration { get; }
    bool Tray { get; }
    bool GlobalHotkeys { get; }
    bool GeneralActivation { get; }
    bool MicrosoftStore { get; }
    bool AppleAppStore { get; }

    /// <summary>The app handles configurable hardware keyboard shortcuts (the Keyboard shortcuts settings page).</summary>
    bool KeyboardShortcuts { get; }

    /// <summary>
    /// The platform saves and opens backup files by path, so the Backup and restore settings page can export and
    /// import a snapshot file.
    /// </summary>
    bool FileBackup { get; }

    /// <summary>
    /// The app sells add-ons here (a store, or Wino Account checkout). False on Android v1, which has no Play Billing:
    /// purchase entry points are hidden and the account limit is only explained.
    /// </summary>
    bool Purchases { get; }

    /// <summary>The platform can receive relay pushes (APNs). Live notifications are always on when signed in unless <see cref="PushRelayOptIn"/>.</summary>
    bool RemoteNotifications { get; }

    /// <summary>Live notifications are an opt-in setting on this platform (off by default).</summary>
    bool PushRelayOptIn { get; }

    /// <summary>Each app mode has its own taskbar entry, so the window's taskbar identity can follow the mode (Windows).</summary>
    bool AppModeTaskbarIdentity { get; }

    /// <summary>The mail list has a hovering pointer, so hover actions are offered.</summary>
    bool PointerHover { get; }

    /// <summary>The mail list offers more than one row density.</summary>
    bool MailListDensity { get; }

    /// <summary>The app can tell when the user is presenting or sharing the screen, so notifications can wait.</summary>
    bool PresentationDetection { get; }

    /// <summary>
    /// The app synchronizes on its own timer at the user's chosen intervals. False on mobile, where the system decides
    /// when the app runs in the background and the foreground loop uses the default intervals.
    /// </summary>
    bool ConfigurableSyncIntervals { get; }

    /// <summary>The unread badge sits on a taskbar or Dock button that launches the app, so a launch can open the counted folder.</summary>
    bool BadgeLaunchNavigation { get; }
}
