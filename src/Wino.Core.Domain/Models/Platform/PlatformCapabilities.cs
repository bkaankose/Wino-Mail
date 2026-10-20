using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Platform;

/// <summary>Immutable platform availability; independent of account entitlements.</summary>
public sealed record PlatformCapabilities(
    bool Printing = false,
    bool PdfExport = false,
    bool Smime = false,
    bool AdditionalWindows = false,
    bool Notifications = false,
    bool NotificationActions = false,
    bool StartupIntegration = false,
    bool Tray = false,
    bool GlobalHotkeys = false,
    bool GeneralActivation = false,
    bool MicrosoftStore = false,
    bool AppleAppStore = false,
    bool RemoteNotifications = false,
    bool KeyboardShortcuts = false,
    bool FileBackup = false,
    bool Purchases = false,
    bool AppModeTaskbarIdentity = false,
    bool PointerHover = false,
    bool MailListDensity = false,
    bool PresentationDetection = false,
    bool ConfigurableSyncIntervals = false,
    bool BadgeLaunchNavigation = false,
    WinoIconSet DefaultIconSet = WinoIconSet.Fluent) : IPlatformCapabilities;
