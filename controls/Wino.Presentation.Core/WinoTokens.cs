namespace Wino.Presentation;

/// <summary>
/// Platform-neutral Wino design tokens: spacing, shape and size decisions plus the avatar palette.
/// Native heads convert them to their own units and colour types (docs/macos-design-decisions.md).
/// </summary>
public static class WinoTokens
{
    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;
    public const double Space6 = 24;
    public const double Space8 = 32;

    public const double ControlRadius = 6;
    public const double GroupRadius = 8;
    public const double PopoverRadius = 10;

    public const double SettingsRowMinHeight = 44;
    public const double SidebarRowHeight = 28;

    /// <summary>Gap between the window edge, the sidebar and the zones (Windows uses 6).</summary>
    public const double ZoneGutter = 6;
    public const double ZoneRadius = 8;

    /// <summary>The 16-step avatar palette from the Wino design system, as #RRGGBB.</summary>
    public static IReadOnlyList<string> AvatarPalette { get; } = new string[]
    {
        "#E74C3C", "#C0392B", "#9B59B6", "#8E44AD",
        "#3498DB", "#2980B9", "#16A085", "#27AE60",
        "#00796B", "#E67E22", "#D35400", "#663399",
        "#3F51B5", "#E91E63", "#7F8C8D", "#795548"
    };

    /// <summary>The stable palette index for an avatar key (case-insensitive); an empty key uses the grey step.</summary>
    public static int AvatarPaletteIndex(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 14;
        int hash = 17;
        foreach (char character in key.ToLowerInvariant()) hash = unchecked(hash * 31 + character);
        return (int)((uint)hash % (uint)AvatarPalette.Count);
    }
}
