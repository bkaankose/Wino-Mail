#if DEBUG
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Debug-only bridge for remote development without screen access (the Mac MacDebugBridge). It polls
/// {app tmp}/wino-debug/cmd; each line is one command, results go to out.log and snapshots to NAME.png in the same
/// folder. In the Simulator the folder is reachable from the Mac through
/// <c>xcrun simctl get_app_container booted com.winomail.macos data</c> + <c>/tmp/wino-debug</c>.
///
/// Commands: snap NAME · mode Mail|Calendar|Contacts|Tasks · page WinoPage · settings · back ·
/// theme NAME|Default · appearance light|dark|system · iconstyle mono|color · iconset WinoIconSet · sleep MS
/// Feature code can add commands with <see cref="Register"/>.
/// </summary>
public static class UIKitDebugBridge
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "wino-debug");
    private static readonly Dictionary<string, Func<string[], Task<string>>> Commands = new(StringComparer.OrdinalIgnoreCase);
    private static IServiceProvider? _services;
    private static NSTimer? _timer;
    private static bool _busy;

    /// <summary>The folder the bridge reads commands from and writes results and snapshots to.</summary>
    public static string CommandFolder => Folder;

    public static void Register(string name, Func<string[], Task<string>> handler) => Commands[name] = handler;

    public static void Start(IServiceProvider services)
    {
        if (_timer is not null) return;
        _services = services;
        Directory.CreateDirectory(Folder);
        RegisterBuiltIns(services);
        _timer = NSTimer.CreateRepeatingScheduledTimer(0.5, _ => Poll());
        Serilog.Log.Information("UIKit debug bridge listening in {Folder}", Folder);
    }

    private static void Poll() => _ = PollAsync();

    private static async Task PollAsync()
    {
        var path = Path.Combine(Folder, "cmd");
        if (_busy || !File.Exists(path)) return;
        _busy = true;
        try
        {
            var lines = await File.ReadAllLinesAsync(path);
            File.Delete(path);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string result;
                try { result = Commands.TryGetValue(parts[0], out var handler) ? await handler(parts[1..]) : "unknown command"; }
                catch (Exception exception) { result = "error: " + exception; }
                await File.AppendAllTextAsync(Path.Combine(Folder, "out.log"), $"{DateTime.Now:HH:mm:ss} {line} -> {result}\n");
            }
            await File.AppendAllTextAsync(Path.Combine(Folder, "out.log"), "DONE\n");
        }
        catch (Exception exception) { Serilog.Log.Warning(exception, "The debug bridge could not run its commands."); }
        finally { _busy = false; }
    }

    private static void RegisterBuiltIns(IServiceProvider services)
    {
        Register("sleep", async args => { await Task.Delay(args.Length > 0 && int.TryParse(args[0], out var ms) ? ms : 500); return "ok"; });
        Register("snap", args => Task.FromResult(Snap(args.Length > 0 ? args[0] : "snap")));
        Register("mode", args => Task.FromResult(
            args.Length > 0 && Enum.TryParse<WinoApplicationMode>(args[0], true, out var mode)
                ? services.GetRequiredService<UIKitNavigationService>().ChangeApplicationMode(mode) ? "ok" : "refused"
                : "usage: mode Mail|Calendar|Contacts|Tasks"));
        Register("settings", _ => Task.FromResult(services.GetRequiredService<UIKitNavigationService>().ChangeApplicationMode(WinoApplicationMode.Settings) ? "ok" : "refused"));
        Register("page", args => Task.FromResult(
            args.Length > 0 && Enum.TryParse<WinoPage>(args[0], true, out var page)
                ? services.GetRequiredService<UIKitNavigationService>().Navigate(page) ? "ok" : "no route"
                : "usage: page WinoPage"));
        Register("back", async _ => await services.GetRequiredService<UIKitNavigationService>().GoBackAsync() ? "ok" : "nothing to go back to");
        Register("theme", async args =>
        {
            var name = string.Join(' ', args);
            var themes = services.GetRequiredService<INewThemeService>();
            var theme = (await themes.GetAvailableThemesAsync()).FirstOrDefault(item => string.Equals(item.ThemeName, name, StringComparison.OrdinalIgnoreCase));
            if (theme is null) return "no theme " + name;
            await themes.SelectThemeAsync(theme.Id);
            return "ok " + theme.ThemeName;
        });
        Register("appearance", args =>
        {
            var themes = services.GetRequiredService<INewThemeService>();
            themes.RootTheme = (args.Length > 0 ? args[0].ToLowerInvariant() : "system") switch
            {
                "light" => ApplicationElementTheme.Light,
                "dark" => ApplicationElementTheme.Dark,
                _ => ApplicationElementTheme.Default
            };
            return Task.FromResult("ok " + themes.RootTheme);
        });
        Register("iconstyle", args =>
        {
            var preferences = services.GetRequiredService<IPreferencesService>();
            preferences.IconStyle = args.Length > 0 && args[0].StartsWith("mono", StringComparison.OrdinalIgnoreCase) ? WinoIconStyle.Monochrome : WinoIconStyle.Colorful;
            return Task.FromResult("ok " + preferences.IconStyle);
        });
        Register("iconset", args =>
        {
            var preferences = services.GetRequiredService<IPreferencesService>();
            if (args.Length > 0 && Enum.TryParse<WinoIconSet>(args[0], ignoreCase: true, out var set)) preferences.IconSet = set;
            return Task.FromResult("ok " + preferences.IconSet);
        });
    }

    /// <summary>Renders the key window (with its presented sheets) into NAME.png.</summary>
    private static string Snap(string name)
    {
        var window = _services?.GetService<IUIKitShellHost>()?.Window;
        if (window is null) return "no window";
        var bounds = window.Bounds;
        var format = new UIGraphicsImageRendererFormat { Scale = window.Screen.Scale };
        using var renderer = new UIGraphicsImageRenderer(bounds.Size, format);
        using var image = renderer.CreateImage(_ => window.DrawViewHierarchy(bounds, true));
        using var png = image.AsPNG();
        if (png is null) return "no image";
        var path = Path.Combine(Folder, Path.GetFileName(name) + ".png");
        if (!png.Save(NSUrl.FromFilename(path), true)) return "could not write " + path;
        return $"ok {path} {bounds.Width}x{bounds.Height}";
    }
}
#endif
