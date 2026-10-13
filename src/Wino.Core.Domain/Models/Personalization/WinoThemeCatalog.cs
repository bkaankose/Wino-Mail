#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

/// <summary>
/// Background stops of a gradient theme (Windows AppThemes/*.xaml, StartPoint 0,0 to EndPoint 0.32,1),
/// as 0xRRGGBB values.
/// </summary>
public sealed record WinoThemeGradient(uint LightStart, uint LightEnd, uint DarkStart, uint DarkEnd);

/// <summary>
/// One predefined Wino theme. <see cref="AccentColor"/> is empty for the system theme, which follows
/// the platform accent. <see cref="ForcedElementTheme"/> is Default when the theme follows light and dark.
/// </summary>
public sealed record WinoThemeDefinition(string Name, Guid Id, string AccentColor, ApplicationElementTheme ForcedElementTheme, WinoThemeGradient? Gradient = null)
{
    public bool IsSystemTheme => Id == WinoThemeCatalog.DefaultThemeId;

    public ThemeCompatibility Compatibility => WinoThemeCatalog.CompatibilityFor(ForcedElementTheme);
}

/// <summary>
/// The predefined themes and the theme configuration keys shared by every platform, so a theme
/// chosen on one platform resolves to the same theme on another.
/// </summary>
public static class WinoThemeCatalog
{
    public const string SelectedAppThemeKey = nameof(SelectedAppThemeKey);
    public const string AccentColorKey = nameof(AccentColorKey);
    public const string CurrentApplicationThemeKey = nameof(CurrentApplicationThemeKey);

    public static readonly Guid DefaultThemeId = Guid.Empty;

    /// <summary>The system theme first, then the predefined themes in gallery order.</summary>
    public static IReadOnlyList<WinoThemeDefinition> PredefinedThemes { get; } = (WinoThemeDefinition[])
    [
        new("Default", DefaultThemeId, string.Empty, ApplicationElementTheme.Default),
        new("Nighty", Guid.Parse("5b65e04e-fd7e-4c2d-8221-068d3e02d23a"), "#e1b12c", ApplicationElementTheme.Dark),
        new("Forest", Guid.Parse("8bc89b37-a7c5-4049-86e2-de1ae8858dbd"), "#16a085", ApplicationElementTheme.Dark),
        new("Clouds", Guid.Parse("3b621cc2-e270-4a76-8477-737917cccda0"), "#0984e3", ApplicationElementTheme.Light),
        new("Snowflake", Guid.Parse("e143ddde-2e28-4846-9d98-dad63d6505f1"), "#4a69bd", ApplicationElementTheme.Light),
        new("Garden", Guid.Parse("698e4466-f88c-4799-9c61-f0ea1308ed49"), "#05c46b", ApplicationElementTheme.Light),

        new("Dunes", Guid.Parse("fff1d070-e7f2-4a91-8e50-30f20a19bf7d"), "#e17055", ApplicationElementTheme.Light),
        new("Lavender", Guid.Parse("41567cf1-31ca-4f8c-bd18-2f9a2d990cb5"), "#6c5ce7", ApplicationElementTheme.Light),
        new("Coastline", Guid.Parse("17434033-2152-41dc-bda6-933c52d250b4"), "#00a8b5", ApplicationElementTheme.Light),
        new("Blossom", Guid.Parse("02eb4827-50c7-4052-8dde-3a4af843317b"), "#e84393", ApplicationElementTheme.Light),
        new("Poppy", Guid.Parse("004c87bc-e540-406c-8c50-8eb8eb03d247"), "#d63031", ApplicationElementTheme.Light),

        new("Aurora", Guid.Parse("ba2974ca-7e03-4fe1-b102-efc6a6fa9cc9"), "#00cec9", ApplicationElementTheme.Dark),
        new("Ember", Guid.Parse("437d53b5-9228-408d-9374-c5ca457bf02f"), "#e58e26", ApplicationElementTheme.Dark),
        new("Nebula", Guid.Parse("b509ae0e-ca7a-4d53-b052-c3f30bbd4bd5"), "#8c7ae6", ApplicationElementTheme.Dark),
        new("Skyline", Guid.Parse("1ae2d96e-1aa8-4058-ad08-84f11069c69a"), "#0097e6", ApplicationElementTheme.Dark),
        new("Graphite", Guid.Parse("7e633982-ad91-4634-a926-25b154e7bfaf"), "#6b7b8c", ApplicationElementTheme.Dark),

        new("Mist", Guid.Parse("a0347ab8-d51e-411f-ab80-996d3b21a81b"), "#5e81ac", ApplicationElementTheme.Default, new(0xF4F7FA, 0xE1E7EF, 0x191D23, 0x0C0E12)),
        new("Cocoa", Guid.Parse("24b4f855-c9ea-485c-b9a9-0672d1b327dc"), "#c07a4c", ApplicationElementTheme.Default, new(0xFBF7F2, 0xEDE3D8, 0x1D1815, 0x100D0B)),
        new("Moss", Guid.Parse("e75ea27f-3757-4b46-8ad7-f5a4d4c58cdf"), "#2f9e63", ApplicationElementTheme.Default, new(0xF1F6F1, 0xE0EBE1, 0x141A16, 0x0B0F0C)),
        new("Rose", Guid.Parse("ef2a1205-f6a4-4551-bac6-8747879669f7"), "#c4577e", ApplicationElementTheme.Default, new(0xFBF2F5, 0xF2E2E8, 0x1B1418, 0x100B0E)),
        new("Indigo", Guid.Parse("14c784d9-d8d3-462d-9b2c-965967d86d38"), "#4c5fd7", ApplicationElementTheme.Default, new(0xF1F3FB, 0xE1E5F5, 0x12141F, 0x0A0C14)),
    ];

    public static WinoThemeDefinition? Find(Guid id)
    {
        foreach (var theme in PredefinedThemes)
        {
            if (theme.Id == id) return theme;
        }

        return null;
    }

    public static bool IsPredefined(Guid? id) => id is { } value && Find(value) is not null;

    /// <summary>A gradient theme's stops, or null when the theme has a wallpaper or no backdrop.</summary>
    public static WinoThemeGradient? GradientFor(string? themeName)
    {
        if (string.IsNullOrEmpty(themeName)) return null;

        foreach (var theme in PredefinedThemes)
        {
            if (string.Equals(theme.Name, themeName, StringComparison.Ordinal)) return theme.Gradient;
        }

        return null;
    }

    public static ThemeCompatibility CompatibilityFor(ApplicationElementTheme forcedElementTheme) => forcedElementTheme switch
    {
        ApplicationElementTheme.Dark => ThemeCompatibility.Dark,
        ApplicationElementTheme.Light => ThemeCompatibility.Light,
        _ => ThemeCompatibility.Both
    };
}
