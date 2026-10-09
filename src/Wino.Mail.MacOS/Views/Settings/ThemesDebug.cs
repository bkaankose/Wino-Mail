#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands for themes:
/// <c>themes-list</c> lists every theme (id, type, name, * for the current one);
/// <c>theme-select ID|NAME</c> selects a theme;
/// <c>theme-create-sample IMAGEPATH</c> saves a custom theme from an image with the Slate base preset, selects it and prints its id;
/// <c>theme-delete ID</c> deletes a custom theme;
/// <c>theme-state</c> prints the backdrop, fit, focal point, surface overrides and appearance.
/// The gallery and editor open with the existing <c>settings ApplicationThemeGalleryPage</c>.
/// </summary>
internal static class ThemesDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    private static MacWinoThemeService Themes => Services.GetRequiredService<MacWinoThemeService>();

    public static void Register()
    {
        MacDebugBridge.Register("themes-list", async _ =>
        {
            var current = Themes.CurrentApplicationThemeId;
            var themes = await Themes.GetAvailableThemesAsync();
            return string.Join("\n", themes.Select(theme => $"{(theme.Id == current ? "*" : " ")} {theme.Id} {theme.AppThemeType} {theme.ThemeName}"));
        });

        MacDebugBridge.Register("theme-select", async args =>
        {
            var key = string.Join(' ', args);
            var themes = await Themes.GetAvailableThemesAsync();
            var theme = themes.FirstOrDefault(item => item.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase) || string.Equals(item.ThemeName, key, StringComparison.OrdinalIgnoreCase));
            if (theme is null) return "no theme " + key;
            await Themes.SelectThemeAsync(theme.Id);
            return "ok " + theme.Id;
        });

        MacDebugBridge.Register("theme-create-sample", async args =>
        {
            var path = string.Join(' ', args);
            if (!File.Exists(path)) return "no file " + path;
            var preset = ThemeBasePresets.Create()[0];
            var light = new CustomThemePalette();
            light.SetOverride(CustomThemeColorKey.BaseSurface, preset.LightColor);
            var dark = new CustomThemePalette();
            dark.SetOverride(CustomThemeColorKey.BaseSurface, preset.DarkColor);
            var request = new CustomThemeSaveRequest(null, $"Sample {DateTime.Now:HHmmss}", "#4C5FD7", await File.ReadAllBytesAsync(path),
                light, dark, ThemeWallpaperFit.Fill, ThemeWallpaperAlignment.Center);
            var saved = await Themes.SaveCustomThemeAsync(request);
            await Themes.SelectThemeAsync(saved.Id, forceReapply: true);
            return "ok " + saved.Id;
        });

        MacDebugBridge.Register("theme-delete", async args =>
        {
            if (args.Length == 0 || !Guid.TryParse(args[0], out var id)) return "usage: theme-delete ID";
            return await Themes.DeleteCustomThemeAsync(id) ? "ok" : "not found";
        });

        MacDebugBridge.Register("theme-state", async _ =>
        {
            string result = string.Empty;
            await Services.GetRequiredService<Wino.Core.Domain.Interfaces.IDispatcher>().ExecuteOnUIThread(() =>
            {
                var surfaces = string.Join(",", Enum.GetValues<WinoThemeSurface>().Where(surface => WinoThemeSurfaces.Resolve(surface, false) is not null));
                result = $"id={Themes.CurrentApplicationThemeId} custom={Themes.IsCustomTheme} accent='{Themes.AccentColor}' root={Themes.RootTheme} " +
                         $"image={MacWinoThemeService.BackdropImage?.Size} gradient={MacWinoThemeService.BackdropGradient is not null} " +
                         $"fit={MacWinoThemeService.BackdropFit} focal={MacWinoThemeService.BackdropAlignment} surfaces=[{surfaces}] " +
                         $"appearance={AppKit.NSApplication.SharedApplication.Appearance?.Name ?? "system"}";
            });
            return result;
        });
    }
}
#endif
