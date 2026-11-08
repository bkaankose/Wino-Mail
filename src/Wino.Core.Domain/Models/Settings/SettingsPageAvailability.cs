#nullable enable
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Settings;

/// <summary>
/// Which settings pages are listed and reachable on the current platform. The S/MIME page needs certificate
/// support from the platform. The Wino Intelligence page is always listed because it also explains the
/// subscription to signed-out users; only its sub-pages are gated on the entitlement.
/// </summary>
public static class SettingsPageAvailability
{
    public static bool IsAvailable(WinoPage page, bool isSmimeSupported) => page switch
    {
        WinoPage.SignatureAndEncryptionPage => isSmimeSupported,
        _ => true
    };

    /// <summary>
    /// The full platform rule: besides S/MIME, the Companion page needs a tray
    /// or menu bar companion, Widgets / Live Tiles needs Windows widgets and tiles, Keyboard shortcuts needs hardware keyboard shortcut handling, and Backup and restore needs
    /// backup files the platform can save and open by path.
    /// </summary>
    public static bool IsAvailable(WinoPage page, IPlatformCapabilities capabilities) => page switch
    {
        WinoPage.CompanionSettingsPage => capabilities.Tray,
        WinoPage.WidgetsLiveTilesSettingsPage => capabilities.WidgetsAndLiveTiles,
        WinoPage.VoiceControlSettingsPage => capabilities.VoiceControl,
        WinoPage.VoiceBrainPage => capabilities.VoiceControl,
        WinoPage.KeyboardShortcutsPage => capabilities.KeyboardShortcuts,
        WinoPage.BackupRestorePage => capabilities.FileBackup,
        _ => IsAvailable(page, capabilities.Smime)
    };
}
