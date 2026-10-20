namespace Wino.Core.Domain.Enums;

/// <summary>
/// Which family of WinoIcons fonts the app draws with (icons/sets.json). Every set keeps the same codepoints,
/// so switching sets only switches the font; <see cref="WinoIconStyle"/> picks its monochrome or colorful variant.
/// Stored as a preference: never renumber.
/// </summary>
public enum WinoIconSet
{
    /// <summary>The original WinoIcons outlines (icons/manifest.json and icons/svg).</summary>
    Classic = 0,

    /// <summary>Fluent UI System Icons, 20px Regular with Filled accents. The Windows default.</summary>
    Fluent = 1,

    /// <summary>Material Symbols Outlined, weight 400. The Android default.</summary>
    MaterialOutlined = 2,

    /// <summary>Material Symbols Rounded, weight 400.</summary>
    MaterialRounded = 3,

    /// <summary>Framework7 Icons, drawn in the SF Symbols style. The macOS and iOS default.</summary>
    Framework7 = 4,
}
