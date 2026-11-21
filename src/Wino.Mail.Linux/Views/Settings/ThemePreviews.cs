using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Theme previews for the Personalization and gallery pages: the tile model of a theme and its decoded preview, scaled to
/// twice the tile size off the main thread and cached for the process by path and write time (a custom theme can rewrite
/// its preview).
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

    /// <summary>Main thread.</summary>
    public static bool TryGet(string path, out Gdk.Texture? texture) => Cache.TryGetValue(Key(path), out texture);

    /// <summary>
    /// Decodes at preview size on a worker thread and creates the texture on the main thread, where
    /// <paramref name="apply"/> runs unless <paramref name="cancellation"/> was cancelled. Null when the file is missing
    /// or unreadable. Call on the main thread.
    /// </summary>
    public static async Task LoadAsync(string path, IDispatcher dispatcher, Action<Gdk.Texture?> apply, CancellationToken cancellation = default)
    {
        var key = Key(path);
        if (Cache.TryGetValue(key, out var cached))
        {
            apply(cached);
            return;
        }
        var image = await Task.Run(() => File.Exists(path) ? ThemeImaging.DecodeToFit(path, DecodeWidth, DecodeHeight) : null, cancellation).ConfigureAwait(false);
        if (image is null) Serilog.Log.Warning("Theme preview {Path} could not be decoded.", path);
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (!Cache.TryGetValue(key, out var texture))
            {
                texture = image is null ? null : ThemeImaging.ToTexture(image);
                Cache[key] = texture;
            }
            if (!cancellation.IsCancellationRequested) apply(texture);
        });
    }

    private static string Key(string path)
    {
        try { return $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}"; }
        catch (Exception) { return path; }
    }
}
