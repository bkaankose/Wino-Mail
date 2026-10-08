namespace Wino.Core.Domain.Enums;

public enum WindowBackdropType
{
    None = 0,
    Mica = 1,
    MicaAlt = 2,
    DesktopAcrylic = 3,

    // Retained to migrate existing persisted settings. WinUI 3 exposes no
    // equivalent acrylic variants, so both map to DesktopAcrylic.
    [System.Obsolete("Use DesktopAcrylic. This value is retained for settings migration.")]
    AcrylicBase = 4,

    [System.Obsolete("Use DesktopAcrylic. This value is retained for settings migration.")]
    AcrylicThin = 5
}
