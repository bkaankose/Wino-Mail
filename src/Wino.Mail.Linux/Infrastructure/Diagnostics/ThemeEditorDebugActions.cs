#if DEBUG
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Linux.Views.Settings;
using Wino.Services.Themes;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-theme-editor</c> (<c>as</c>) drives the custom theme editor: <c>['new']</c>, <c>['edit', name]</c> (opens Settings
/// on the gallery and sends the gallery's navigation), <c>['name', text]</c>, <c>['mode', 'light'|'dark']</c>,
/// <c>['color', CustomThemeColorKey, hex]</c>, <c>['accent', hex|'system']</c>, <c>['wallpaper', path]</c> (the picker
/// bypass), <c>['fit', 'fill'|'fit']</c>, <c>['focal', ThemeWallpaperAlignment]</c>, <c>['expand', key]</c>,
/// <c>['save']</c>, <c>['cancel']</c>, <c>['delete']</c>, <c>['state', path]</c>.
/// </summary>
internal static class ThemeEditorDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-theme-editor", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var themes = provider.GetRequiredService<INewThemeService>();
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        string Arg(int index) => parts.Length > index ? parts[index] : throw new ArgumentException($"debug-theme-editor {op} needs argument {index}.");

        switch (op)
        {
            case "new":
            case "edit":
            {
                Guid? id = null;
                if (op == "edit")
                {
                    var name = Arg(1);
                    id = (await themes.GetCurrentCustomThemesAsync()).FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase))?.Id
                        ?? throw new InvalidOperationException($"No custom theme '{name}'.");
                }
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.ApplicationThemeGalleryPage, null);
                await Task.Delay(300);
                provider.GetRequiredService<IMessenger>().Send(new BreadcrumbNavigationRequested(
                    id.HasValue ? Translator.ApplicationThemeEditor_EditTitle : Translator.ApplicationThemeEditor_CreateTitle,
                    WinoPage.ApplicationThemeEditorPage, new CustomThemeEditorNavigationParameter(id)));
                return;
            }
            case "state":
            {
                var path = Arg(1);
                var text = new StringBuilder();
                var page = ApplicationThemeEditorPage.DebugCurrent;
                text.AppendLine(page?.DebugState() ?? "editor=closed");
                text.Append("IsCustomTheme ").Append(themes.IsCustomTheme).Append(" CurrentId ").Append(themes.CurrentApplicationThemeId).AppendLine();
                var store = provider.GetRequiredService<CustomThemeFileStore>();
                foreach (var theme in await themes.GetCurrentCustomThemesAsync())
                    text.Append("custom ").Append(theme.Id).Append(" | ").Append(theme.Name).Append(" | accent ").Append(theme.AccentColorHex)
                        .Append(" | ").Append(theme.WallpaperFit).Append(' ').Append(theme.WallpaperAlignment)
                        .Append(" | files ").Append(string.Join(',', Directory.Exists(store.RootPath)
                            ? Directory.GetFiles(store.RootPath, theme.Id + "*").Select(Path.GetFileName) : [])).AppendLine();
                if (themes is GtkThemeService gtk) text.AppendLine("css:").AppendLine(gtk.LastCss);
                await File.WriteAllTextAsync(path + ".partial", text.ToString());
                File.Move(path + ".partial", path, true);
                Serilog.Log.Information("Debug action: theme editor state written to {Path}.", path);
                return;
            }
        }

        var editor = ApplicationThemeEditorPage.DebugCurrent ?? throw new InvalidOperationException("The theme editor is not open.");
        switch (op)
        {
            case "wallpaper":
            {
                var path = Arg(1);
                var bytes = await File.ReadAllBytesAsync(path);
                await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => editor.ViewModel.ApplyWallpaper(bytes, path, Path.GetFileName(path)));
                break;
            }
            default:
                await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => editor.DebugRun(op, parts.Skip(1).ToArray()));
                break;
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-theme-editor failed."); }
    }
}
#endif
