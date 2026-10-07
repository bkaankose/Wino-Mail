#if DEBUG
using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Debug-only bridge for remote development without screen access. It polls
/// {container tmp}/wino-debug/cmd; each line is one command, results go to out.log and
/// snapshots to *.png in the same folder. Snapshots render the app's own windows, so they need
/// no Screen Recording permission (behind-window vibrancy renders as its fallback colour).
///
/// Commands: snap NAME · mode Mail|Calendar|Contacts|Tasks · settings [WinoPage] ·
/// page WinoPage · theme NAME|Default · appearance light|dark|system · iconstyle mono|color ·
/// size WIDTH HEIGHT · sleep MS · front · windows
/// Feature code can add commands with <see cref="Register"/>.
/// </summary>
public static class MacDebugBridge
{
    // The app is sandboxed, so this resolves inside ~/Library/Containers/com.winomail.macos/Data/tmp.
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "wino-debug");
    private static readonly Dictionary<string, Func<string[], Task<string>>> Commands = new(StringComparer.OrdinalIgnoreCase);
    private static IServiceProvider? _services;
    private static NSTimer? _timer;
    private static bool _busy;

    public static void Register(string name, Func<string[], Task<string>> handler) => Commands[name] = handler;

    public static void Start(IServiceProvider services)
    {
        _services = services;
        Directory.CreateDirectory(Folder);
        RegisterBuiltIns();
        _timer = NSTimer.CreateRepeatingScheduledTimer(0.5, timer => { _ = PollAsync(); });
    }

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
                try
                {
                    result = Commands.TryGetValue(parts[0], out var handler) ? await handler(parts[1..]) : "unknown command";
                }
                catch (Exception exception)
                {
                    result = "error: " + exception;
                }
                await File.AppendAllTextAsync(Path.Combine(Folder, "out.log"), $"{DateTime.Now:HH:mm:ss} {line} -> {result}\n");
            }
            await File.AppendAllTextAsync(Path.Combine(Folder, "out.log"), "DONE\n");
        }
        finally { _busy = false; }
    }

    private static T Get<T>() where T : notnull => _services!.GetRequiredService<T>();

    private static void RegisterBuiltIns()
    {
        Register("sleep", async args => { await Task.Delay(args.Length > 0 ? int.Parse(args[0]) : 500); return "ok"; });
        Register("snap", args => Task.FromResult(Snap(args.Length > 0 ? args[0] : "snap")));
        Register("mode", args =>
        {
            var mode = Enum.Parse<WinoApplicationMode>(args[0], true);
            return Task.FromResult(Get<AppKitNavigationService>().ChangeApplicationMode(mode) ? "ok" : "refused");
        });
        Register("settings", args =>
        {
            var navigation = Get<AppKitNavigationService>();
            if (args.Length == 0) return Task.FromResult(navigation.ChangeApplicationMode(WinoApplicationMode.Settings) ? "ok" : "refused");
            return Task.FromResult(navigation.Navigate(Enum.Parse<WinoPage>(args[0], true)) ? "ok" : "refused");
        });
        Register("page", args => Task.FromResult(Get<AppKitNavigationService>().Navigate(Enum.Parse<WinoPage>(args[0], true)) ? "ok" : "refused"));
        Register("theme", async args =>
        {
            var themes = Get<INewThemeService>();
            var all = await themes.GetAvailableThemesAsync();
            var theme = all.FirstOrDefault(item => string.Equals(item.ThemeName, args[0], StringComparison.OrdinalIgnoreCase));
            if (theme is null) return "no theme " + args[0];
            await themes.SelectThemeAsync(theme.Id, true);
            return "ok";
        });
        Register("appearance", args =>
        {
            Get<INewThemeService>().RootTheme = args[0].ToLowerInvariant() switch
            {
                "light" => ApplicationElementTheme.Light,
                "dark" => ApplicationElementTheme.Dark,
                _ => ApplicationElementTheme.Default
            };
            return Task.FromResult("ok");
        });
        Register("iconstyle", args =>
        {
            Get<IPreferencesService>().IconStyle = args[0].StartsWith("c", StringComparison.OrdinalIgnoreCase) ? WinoIconStyle.Colorful : WinoIconStyle.Monochrome;
            return Task.FromResult("ok");
        });
        Register("size", args =>
        {
            var window = NSApplication.SharedApplication.MainWindow ?? AllWindows().FirstOrDefault(item => item.IsVisible);
            if (window is null) return Task.FromResult("no window");
            var frame = window.Frame;
            window.SetFrame(new CoreGraphics.CGRect(frame.X, frame.Y, (nfloat)double.Parse(args[0]), (nfloat)double.Parse(args[1])), true);
            return Task.FromResult("ok");
        });
        Register("front", _ =>
        {
            // Lets a full-screen capture on the Mac show Wino without granting System Events access.
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
            foreach (var window in AllWindows().Where(item => item.IsVisible)) window.OrderFrontRegardless();
            return Task.FromResult("ok");
        });
        // "tree CLASS [DEPTH]" writes the view tree of the first visible window of that class to tree.txt.
        Register("tree", args =>
        {
            var window = AllWindows().FirstOrDefault(item => item.IsVisible && item.GetType().Name == args[0]);
            if (window?.ContentView is null) return Task.FromResult("no window");
            int maxDepth = args.Length > 1 ? int.Parse(args[1]) : 12;
            var builder = new System.Text.StringBuilder();
            void Walk(NSView view, int depth)
            {
                builder.Append(' ', depth * 2).Append(view.GetType().Name).Append(' ').Append(view.Frame.ToString())
                    .Append(view.Hidden ? " hidden" : string.Empty).AppendLine();
                if (depth < maxDepth) foreach (var child in view.Subviews) Walk(child, depth + 1);
            }
            Walk(window.ContentView, 0);
            File.WriteAllText(Path.Combine(Folder, "tree.txt"), builder.ToString());
            return Task.FromResult("tree.txt");
        });
        // "lsnap NAME" renders each window's layer tree instead of its drawing, for views that
        // cacheDisplay leaves blank (layer-backed scroll content).
        Register("lsnap", args =>
        {
            var saved = new List<string>();
            int index = 0;
            foreach (var window in AllWindows().Where(item => item.IsVisible && item.ContentView is not null))
            {
                var view = window.ContentView!.Superview ?? window.ContentView!;
                view.LayoutSubtreeIfNeeded();
                view.DisplayIfNeeded();
                if (view.Layer is null) continue;
                nfloat scale = window.BackingScaleFactor;
                int width = (int)(view.Bounds.Width * scale), height = (int)(view.Bounds.Height * scale);
                using var space = CoreGraphics.CGColorSpace.CreateSrgb();
                using var context = new CoreGraphics.CGBitmapContext(null, width, height, 8, width * 4, space, CoreGraphics.CGImageAlphaInfo.PremultipliedFirst);
                context.ScaleCTM(scale, scale);
                if (view.IsFlipped) { context.TranslateCTM(0, view.Bounds.Height); context.ScaleCTM(1, -1); }
                view.Layer.RenderInContext(context);
                using var image = context.ToImage();
                if (image is null) continue;
                var rep = new NSBitmapImageRep(image);
                using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
                var name = args.Length > 0 ? args[0] : "lsnap";
                var file = Path.Combine(Folder, index == 0 ? $"{name}.png" : $"{name}-{index}.png");
                data?.Save(file, true);
                saved.Add($"{Path.GetFileName(file)} ({window.GetType().Name})");
                index++;
            }
            return Task.FromResult(saved.Count == 0 ? "no layer-backed windows" : string.Join(", ", saved));
        });
        // "dsnap CLASS NAME" renders the document view of the largest scroll view in that window.
        Register("dsnap", args =>
        {
            var window = AllWindows().FirstOrDefault(item => item.IsVisible && item.GetType().Name == args[0]);
            if (window?.ContentView is null) return Task.FromResult("no window");
            NSScrollView? best = null;
            void Find(NSView view)
            {
                if (view is NSScrollView scroll && !scroll.Hidden && scroll.DocumentView is not null
                    && (best is null || scroll.Frame.Width * scroll.Frame.Height > best.Frame.Width * best.Frame.Height)) best = scroll;
                foreach (var child in view.Subviews) Find(child);
            }
            Find(window.ContentView);
            if (best?.DocumentView is not { } document) return Task.FromResult("no scroll view");
            document.LayoutSubtreeIfNeeded();
            var rep = document.BitmapImageRepForCachingDisplayInRect(document.Bounds);
            if (rep is null) return Task.FromResult("empty");
            document.CacheDisplay(document.Bounds, rep);
            using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            var file = Path.Combine(Folder, (args.Length > 1 ? args[1] : "dsnap") + ".png");
            data?.Save(file, true);
            return Task.FromResult(Path.GetFileName(file) + " " + document.Bounds.ToString());
        });
        // "menus" lists the main menu titles and their items.
        Register("menus", _ => Task.FromResult(string.Join(" || ",
            (NSApplication.SharedApplication.MainMenu?.Items ?? []).Select(item =>
                $"{item.Submenu?.Title ?? item.Title}: " + string.Join(", ", (item.Submenu?.Items ?? []).Where(child => !child.IsSeparatorItem).Select(child => child.Title + (child.Hidden ? "[hidden]" : "") + (child.Alternate ? "[alt]" : "")))))));
        // "raise TITLE" activates the app and brings the first visible window whose title contains TITLE to the front.
        Register("raise", args =>
        {
            var title = string.Join(' ', args);
            var window = AllWindows().FirstOrDefault(item => item.IsVisible && item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (window is null) return Task.FromResult("no window");
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
            window.MakeKeyAndOrderFront(null);
            return Task.FromResult($"{window.Frame}");
        });
        Register("windows", _ => Task.FromResult(string.Join(" | ",
            AllWindows().Where(item => item.IsVisible).Select(item => $"{item.GetType().Name} '{item.Title}' {item.Frame.Width}x{item.Frame.Height}"))));
    }

    private static NSWindow[] AllWindows() => NSApplication.SharedApplication.DangerousWindows.ToArray();

    /// <summary>Renders every visible app window (frame view included, so the toolbar shows) to PNG.</summary>
    public static string Snap(string name)
    {
        var saved = new List<string>();
        int index = 0;
        foreach (var window in AllWindows().Where(item => item.IsVisible && item.ContentView is not null))
        {
            var view = window.ContentView!.Superview ?? window.ContentView!;
            view.LayoutSubtreeIfNeeded();
            var rep = view.BitmapImageRepForCachingDisplayInRect(view.Bounds);
            if (rep is null) continue;
            view.CacheDisplay(view.Bounds, rep);
            using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            var file = Path.Combine(Folder, index == 0 ? $"{name}.png" : $"{name}-{index}.png");
            data?.Save(file, true);
            saved.Add($"{Path.GetFileName(file)} ({window.GetType().Name})");
            index++;
        }
        return saved.Count == 0 ? "no visible windows" : string.Join(", ", saved);
    }
}
#endif
