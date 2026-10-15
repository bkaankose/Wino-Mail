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
}
