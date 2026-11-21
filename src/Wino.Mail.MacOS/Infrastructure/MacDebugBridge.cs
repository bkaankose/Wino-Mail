#if DEBUG
using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Interfaces;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Debug-only bridge for remote development without screen access. It polls
/// {container tmp}/wino-debug/cmd; each line is one command, results go to out.log and
/// snapshots to *.png in the same folder. Snapshots render the app's own windows, so they need
/// no Screen Recording permission (behind-window vibrancy renders as its fallback colour).
///
/// Commands: snap NAME · snap2x NAME · seedmail · seedstore · mode Mail|Calendar|Contacts|Tasks · settings [WinoPage] ·
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
        // Common modes, so a modal alert (which runs the loop in the modal mode) does not stop the bridge.
        _timer = NSTimer.CreateRepeatingTimer(0.5, timer => { _ = PollAsync(); });
        NSRunLoop.Main.AddTimer(_timer, NSRunLoopMode.Common);
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
        Register("expander", args =>
        {
            var expanders = new List<Wino.Mail.Controls.AppKit.Settings.WinoSettingsExpander>();
            void Collect(NSView view)
            {
                if (view is Wino.Mail.Controls.AppKit.Settings.WinoSettingsExpander expander) expanders.Add(expander);
                foreach (var child in view.Subviews) Collect(child);
            }
            foreach (var window in AllWindows().Where(item => item.IsVisible && item.ContentView is not null)) Collect(window.ContentView!);
            if (args.Length == 0) return Task.FromResult(string.Join(", ", expanders.Select((item, index) => $"{index}:{item.HeaderCard.Header}={item.IsExpanded}")));
            var target = expanders[int.Parse(args[0])];
            target.IsExpanded = args.Length > 1 ? bool.Parse(args[1]) : !target.IsExpanded;
            return Task.FromResult($"{target.HeaderCard.Header}={target.IsExpanded}");
        });
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
        // "seedmail" adds the shared demo account with its folders and mails (MailDebugSeed), then opens Mail.
        Register("seedmail", async _ =>
        {
            var result = await Wino.Mail.ViewModels.Diagnostics.MailDebugSeed.SeedAsync(_services!);
            Get<AppKitNavigationService>().ChangeApplicationMode(WinoApplicationMode.Mail);
            return result;
        });
        // "seedstore" adds the store screenshot demo (StoreListingDebugSeed): Outlook, Gmail and iCloud accounts with mail,
        // calendars, contacts and tasks. Run it on an empty data folder.
        Register("seedstore", async _ =>
        {
            var result = await Wino.Mail.ViewModels.Diagnostics.StoreListingDebugSeed.SeedAsync(_services!);
            Get<AppKitNavigationService>().ChangeApplicationMode(WinoApplicationMode.Mail);
            return result;
        });
        // "clearattention" removes the attention state the demo's token-less Outlook and Gmail accounts get after a sync.
        Register("clearattention", async _ =>
        {
            var accounts = Get<IAccountService>();
            int cleared = 0;
            foreach (var account in await accounts.GetAccountsAsync())
            {
                if (account.AttentionReason == AccountAttentionReason.None) continue;
                account.AttentionReason = AccountAttentionReason.None;
                await accounts.UpdateAccountAsync(account);
                cleared++;
            }
            return $"cleared {cleared}";
        });
        // "wsnap NAME" captures the shell window as the window server composites it (glass and vibrancy included). It needs
        // Screen Recording permission for Wino Mail; the first call shows the system prompt.
        Register("wsnap", args =>
        {
            if (!CGPreflightScreenCaptureAccess())
            {
                CGRequestScreenCaptureAccess();
                return Task.FromResult("no screen recording permission; prompt requested");
            }
            var window = AllWindows().Where(item => item.IsVisible && item.StyleMask.HasFlag(NSWindowStyle.Titled)).MaxBy(item => item.Frame.Width * item.Frame.Height);
            if (window is null) return Task.FromResult("no window");
            // kCGWindowListOptionIncludingWindow, kCGWindowImageBoundsIgnoreFraming | kCGWindowImageBestResolution
            var handle = CGWindowListCreateImage(CoreGraphics.CGRect.Null, 8, (uint)window.WindowNumber, 9);
            if (handle == IntPtr.Zero) return Task.FromResult("capture failed");
            using var image = ObjCRuntime.Runtime.GetINativeObject<CoreGraphics.CGImage>(handle, true)!;
            using var rep = new NSBitmapImageRep(image);
            using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            var file = Path.Combine(Folder, (args.Length > 0 ? args[0] : "wsnap") + ".png");
            data?.Save(NSUrl.FromFilename(file), true);
            return Task.FromResult($"{Path.GetFileName(file)} {image.Width}x{image.Height}");
        });
        // "snap2x NAME" is snap at twice the window's point size, for Retina-size store screenshots on a 1x display.
        Register("snap2x", args => Task.FromResult(Snap(args.Length > 0 ? args[0] : "snap2x", 2)));
        // "pref NAME" prints a preference; "pref NAME VALUE" sets it (bool, number, string or enum member name).
        Register("live", _ =>
        {
            var lines = Wino.Presentation.AppKit.WinoLiveObjects.Snapshot().Select(entry => $"{entry.Type}={entry.Count}({entry.Disposed}d)");
            var rss = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            return Task.FromResult($"rss={rss}MB managed={GC.GetTotalMemory(false) / (1024 * 1024)}MB " + string.Join(' ', lines));
        });
        Register("chain", args =>
        {
            var report = new System.Text.StringBuilder();
            foreach (var instance in Wino.Presentation.AppKit.WinoLiveObjects.Instances(args[0]).OfType<NSView>().Take(6))
            {
                if (instance.Handle == IntPtr.Zero) { report.AppendLine("disposed"); continue; }
                var chain = new List<string>();
                NSView? view = instance;
                int guard = 0;
                while (view is not null && guard++ < 40) { chain.Add(view.GetType().Name); view = view.Superview; }
                report.AppendLine($"window={(instance.Window is null ? "none" : "yes")} retain={instance.RetainCount} chain={string.Join(" < ", chain)}");
            }
            return Task.FromResult(report.ToString());
        });
        Register("roots", _ =>
        {
            var report = new System.Text.StringBuilder();
            void Dump(string name, IEnumerable<string> targets)
            {
                var groups = targets.GroupBy(t => t).Select(g => $"{g.Key.Split('.').Last()}x{g.Count()}");
                report.Append(name).Append(": ").Append(string.Join(' ', groups)).Append(Environment.NewLine);
            }
            var preferences = Get<IPreferencesService>();
            Dump("PreferenceChanged", Wino.Presentation.AppKit.WinoLiveObjects.EventTargets(preferences.GetType(), preferences, "PreferenceChanged"));
            Dump("AccentChanged", Wino.Presentation.AppKit.WinoLiveObjects.EventTargets(typeof(Wino.Presentation.AppKit.WinoStyle), null, "AccentChanged"));
            Dump("BackdropChanged", Wino.Presentation.AppKit.WinoLiveObjects.EventTargets(typeof(Wino.Presentation.AppKit.WinoStyle), null, "BackdropChanged"));
            Dump("StyleChanged", Wino.Presentation.AppKit.WinoLiveObjects.EventTargets(typeof(Wino.Presentation.AppKit.WinoIcons), null, "StyleChanged"));
            var sync = Get<ISynchronizationManager>();
            foreach (var evt in sync.GetType().GetEvents()) Dump("Sync." + evt.Name, Wino.Presentation.AppKit.WinoLiveObjects.EventTargets(sync.GetType(), sync, evt.Name));
            return Task.FromResult(report.ToString());
        });
        Register("pref", args =>
        {
            var preferences = Get<IPreferencesService>();
            var property = typeof(IPreferencesService).GetProperty(args[0]);
            if (property is null) return Task.FromResult("no preference " + args[0]);
            if (args.Length > 1)
            {
                var text = string.Join(' ', args.Skip(1));
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                object value = type.IsEnum ? Enum.Parse(type, text, true) : Convert.ChangeType(text, type, System.Globalization.CultureInfo.InvariantCulture);
                property.SetValue(preferences, value);
            }
            return Task.FromResult($"{args[0]}={property.GetValue(preferences)}");
        });
        Register("iconstyle", args =>
        {
            Get<IPreferencesService>().IconStyle = args[0].StartsWith("c", StringComparison.OrdinalIgnoreCase) ? WinoIconStyle.Colorful : WinoIconStyle.Monochrome;
            return Task.FromResult("ok");
        });
        Register("size", args =>
        {
            // In the background there is no main window; the largest titled window is the shell, not the status item.
            var window = NSApplication.SharedApplication.MainWindow
                ?? AllWindows().Where(item => item.IsVisible && item.StyleMask.HasFlag(NSWindowStyle.Titled)).MaxBy(item => item.Frame.Width * item.Frame.Height);
            if (window is null) return Task.FromResult("no window");
            var frame = window.Frame;
            window.SetFrame(new CoreGraphics.CGRect(frame.X, frame.Y, (nfloat)double.Parse(args[0]), (nfloat)double.Parse(args[1])), true);
            return Task.FromResult("ok");
        });
        // "wsize WIDTH HEIGHT TITLE" sizes the first visible window whose title contains TITLE, keeping its top-left corner.
        Register("wsize", args =>
        {
            var title = string.Join(' ', args.Skip(2));
            var window = AllWindows().FirstOrDefault(item => item.IsVisible && item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (window is null) return Task.FromResult("no window");
            var frame = window.Frame;
            var height = (nfloat)double.Parse(args[1]);
            window.SetFrame(new CoreGraphics.CGRect(frame.X, frame.Bottom - height, (nfloat)double.Parse(args[0]), height), true);
            return Task.FromResult($"{window.Frame}");
        });
        Register("front", _ =>
        {
            // Lets a full-screen capture on the Mac show Wino without granting System Events access.
            NSApplication.SharedApplication.Activate();
            foreach (var window in AllWindows().Where(item => item.IsVisible)) window.OrderFrontRegardless();
            return Task.FromResult("ok");
        });
        // "fitwide CLASS MIN" writes the views (hidden ones marked) of that window whose own fitting width is at least MIN to fitwide.txt:
        // the views that keep a window from getting narrower.
        Register("fitwide", args =>
        {
            var window = AllWindows().FirstOrDefault(item => item.IsVisible && item.GetType().Name == args[0]);
            if (window?.ContentView is null) return Task.FromResult("no window");
            var minimum = double.Parse(args[1]);
            var builder = new System.Text.StringBuilder();
            void Walk(NSView view, int depth)
            {
                var width = view.FittingSize.Width;
                if (width >= minimum)
                    builder.Append(' ', depth * 2).Append(view.GetType().Name).Append(" fit=").Append(Math.Round(width))
                        .Append(" frame=").Append(Math.Round(view.Frame.Width)).Append(view.Hidden ? " hidden" : string.Empty)
                        .Append(' ').Append(view.AccessibilityIdentifier)
                        .Append(view is NSTextField field ? $" \"{field.StringValue}\" lines={field.MaximumNumberOfLines} hres={view.GetContentCompressionResistancePriority(NSLayoutConstraintOrientation.Horizontal)}" : string.Empty)
                        .AppendLine();
                foreach (var child in view.Subviews) Walk(child, depth + 1);
            }
            Walk(window.ContentView, 0);
            File.WriteAllText(Path.Combine(Folder, "fitwide.txt"), builder.ToString());
            return Task.FromResult("fitwide.txt");
        });
        // "tree CLASS [DEPTH]" writes the view tree of the first visible window of that class to tree.txt.
        Register("tree", args =>
        {
            // "tree TYPE|TITLE-PREFIX [DEPTH]": a sheet is a plain NSWindow like the status item, so its title picks it.
            var window = AllWindows().FirstOrDefault(item => item.IsVisible && item.GetType().Name == args[0])
                ?? AllWindows().FirstOrDefault(item => item.IsVisible && item.Title.StartsWith(args[0], StringComparison.OrdinalIgnoreCase));
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
                data?.Save(NSUrl.FromFilename(file), true);
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
            data?.Save(NSUrl.FromFilename(file), true);
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
            NSApplication.SharedApplication.Activate();
            window.MakeKeyAndOrderFront(null);
            return Task.FromResult($"{window.Frame}");
        });
        Register("windows", _ => Task.FromResult(string.Join(" | ",
            AllWindows().Where(item => item.IsVisible).Select(item => $"{item.GetType().Name} '{item.Title}' {item.Frame.Width}x{item.Frame.Height}"))));
    }

    private static NSWindow[] AllWindows() => NSApplication.SharedApplication.DangerousWindows.ToArray();

    /// <summary>Renders every visible app window (frame view included, so the toolbar shows) to PNG.</summary>
    public static string Snap(string name, int scale = 1)
    {
        var saved = new List<string>();
        int index = 0;
        foreach (var window in AllWindows().Where(item => item.IsVisible && item.ContentView is not null))
        {
            var view = window.ContentView!.Superview ?? window.ContentView!;
            view.LayoutSubtreeIfNeeded();
            var rep = scale == 1 ? view.BitmapImageRepForCachingDisplayInRect(view.Bounds) : ScaledRep(view.Bounds.Size, scale);
            if (rep is null) continue;
            view.CacheDisplay(view.Bounds, rep);
            using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            var file = Path.Combine(Folder, index == 0 ? $"{name}.png" : $"{name}-{index}.png");
            data?.Save(NSUrl.FromFilename(file), true);
            saved.Add($"{Path.GetFileName(file)} ({window.GetType().Name})");
            index++;
        }
        return saved.Count == 0 ? "no visible windows" : string.Join(", ", saved);
    }

    private const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [System.Runtime.InteropServices.DllImport(CoreGraphicsLibrary)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    [System.Runtime.InteropServices.DllImport(CoreGraphicsLibrary)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool CGRequestScreenCaptureAccess();

    // Deprecated in favour of ScreenCaptureKit but still present; this is a debug-only capture path.
    [System.Runtime.InteropServices.DllImport(CoreGraphicsLibrary)]
    private static extern IntPtr CGWindowListCreateImage(CoreGraphics.CGRect bounds, uint listOption, uint windowId, uint imageOption);

    // A bitmap with more pixels than points: cacheDisplay then draws the view at that scale.
    private static NSBitmapImageRep ScaledRep(CoreGraphics.CGSize size, int scale)
    {
        var rep = new NSBitmapImageRep(IntPtr.Zero, (nint)(size.Width * scale), (nint)(size.Height * scale), 8, 4, true, false, "NSDeviceRGBColorSpace", 0, 0);
        rep.Size = size;
        return rep;
    }
}
#endif
