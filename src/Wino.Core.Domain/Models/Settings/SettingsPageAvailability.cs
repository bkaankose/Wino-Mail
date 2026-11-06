#nullable enable
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Settings;

/// <summary>
/// Which settings pages are listed and reachable on the current platform and account. The S/MIME page
/// needs certificate support from the platform; the Wino Intelligence page needs the Wino Account
/// entitlement to its surfaces.
/// </summary>
public static class SettingsPageAvailability
{
    public static bool IsAvailable(WinoPage page, bool isSmimeSupported, bool canAccessIntelligence) => page switch
    {
        WinoPage.SignatureAndEncryptionPage => isSmimeSupported,
        WinoPage.WinoIntelligencePage => canAccessIntelligence,
        _ => true
    };

    /// <summary>
    /// The full platform rule: besides S/MIME and the Wino Intelligence entitlement, the Companion page needs a tray
    /// or menu bar companion, Keyboard shortcuts needs hardware keyboard shortcut handling, and Backup and restore needs
    /// backup files the platform can save and open by path.
    /// </summary>
    public static bool IsAvailable(WinoPage page, IPlatformCapabilities capabilities, bool canAccessIntelligence) => page switch
    {
        WinoPage.CompanionSettingsPage => capabilities.Tray,
        WinoPage.KeyboardShortcutsPage => capabilities.KeyboardShortcuts,
        WinoPage.BackupRestorePage => capabilities.FileBackup,
        _ => IsAvailable(page, capabilities.Smime, canAccessIntelligence)
    };
}
