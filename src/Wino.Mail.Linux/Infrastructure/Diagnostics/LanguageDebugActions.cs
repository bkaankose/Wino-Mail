#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-language</c> <c>as</c>: <c>['set', AppLanguage]</c> changes the app language as the Settings row does;
/// <c>['state', path]</c> writes the language, the cultures, the text directions and the main window's widget dump.
/// </summary>
internal static class LanguageDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-language", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length < 2) throw new ArgumentException("An operation and its argument are required.");
                var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
                if (arguments[0] == "set")
                {
                    if (!Enum.TryParse<AppLanguage>(arguments[1], true, out var language) || language == AppLanguage.None) throw new ArgumentException($"Unknown language '{arguments[1]}'.");
                    _ = SetAsync(provider.GetRequiredService<ITranslationService>(), language);
                    return;
                }
                if (arguments[0] != "state") throw new ArgumentException($"Unknown operation '{arguments[0]}'.");

                var window = provider.GetRequiredService<GtkNavigationService>().MainWindow?.Window;
                var text = new StringBuilder()
                    .Append("language=").AppendLine(provider.GetRequiredService<ITranslationService>().CurrentLanguageModel?.Language.ToString() ?? "-")
                    .Append("prepared=").AppendLine(LinuxLanguage.Prepared.ToString())
                    .Append("uiCulture=").AppendLine(CultureInfo.CurrentUICulture.Name)
                    .Append("defaultDirection=").AppendLine(Gtk.Widget.GetDefaultDirection().ToString())
                    .Append("windowDirection=").AppendLine(window?.GetDirection().ToString() ?? "-")
                    .Append("sample=").AppendLine(string.Join(" | ", Translator.Buttons_Back, Translator.MenuSettings, Translator.KeyboardShortcuts_ModeMail, Translator.SearchBarPlaceholder, Translator.Buttons_Sync));
                if (window is not null) text.Append(DebugActions.Describe(window));
                DebugActions.WriteText(arguments[1], text.ToString());
                Serilog.Log.Information("Debug action: language state written to {Path}.", arguments[1]);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-language failed."); }
        };
        application.AddAction(action);
    }

    private static async Task SetAsync(ITranslationService translation, AppLanguage language)
    {
        try
        {
            await translation.InitializeLanguageAsync(language);
            Serilog.Log.Information("Debug action: language set to {Language}.", language);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-language set failed."); }
    }
}
#endif
