namespace Wino.Core.Domain.Enums;

/// <summary>
/// Settings that only some platforms support. <see cref="Models.Settings.SettingAvailability"/> decides from the
/// platform capabilities whether a setting is shown and whether the shared services apply its stored value.
/// </summary>
public enum PlatformSetting
{
    /// <summary>The window's taskbar identity follows the app mode (Windows packaged app entries).</summary>
    AppModeTaskbarIdentity,

    /// <summary>Notifications about Microsoft Store updates.</summary>
    StoreUpdateNotifications,

    /// <summary>Launch Wino when the user signs in.</summary>
    LaunchAtSignIn,

    /// <summary>What closing the main window does (tray or menu bar, hidden, quit).</summary>
    CloseBehavior,

    /// <summary>Mail list hover actions.</summary>
    HoverActions,

    /// <summary>Mail list row density.</summary>
    MailListDensity,

    /// <summary>Hold notifications while presenting or sharing the screen.</summary>
    SnoozeWhilePresenting,

    /// <summary>User-chosen mail and calendar synchronization intervals.</summary>
    SyncIntervals,

    /// <summary>Open the folder the unread badge counts when the app is launched.</summary>
    BadgeLaunchNavigation,

    /// <summary>The app-wide choice between timer-based and relay-based sync. Mobile always uses the relay.</summary>
    SyncMethod
}
