using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Theme previews for the Personalization and gallery pages: the tile model of a theme and its decoded preview, scaled to
/// twice the tile size and cached for the process by path and write time (a custom theme can rewrite its preview).
/// </summary>
internal static class ThemePreviews
{
    private const int DecodeWidth = WinoThemeTile.PreviewWidth * 2;
    private const int DecodeHeight = WinoThemeTile.PreviewHeight * 2;

    private static readonly Dictionary<string, Gdk.Texture?> Cache = new(StringComparer.Ordinal);

    public static WinoThemeTileModel Model(AppThemeBase theme)
    {
        var gradient = theme.IsCustomTheme ? null : WinoThemeCatalog.GradientFor(theme.ThemeName);
        var dark = WinoAppearance.IsDark;
        // A gradient theme's file is not its preview (macOS and the F-08 backdrop follow the same rule).
        var image = gradient is null && !string.IsNullOrWhiteSpace(theme.PreviewImage) ? theme.PreviewImage : null;
        return new WinoThemeTileModel(theme.Id, theme.ThemeName ?? string.Empty,
            theme.IsCustomTheme ? null : theme.CompatibilityTitle, image,
            gradient is null ? null : dark ? gradient.DarkStart : gradient.LightStart,
            gradient is null ? null : dark ? gradient.DarkEnd : gradient.LightEnd,
            theme.IsCustomTheme);
    }

    public static bool TryGet(string path, out Gdk.Texture? texture) => Cache.TryGetValue(Key(path), out texture);

    /// <summary>Decodes on the main thread at preview size; null when the file is missing or unreadable.</summary>
    public static Gdk.Texture? Load(string path)
    {
        var key = Key(path);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Gdk.Texture? texture = null;
        try
        {
            if (File.Exists(path))
            {
                var pixbuf = GdkPixbuf.Pixbuf.NewFromFileAtScale(path, DecodeWidth, DecodeHeight, true);
                if (pixbuf is not null) texture = Gdk.Texture.NewForPixbuf(pixbuf);
            }
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Theme preview {Path} could not be decoded.", path);
        }
        Cache[key] = texture;
        return texture;
    }

    private static string Key(string path)
    {
        try { return $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}"; }
        catch (Exception) { return path; }
    }
}
