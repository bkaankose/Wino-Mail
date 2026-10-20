using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// File and family names of the WinoIcons fonts that icons/tools/build_fonts.py writes (set_font_specs in
/// icons/tools/wino_icons.py). Each set has a monochrome font and a light and a dark colorful font.
/// </summary>
public static class WinoIconFonts
{
    /// <summary>Every set, in the order the settings show them.</summary>
    public static WinoIconSet[] Sets { get; } =
        [WinoIconSet.Fluent, WinoIconSet.MaterialOutlined, WinoIconSet.MaterialRounded, WinoIconSet.Framework7, WinoIconSet.Classic];

    /// <summary>The font file name, such as <c>WinoIconsFluentColor-Dark.ttf</c>.</summary>
    public static string FileName(WinoIconSet set, bool colorful, bool dark)
        => $"WinoIcons{Prefix(set)}{Variant(colorful, dark, "Color-Light", "Color-Dark")}.ttf";

    /// <summary>The font family inside the file, such as <c>WinoIconsFluentColorDark</c>.</summary>
    public static string FamilyName(WinoIconSet set, bool colorful, bool dark)
        => $"WinoIcons{Prefix(set)}{Variant(colorful, dark, "ColorLight", "ColorDark")}";

    /// <summary>The monochrome, light colorful and dark colorful file names of <paramref name="set"/>.</summary>
    public static string[] FileNames(WinoIconSet set)
        => [FileName(set, false, false), FileName(set, true, false), FileName(set, true, true)];

    // Classic keeps the original names (WinoIcons.ttf, WinoIconsColor-Light.ttf...).
    private static string Prefix(WinoIconSet set) => set == WinoIconSet.Classic ? string.Empty : set.ToString();

    private static string Variant(bool colorful, bool dark, string light, string darkVariant)
        => !colorful ? string.Empty : dark ? darkVariant : light;
}
