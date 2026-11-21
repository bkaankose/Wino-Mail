#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the theme service, all through <see cref="INewThemeService"/>, each with a string parameter:
/// <c>debug-theme-select</c> (a theme name from <see cref="WinoThemeCatalog"/>), <c>debug-accent</c> (<c>#rrggbb</c>, or
/// <c>system</c>), <c>debug-root-theme</c> (<c>Default</c>, <c>Light</c>, <c>Dark</c>), <c>debug-icon-style-preference</c>
/// (<c>Monochrome</c>, <c>Colorful</c>: the shared preference the service follows) and <c>debug-theme-state</c> (an output
/// path for the applied state and the theme CSS).
/// </summary>
internal static class ThemeDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, services, "debug-theme-select", (themes, name) =>
        {
            var definition = WinoThemeCatalog.PredefinedThemes.FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{name}' is not a Wino theme.");
            return themes.SelectThemeAsync(definition.Id);
        });
        Add(application, services, "debug-accent", (themes, value)
            => themes.SetAccentColorAsync(string.Equals(value, "system", StringComparison.OrdinalIgnoreCase) ? string.Empty : value));
        Add(application, services, "debug-root-theme", (themes, value) =>
        {
            if (!Enum.TryParse<ApplicationElementTheme>(value, true, out var root)) throw new ArgumentException($"'{value}' is not Default, Light or Dark.");
            themes.RootTheme = root;
            return Task.CompletedTask;
        });
        Add(application, services, "debug-icon-style-preference", (themes, value) =>
        {
            if (!Enum.TryParse<WinoIconStyle>(value, true, out var style)) throw new ArgumentException($"'{value}' is not Monochrome or Colorful.");
            if (services()?.GetService<IPreferencesService>() is not { } preferences) throw new InvalidOperationException("The app has not started.");
            preferences.IconStyle = style;
            return Task.CompletedTask;
        });
        Add(application, services, "debug-theme-state", (themes, path) =>
        {
            var styleManager = Adw.StyleManager.GetDefault();
            var id = themes.CurrentApplicationThemeId ?? WinoThemeCatalog.DefaultThemeId;
            var text = new StringBuilder()
                .Append("ThemeId ").Append(id).AppendLine()
                .Append("ThemeName ").Append(WinoThemeCatalog.Find(id)?.Name ?? "(custom or unknown)").AppendLine()
                .Append("RootTheme ").Append(themes.RootTheme).AppendLine()
                .Append("AccentColor ").Append(themes.AccentColor).AppendLine()
                .Append("SystemAccent ").Append(themes.GetSystemAccentColorHex()).AppendLine()
                .Append("HasBackdrop ").Append(WinoAppearance.HasBackdrop).AppendLine()
                .Append("IsDark ").Append(WinoAppearance.IsDark).AppendLine()
                .Append("Accent ").Append(WinoAppearance.Accent is { } accent ? ThemeCss.Hex(accent) : "(system)").AppendLine()
                .Append("IconStyle ").Append(WinoAppearance.IconStyle).AppendLine()
                .Append("WinoIcons.Style ").Append(WinoIcons.Style).AppendLine()
                .Append("StyleManager.Dark ").Append(styleManager.Dark).AppendLine()
                .Append("StyleManager.ColorScheme ").Append(styleManager.ColorScheme).AppendLine()
                .AppendLine("Css:")
                .Append(themes is GtkThemeService gtk ? gtk.LastCss : string.Empty);
            var temporary = path + ".partial";
            File.WriteAllText(temporary, text.ToString());
            File.Move(temporary, path, overwrite: true);
            return Task.CompletedTask;
        });
    }

    private static void Add(Adw.Application application, Func<IServiceProvider?> services, string name, Func<INewThemeService, string, Task> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) =>
        {
            var value = args.Parameter?.GetString(out var _) ?? string.Empty;
            if (services()?.GetService<INewThemeService>() is not { } themes)
            {
                Serilog.Log.Error("Debug action {Name} failed: the app has not started.", name);
                return;
            }
            _ = RunAsync(name, value, themes, run);
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(string name, string value, INewThemeService themes, Func<INewThemeService, string, Task> run)
    {
        try
        {
            await run(themes, value);
            Serilog.Log.Information("Debug action {Name} {Value} done.", name, value);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }
}
#endif
