using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;

namespace Wino.Presentation.Linux.Style;

/// <summary>What the theme stylesheet paints: the accent, a wallpaper or a gradient, and a custom theme's surface colours.</summary>
public sealed record ThemeCssInput(
    WinoRgba? Accent,
    string? WallpaperPath,
    WinoRgba? GradientStart,
    WinoRgba? GradientEnd,
    ThemeWallpaperFit Fit = ThemeWallpaperFit.Fill,
    ThemeWallpaperAlignment Alignment = ThemeWallpaperAlignment.Top,
    Dictionary<WinoThemeSurface, WinoRgba>? Surfaces = null,
    bool IsDark = false);

/// <summary>Builds the theme stylesheet the theme service loads at <see cref="WinoStyle.ThemePriority"/>. No GTK call.</summary>
public static class ThemeCss
{
    public static string Build(ThemeCssInput input)
    {
        var css = new StringBuilder();
        if (input.Accent is { } accent)
        {
            // Both spellings on purpose: libadwaita 1.6+ styles through the variables, and the @define-color names keep
            // rules that use @accent_bg_color and @accent_color in step. The standalone accent repeats libadwaita's own
            // derivation for the applied scheme (the service rebuilds on every scheme change) because distribution builds
            // (Ubuntu's Yaru) pin it to the system accent; a prefers-color-scheme block here did not flip in GTK 4.22.
            // The perceived-brightness rule gives #e1b12c black text where the WCAG 0.5 rule gives white.
            var hex = Hex(accent);
            var foreground = ReadableTextColor.PrefersDarkText(accent.R / 255.0, accent.G / 255.0, accent.B / 255.0) ? "#000000" : "#ffffff";
            var standalone = input.IsDark ? "max(l, 0.85) a b" : "min(l, 0.5) a b";
            css.Append(":root { --accent-bg-color: ").Append(hex).Append("; --accent-fg-color: ").Append(foreground)
                .Append("; --accent-color: oklab(from ").Append(hex).Append(' ').Append(standalone).AppendLine("); }");
            css.Append("@define-color accent_bg_color ").Append(hex).AppendLine(";");
            css.Append("@define-color accent_fg_color ").Append(foreground).AppendLine(";");
            css.Append("@define-color accent_color oklab(from ").Append(hex).Append(' ').Append(standalone).AppendLine(");");
        }

        if (input.GradientStart is { } start && input.GradientEnd is { } end)
        {
            // 162 degrees is the Windows brush from (0,0) to (0.32,1).
            css.Append('.').Append(WinoCssClass.ThemeBackdrop).Append(" { background-image: linear-gradient(162deg, ")
                .Append(Hex(start)).Append(", ").Append(Hex(end)).AppendLine("); }");
        }
        else if (WallpaperUri(input.WallpaperPath) is { } uri)
        {
            var fit = input.Fit == ThemeWallpaperFit.Fit;
            css.Append('.').Append(WinoCssClass.ThemeBackdrop).Append(" { background-image: url(\"").Append(uri)
                .Append("\"); background-size: ").Append(fit ? "contain" : "cover")
                .Append("; background-position: ").Append(fit ? "center" : Position(input.Alignment))
                .AppendLine("; background-repeat: no-repeat; }");
        }

        if (input.Surfaces is { } surfaces)
        {
            foreach (var (surface, color) in surfaces)
            {
                css.Append('.').Append(WinoZone.SurfaceClass(surface)).Append(" { background-color: ").Append(Rgba(color)).AppendLine("; }");
            }
        }

        return css.ToString();
    }

    /// <summary>Whether <see cref="Build"/> paints the backdrop for <paramref name="input"/>.</summary>
    public static bool HasBackdrop(ThemeCssInput input)
        => (input.GradientStart is not null && input.GradientEnd is not null) || WallpaperUri(input.WallpaperPath) is not null;

    public static string Hex(WinoRgba color)
        => string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}");

    /// <summary>The file URI of an absolute path, or null when it is missing or would break out of the CSS string.</summary>
    public static string? WallpaperUri(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return null;
        var uri = new Uri(path).AbsoluteUri;
        return uri.IndexOfAny(['"', '\\', '\n', '\r']) >= 0 ? null : uri;
    }

    public static string Rgba(WinoRgba color)
        => string.Create(CultureInfo.InvariantCulture, $"rgba({color.R}, {color.G}, {color.B}, {Math.Clamp(color.Alpha, 0, 1):0.###})");

    public static string Position(ThemeWallpaperAlignment alignment) => alignment switch
    {
        ThemeWallpaperAlignment.TopLeft => "left top",
        ThemeWallpaperAlignment.Top => "center top",
        ThemeWallpaperAlignment.TopRight => "right top",
        ThemeWallpaperAlignment.Left => "left center",
        ThemeWallpaperAlignment.Right => "right center",
        ThemeWallpaperAlignment.BottomLeft => "left bottom",
        ThemeWallpaperAlignment.Bottom => "center bottom",
        ThemeWallpaperAlignment.BottomRight => "right bottom",
        _ => "center"
    };
}
