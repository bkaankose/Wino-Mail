#nullable enable
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Settings;

/// <summary>
/// Which individual settings the current platform supports. Settings pages show a setting only when it is available,
/// and the shared services ignore the stored value of an unavailable one. Page-level rules are in
/// <see cref="SettingsPageAvailability"/>.
/// </summary>
public static class SettingAvailability
{
    public static bool IsAvailable(PlatformSetting setting, IPlatformCapabilities capabilities) => setting switch
    {
        PlatformSetting.AppModeTaskbarIdentity => capabilities.AppModeTaskbarIdentity,
        PlatformSetting.StoreUpdateNotifications => capabilities.MicrosoftStore,
        PlatformSetting.LaunchAtSignIn => capabilities.StartupIntegration,
        PlatformSetting.CloseBehavior => capabilities.Tray,
        PlatformSetting.HoverActions => capabilities.PointerHover,
        PlatformSetting.MailListDensity => capabilities.MailListDensity,
        PlatformSetting.SnoozeWhilePresenting => capabilities.PresentationDetection,
        PlatformSetting.SyncIntervals => capabilities.ConfigurableSyncIntervals,
        PlatformSetting.BadgeLaunchNavigation => capabilities.BadgeLaunchNavigation,
        _ => true
    };
}
