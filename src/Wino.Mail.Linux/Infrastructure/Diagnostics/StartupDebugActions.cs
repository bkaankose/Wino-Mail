#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views;
using Wino.Platform.Linux.Services;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-startup</c> <c>as</c> (PL-03): state (autostart entry path and Exec, the service status, close behaviour,
/// window visible, held in the background), enable, disable, close (the window's close request: hides it when the app
/// runs in the background), background on|off (the close behaviour preference). One log line
/// <c>Debug action debug-startup &lt;verb&gt;: &lt;result&gt;</c>; a last argument ending in .txt also receives it.
/// </summary>
internal static class StartupDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services, Func<MainWindow?> window, Func<bool> held)
    {
        var action = Gio.SimpleAction.New("debug-startup", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, window, held, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, Func<MainWindow?> window, Func<bool> held, string[] arguments)
    {
        var verb = arguments.Length > 0 ? arguments[0] : string.Empty;
        string? path = arguments.Length > 1 && arguments[^1].EndsWith(".txt", StringComparison.Ordinal) ? arguments[^1] : null;
        var rest = arguments.Length > 1 ? arguments[1..(path is null ? arguments.Length : arguments.Length - 1)] : [];
        string result;
        try
        {
            var provider = services() ?? throw new InvalidOperationException("no services");
            var startup = provider.GetRequiredService<IStartupIntegrationService>();
            result = verb switch
            {
                "state" => await StateAsync(provider, startup, window, held),
                "enable" => (await startup.SetEnabledAsync(true)).ToString(),
                "disable" => (await startup.SetEnabledAsync(false)).ToString(),
                "close" => Close(window),
                "background" => Background(provider, rest),
                _ => "verbs: state, enable, disable, close, background on|off",
            };
        }
        catch (Exception exception)
        {
            result = "error: " + exception.Message;
            Serilog.Log.Error(exception, "Debug action debug-startup {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action debug-startup {Verb}: {Result}", verb, result);
        if (path is not null) DebugActions.WriteText(path, result);
    }

    private static async Task<string> StateAsync(IServiceProvider provider, IStartupIntegrationService startup, Func<MainWindow?> window, Func<bool> held)
    {
        var entry = LinuxStartupIntegrationService.AutostartEntryPath;
        var exec = File.Exists(entry) ? File.ReadLines(entry).FirstOrDefault(static line => line.StartsWith("Exec=", StringComparison.Ordinal)) ?? "-" : "-";
        var status = await startup.GetCurrentBehaviorAsync();
        var behavior = provider.GetRequiredService<IPreferencesService>().AppCloseBehavior;
        return $"entry={entry} exists={File.Exists(entry)} {exec} status={status} close={behavior} visible={window()?.Window.GetVisible()} held={held()}";
    }

    private static string Close(Func<MainWindow?> window)
    {
        if (window() is not { } main) return "no window";
        main.Window.Close();
        return $"visible={main.Window.GetVisible()}";
    }

    private static string Background(IServiceProvider provider, string[] rest)
    {
        var preferences = provider.GetRequiredService<IPreferencesService>();
        preferences.AppCloseBehavior = rest.Length > 0 && rest[0] == "off" ? AppCloseBehavior.Terminate : AppCloseBehavior.RunInBackgroundWithoutTrayIcon;
        return preferences.AppCloseBehavior.ToString();
    }
}
#endif
