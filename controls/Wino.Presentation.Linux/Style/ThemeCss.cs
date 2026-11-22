using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;

namespace Wino.Presentation.Linux.Style;

/// <summary>
/// What the theme stylesheet paints: the accent, a wallpaper or a gradient, a custom theme's surface colours, a desktop
/// palette that replaces libadwaita's named colours, and the flat tiling-desktop look.
/// </summary>
public sealed record ThemeCssInput(
    WinoRgba? Accent,
    string? WallpaperPath,
    WinoRgba? GradientStart,
    WinoRgba? GradientEnd,
    ThemeWallpaperFit Fit = ThemeWallpaperFit.Fill,
    ThemeWallpaperAlignment Alignment = ThemeWallpaperAlignment.Top,
    Dictionary<WinoThemeSurface, WinoRgba>? Surfaces = null,
    bool IsDark = false,
    ThemePalette? Palette = null,
    bool Tiling = false);

/// <summary>
/// Builds the theme stylesheet the theme service loads at <see cref="WinoStyle.ThemePriority"/>. No GTK call. It is the
/// one place that redefines libadwaita's named colours.
/// </summary>
public static class ThemeCss
{
    public static string Build(ThemeCssInput input)
    {
        var css = new StringBuilder();
        if (input.Palette is { } palette) AppendPalette(css, palette, input.IsDark);
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

        if (input.Tiling) AppendTiling(css);
        return css.ToString();
    }

    private static void AppendPalette(StringBuilder css, ThemePalette palette, bool isDark)
    {
        var foreground = Hex(palette.WindowFg);
        var variables = new StringBuilder(":root {");
        var names = new StringBuilder();
        void Color(string name, string value)
        {
            variables.Append(" --").Append(name.Replace('_', '-')).Append(": ").Append(value).Append(';');
            names.Append("@define-color ").Append(name).Append(' ').Append(value).AppendLine(";");
        }
        void Surface(string prefix, WinoRgba background, bool backdrop = false)
        {
            Color(prefix + "_bg_color", Hex(background));
            Color(prefix + "_fg_color", foreground);
            if (backdrop) Color(prefix + "_backdrop_color", Hex(background));
        }
        void Role(string prefix, WinoRgba color)
        {
            var hex = Hex(color);
            Color(prefix + "_bg_color", hex);
            Color(prefix + "_fg_color", ReadableForeground(color));
            Color(prefix + "_color", "oklab(from " + hex + " " + Standalone(isDark) + ")");
        }

        Surface("window", palette.Window);
        Surface("view", palette.View);
        Surface("headerbar", palette.Headerbar, backdrop: true);
        Surface("sidebar", palette.Sidebar, backdrop: true);
        Surface("secondary_sidebar", palette.Sidebar, backdrop: true);
        Surface("card", palette.Card);
        Surface("popover", palette.Popover);
        Surface("dialog", palette.Dialog);
        if (palette.Destructive is { } destructive)
        {
            Role("destructive", destructive);
            Role("error", destructive);
        }
        if (palette.Success is { } success) Role("success", success);
        if (palette.Warning is { } warning) Role("warning", warning);
        css.Append(variables).AppendLine(" }");
        css.Append(names);
    }

    // Flat look for tiling desktops: the compositor draws the gaps and borders, so windows lose their rounded corners
    // and shadows, and cards and popovers get thin outlines instead of shadows. Pills (999px) stay round on purpose.
    private static void AppendTiling(StringBuilder css)
    {
        css.AppendLine(":root { --window-radius: 0; --wino-control-radius: 2px; --wino-group-radius: 2px; --wino-popover-radius: 2px; --wino-zone-radius: 0; }");
        css.AppendLine("window.csd, window.csd.maximized, window.csd.tiled { box-shadow: none; border-radius: 0; outline: none; }");
        css.AppendLine("window.csd > .titlebar, window.csd headerbar { border-radius: 0; }");
        css.AppendLine(".card, list.boxed-list, .boxed-list-separate > row { box-shadow: none; border: 1px solid alpha(currentColor, 0.12); border-radius: 4px; }");
        css.AppendLine("popover > contents { border-radius: 4px; box-shadow: 0 0 0 1px alpha(currentColor, 0.15); }");
        css.AppendLine("floating-sheet > sheet, dialog.floating > sheet, .dialog-sheet { border-radius: 4px; box-shadow: 0 0 0 1px alpha(currentColor, 0.15); }");
        css.AppendLine("button:not(.pill):not(.circular), entry, spinbutton, dropdown > button, combobox > box > button { border-radius: 4px; }");
    }

    private static string Standalone(bool isDark) => isDark ? "max(l, 0.85) a b" : "min(l, 0.5) a b";

    private static string ReadableForeground(WinoRgba color)
        => ReadableTextColor.PrefersDarkText(color.R / 255.0, color.G / 255.0, color.B / 255.0) ? "#000000" : "#ffffff";

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
