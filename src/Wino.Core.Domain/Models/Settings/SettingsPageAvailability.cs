#nullable enable
using Wino.Core.Domain.Enums;

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
}
