#if DEBUG
using System.Text;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug-only application actions for checks without screen access, registered only when the app starts with
/// <c>--debug-actions</c>. GApplication exports them on D-Bus:
/// <c>gdbus call --session --dest app.winomail.Mail --object-path /app/winomail/Mail --method org.freedesktop.Application.ActivateAction debug-resize "[&lt;(1008, 700)&gt;]" "{}"</c>.
/// <list type="bullet">
/// <item><c>debug-screenshot</c> <c>as</c>: a PNG path, optionally the accessible label of the widget to capture instead of the main window.</item>
/// <item><c>debug-resize</c> <c>(ii)</c>: the default size of the main window.</item>
/// <item><c>debug-theme</c> <c>s</c>: <c>light</c>, <c>dark</c> or <c>system</c>.</item>
/// <item><c>debug-dump-tree</c> <c>s</c>: a text file with every widget of every window.</item>
/// <item><c>debug-quit</c>: closes the main window, which runs the shared shutdown.</item>
/// </list>
/// Files are written to a temporary name and then moved, so a caller that waits for the path never reads a partial file.
/// </summary>
internal static class DebugActions
{
    private const int MaxFrameRetries = 120;

    public static void Register(Adw.Application application, Func<Gtk.Window?> mainWindow)
    {
        Add(application, "debug-screenshot", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out _);
            if (arguments.Length == 0 || string.IsNullOrWhiteSpace(arguments[0])) throw new ArgumentException("A PNG path is required.");
            var label = arguments.Length > 1 && !string.IsNullOrWhiteSpace(arguments[1]) ? arguments[1] : null;
            var target = label is null ? mainWindow() : FindByLabel(label);
            if (target is null) throw new InvalidOperationException(label is null ? "There is no main window." : $"No widget is labelled '{label}'.");
            Screenshot(target, arguments[0], 0);
        });

        Add(application, "debug-resize", "(ii)", parameter =>
        {
            var width = parameter.GetChildValue(0).GetInt32();
            var height = parameter.GetChildValue(1).GetInt32();
            if (mainWindow() is not { } window) throw new InvalidOperationException("There is no main window.");
            if (window.IsMaximized()) window.Unmaximize();
            window.SetDefaultSize(width, height);
            Serilog.Log.Information("Debug action: resized to {Width}x{Height}.", width, height);
        });

        Add(application, "debug-theme", "s", parameter =>
        {
            var name = parameter.GetString(out _);
            var scheme = name switch
            {
                "light" => Adw.ColorScheme.ForceLight,
                "dark" => Adw.ColorScheme.ForceDark,
                "system" => Adw.ColorScheme.Default,
                _ => throw new ArgumentException($"Unknown theme '{name}'. Use light, dark or system."),
            };
            Adw.StyleManager.GetDefault().SetColorScheme(scheme);
            Serilog.Log.Information("Debug action: color scheme {Scheme}.", scheme);
        });

        Add(application, "debug-dump-tree", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            var text = new StringBuilder();
            foreach (var window in Toplevels()) Dump(window, window, 0, text);
            WriteAtomically(path, temporary => File.WriteAllText(temporary, text.ToString()));
            Serilog.Log.Information("Debug action: tree written to {Path}.", path);
        });

        Add(application, "debug-quit", () =>
        {
            if (mainWindow() is { } window) window.Close();
            else application.Quit();
        });

        Serilog.Log.Warning("Debug actions are enabled.");
    }

    /// <summary>Writes a PNG of <paramref name="target"/> (a window or a widget in it), like <c>debug-screenshot</c>.</summary>
    internal static void Capture(Gtk.Widget target, string path) => Screenshot(target, path, 0);

    /// <summary>What <c>debug-dump-tree</c> writes, for one window.</summary>
    internal static string Describe(Gtk.Window window)
    {
        var text = new StringBuilder();
        Dump(window, window, 0, text);
        return text.ToString();
    }

    internal static void WriteText(string path, string text) => WriteAtomically(path, temporary => File.WriteAllText(temporary, text));

    /// <summary>The first mapped widget of any window with that accessible label, as <c>debug-screenshot</c> finds it.</summary>
    internal static Gtk.Widget? FindWidget(string label) => FindByLabel(label);

    private static void Add(Adw.Application application, string name, string parameterType, Action<GLib.Variant> handler)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New(parameterType));
        action.OnActivate += (_, args) => Run(name, () =>
        {
            if (args.Parameter is not { } parameter) throw new ArgumentException($"{name} needs a parameter of type {parameterType}.");
            handler(parameter);
        });
        application.AddAction(action);
    }

    private static void Add(Adw.Application application, string name, Action handler)
    {
        var action = Gio.SimpleAction.New(name, null);
        action.OnActivate += (_, _) => Run(name, handler);
        application.AddAction(action);
    }

    private static void Run(string name, Action handler)
    {
        try { handler(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }

    // A widget is cut out of its window's picture, so it keeps the background its parents draw. A window that is not
    // laid out, not attached to a renderer or drew nothing yet is retried on its next frame.
    private static void Screenshot(Gtk.Widget target, string path, int attempt)
    {
        // The nearest native is the popover for a menu item (it has its own surface) and the window otherwise.
        var root = target.GetNative() as Gtk.Widget ?? target.GetRoot() as Gtk.Widget ?? target;
        if (target.GetRoot() is Gtk.Window { Suspended: true })
        {
            Serilog.Log.Error("Debug action: {Path} not written, the window is suspended (minimized, hidden or the screen is locked) and draws nothing.", path);
            return;
        }

        var width = root.GetWidth();
        var height = root.GetHeight();
        var renderer = root.GetNative()?.GetRenderer();
        var snapshot = Gtk.Snapshot.New();
        if (renderer is not null && width > 0 && height > 0 && target.GetMapped()) Gtk.WidgetPaintable.New(root).Snapshot(snapshot, width, height);
        if (renderer is null || snapshot.ToNode() is not { } node)
        {
            if (attempt >= MaxFrameRetries)
            {
                Serilog.Log.Error("Debug action: {Path} not written, the window never drew a frame.", path);
                return;
            }
            root.AddTickCallback((_, _) =>
            {
                Screenshot(target, path, attempt + 1);
                return false;
            });
            root.QueueDraw();
            return;
        }

        var viewport = new Graphene.Rect();
        if (ReferenceEquals(target, root)) viewport.Init(0, 0, width, height);
        else if (!target.ComputeBounds(root, out viewport))
        {
            Serilog.Log.Error("Debug action: {Path} not written, the widget has no place in its window.", path);
            return;
        }

        // The viewport is the window's own box or the widget's box inside it, so the window shadow stays out.
        var texture = renderer.RenderTexture(node, viewport);
        WriteAtomically(path, temporary =>
        {
            if (!texture.SaveToPng(temporary)) throw new IOException($"GDK could not write {temporary}.");
        });
        Serilog.Log.Information("Debug action: {Width}x{Height} screenshot written to {Path}.", texture.GetWidth(), texture.GetHeight(), path);
    }

    private static void WriteAtomically(string path, Action<string> write)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".partial";
        write(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    private static List<Gtk.Window> Toplevels()
    {
        var model = Gtk.Window.GetToplevels();
        var windows = new List<Gtk.Window>();
        for (uint index = 0; index < model.GetNItems(); index++)
        {
            if (model.GetObject(index) is Gtk.Window window) windows.Add(window);
        }
        return windows;
    }

    private static Gtk.Widget? FindByLabel(string label)
    {
        foreach (var window in Toplevels())
        {
            if (Find(window, label) is { } widget) return widget;
        }
        return null;
    }

    private static Gtk.Widget? Find(Gtk.Widget widget, string label)
    {
        if (widget.GetMapped() && string.Equals(AccessibleLabel(widget), label, StringComparison.Ordinal)) return widget;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (Find(child, label) is { } found) return found;
        }
        return null;
    }

    private static void Dump(Gtk.Widget widget, Gtk.Window window, int depth, StringBuilder text)
    {
        var bounds = widget.ComputeBounds(window, out var rect)
            ? $"{rect.GetX():0},{rect.GetY():0} {rect.GetWidth():0}x{rect.GetHeight():0}"
            : "unplaced";
        text.Append(' ', depth * 2)
            .Append(widget.GetType().FullName).Append(" css=").Append(widget.GetCssName())
            .Append(" role=").Append(widget.GetAccessibleRole().ToString().ToLowerInvariant())
            .Append(" label=\"").Append(AccessibleLabel(widget)).Append('"')
            .Append(" classes=[").Append(string.Join(',', widget.GetCssClasses())).Append(']')
            .Append(" bounds=").Append(bounds)
            .Append(widget.GetVisible() ? " visible" : " hidden")
            .Append(widget.GetMapped() ? " mapped" : string.Empty)
            .Append(widget.GetSensitive() ? " sensitive" : " insensitive")
            .AppendLine();
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling()) Dump(child, window, depth + 1, text);
    }

    // GTK has no public getter for the computed accessible name. This mirrors GTK's rules for the widgets the head
    // uses (own text first, then the content's text for button-like roles; list items get no name from content), so
    // it differs from the AT-SPI name for widgets that set a label through Gtk.Accessible.UpdateProperty.
    private static string AccessibleLabel(Gtk.Widget widget)
    {
        var own = widget switch
        {
            Gtk.Window window => window.GetTitle(),
            Gtk.Label label => label.GetText(),
            Gtk.Button button => button.GetLabel(),
            Gtk.Editable => null,
            Adw.PreferencesRow row => row.GetTitle(),
            Adw.StatusPage page => page.GetTitle(),
            Adw.WindowTitle title => title.GetTitle(),
            Adw.NavigationPage page => page.GetTitle(),
            _ => null,
        };
        if (!string.IsNullOrEmpty(own)) return own;
        return NamedByContent(widget.GetAccessibleRole()) ? ContentText(widget) : string.Empty;
    }

    private static bool NamedByContent(Gtk.AccessibleRole role) => role is Gtk.AccessibleRole.Button or Gtk.AccessibleRole.ToggleButton
        or Gtk.AccessibleRole.Checkbox or Gtk.AccessibleRole.Link or Gtk.AccessibleRole.MenuItem or Gtk.AccessibleRole.Tab
        or Gtk.AccessibleRole.Switch;

    private static string ContentText(Gtk.Widget widget)
    {
        var parts = new List<string>();
        Collect(widget, parts);
        return string.Join(' ', parts);

        static void Collect(Gtk.Widget node, List<string> parts)
        {
            for (var child = node.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (!child.GetVisible()) continue;
                if (child is Gtk.Label { } label)
                {
                    if (label.GetText() is { Length: > 0 } text) parts.Add(text);
                }
                else Collect(child, parts);
            }
        }
    }
}
#endif
