using System;
using CommunityToolkit.WinUI;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Controls;

/// <summary>
/// Draws a miniature of the single Wino surface a custom theme color paints, composited over
/// the theme wallpaper exactly as the application composites it. Each <see cref="ThemeSurfaceScene"/>
/// maps to the real usage site of one resource key.
/// </summary>
public sealed partial class ThemeSurfacePreview : UserControl
{
    /// <summary>
    /// The resolved palette of the mode being edited. Assign a new instance to repaint.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial CustomThemePalette? Palette { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AccentColorHex { get; set; }

    [GeneratedDependencyProperty]
    public partial ImageSource? WallpaperSource { get; set; }

    [GeneratedDependencyProperty(DefaultValue = ThemeWallpaperFit.Fill)]
    public partial ThemeWallpaperFit WallpaperFit { get; set; }

    [GeneratedDependencyProperty(DefaultValue = ThemeWallpaperAlignment.Center)]
    public partial ThemeWallpaperAlignment WallpaperAlignment { get; set; }

    [GeneratedDependencyProperty(DefaultValue = ThemeSurfaceScene.Window)]
    public partial ThemeSurfaceScene Scene { get; set; }

    /// <summary>
    /// The color the caller is editing. Scenes shared by several keys, such as the calendar
    /// grid, use it to point at the slot that changes.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = CustomThemeColorKey.BaseSurface)]
    public partial CustomThemeColorKey HighlightedKey { get; set; }

    public ThemeSurfacePreview()
    {
        Palette = CustomThemePalette.CreateDefaults(false);
        InitializeComponent();
    }

    public static bool IsWindowScene(ThemeSurfaceScene scene) => scene == ThemeSurfaceScene.Window;
    public static bool IsNavigationScene(ThemeSurfaceScene scene) => scene == ThemeSurfaceScene.Navigation;
    public static bool IsWorkspaceScene(ThemeSurfaceScene scene) => scene == ThemeSurfaceScene.Workspace;
    public static bool IsMailListHeaderScene(ThemeSurfaceScene scene) => scene == ThemeSurfaceScene.MailListHeader;
    public static bool IsReadingPaneScene(ThemeSurfaceScene scene) => scene == ThemeSurfaceScene.ReadingPane;
    public static bool IsCalendarScene(ThemeSurfaceScene scene) => scene == ThemeSurfaceScene.CalendarGrid;

    public static Stretch GetWallpaperStretch(ThemeWallpaperFit fit)
        => fit == ThemeWallpaperFit.Fit ? Stretch.Uniform : Stretch.UniformToFill;

    public static AlignmentX GetWallpaperAlignmentX(ThemeWallpaperAlignment alignment) => alignment switch
    {
        ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Left or ThemeWallpaperAlignment.BottomLeft => AlignmentX.Left,
        ThemeWallpaperAlignment.TopRight or ThemeWallpaperAlignment.Right or ThemeWallpaperAlignment.BottomRight => AlignmentX.Right,
        _ => AlignmentX.Center
    };

    public static AlignmentY GetWallpaperAlignmentY(ThemeWallpaperAlignment alignment) => alignment switch
    {
        ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Top or ThemeWallpaperAlignment.TopRight => AlignmentY.Top,
        ThemeWallpaperAlignment.BottomLeft or ThemeWallpaperAlignment.Bottom or ThemeWallpaperAlignment.BottomRight => AlignmentY.Bottom,
        _ => AlignmentY.Center
    };

    /// <summary>
    /// Converts a palette value to a brush. Invalid or missing values draw nothing.
    /// </summary>
    public static Brush GetBrush(string? colorHex)
        => new SolidColorBrush(ParseColor(colorHex, Colors.Transparent));

    /// <summary>
    /// Converts a palette value to a color for the native color picker.
    /// </summary>
    public static Color GetColor(string? colorHex)
        => ParseColor(colorHex, Color.FromArgb(0xFF, 0x80, 0x80, 0x80));

    public static Brush GetAccentBrush(string? accentHex)
        => new SolidColorBrush(ParseColor(accentHex, Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)));

    /// <summary>
    /// The readable text color for this palette, chosen the way the application chooses it:
    /// light text on a dark base surface, dark text on a light one.
    /// </summary>
    public static Brush GetInkBrush(CustomThemePalette? palette)
        => new SolidColorBrush(IsDarkBase(palette) ? Color.FromArgb(0xFF, 0xF4, 0xF6, 0xFA) : Color.FromArgb(0xFF, 0x10, 0x12, 0x16));

    public static Brush GetSubtleInkBrush(CustomThemePalette? palette)
        => new SolidColorBrush(IsDarkBase(palette) ? Color.FromArgb(0x99, 0xF4, 0xF6, 0xFA) : Color.FromArgb(0x99, 0x10, 0x12, 0x16));

    public static Brush GetPlaceholderBrush(CustomThemePalette? palette)
        => new SolidColorBrush(IsDarkBase(palette) ? Color.FromArgb(0x4D, 0xF4, 0xF6, 0xFA) : Color.FromArgb(0x4D, 0x10, 0x12, 0x16));

    /// <summary>
    /// The hairline that separates miniature surfaces, so a card edge stays visible in both modes.
    /// </summary>
    public static Brush GetEdgeBrush(CustomThemePalette? palette)
        => new SolidColorBrush(IsDarkBase(palette) ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

    public static int GetCalendarHighlightRow(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.CalendarWorkHour => 1,
        CustomThemeColorKey.CalendarHoverHour => 2,
        CustomThemeColorKey.CalendarSelectedHour => 3,
        _ => 0
    };

    public static int GetCalendarHighlightColumn(CustomThemeColorKey key) => key switch
    {
        CustomThemeColorKey.CalendarWorkHour => 1,
        CustomThemeColorKey.CalendarHoverHour => 2,
        CustomThemeColorKey.CalendarSelectedHour => 1,
        _ => 0
    };

    private static bool IsDarkBase(CustomThemePalette? palette)
    {
        var color = ParseColor(palette?.MainCustomThemeColor, Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;

        return luminance < 0.5;
    }

    private static Color ParseColor(string? colorHex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
            return fallback;

        var hex = colorHex.Trim().TrimStart('#');

        if (hex.Length == 6)
            hex = "FF" + hex;

        if (hex.Length != 8 ||
            !byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var a) ||
            !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return fallback;

        return Color.FromArgb(a, r, g, b);
    }
}
