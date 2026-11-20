#if DEBUG
using CommunityToolkit.Mvvm.Input;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the menu helpers. A window titled <c>Wino menus</c> holds one label with an attached context
/// menu built from shared <c>ContextFlyout</c> entries.
/// <list type="bullet">
/// <item><c>debug-menu-open</c> <c>s</c>: opens the menu through the Menu key's action and writes a PNG of the popover to the path.</item>
/// <item><c>debug-menu-close</c>: pops the open menu down.</item>
/// <item><c>debug-menu-invoke</c> <c>s</c>: activates the item with that text in the open menu, as a click does.</item>
/// <item><c>debug-menu-report</c> <c>s</c>: writes the open popover count, the live menu count and the invoked items.</item>
/// </list>
/// </summary>
internal static class MenuDebugActions
{
    private const string WindowTitle = "Wino menus";
    private static readonly List<string> Invoked = [];
    private static Adw.Window? _window;
    private static Gtk.Label? _target;
    private static ContextMenuAttachment? _attachment;
    private static bool _flag = true;
    private static string _density = "comfortable";

    public static void Register(Adw.Application application)
    {
        Add(application, "debug-menu-open", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A PNG path is required.");
            var target = EnsureWindow(application);
            MainLoop.Delay(500, () =>
            {
                if (!target.ActivateAction(ContextMenu.PopupAction, null)) Serilog.Log.Error("Debug action: {Action} did not run.", ContextMenu.PopupAction);
                MainLoop.Delay(600, () =>
                {
                    if (_attachment?.Current is not { } open) Serilog.Log.Error("Debug action: no menu is open.");
                    else Capture(open, path, 0);
                });
            });
        });

        Add(application, "debug-menu-close", null, _ => _attachment?.Current?.Popdown());

        Add(application, "debug-menu-invoke", "s", parameter =>
        {
            var label = parameter.GetString(out _);
            if (_attachment?.Current is not { } open) throw new InvalidOperationException("No menu is open.");
            if (FindItem(open, label) is not { } item) throw new InvalidOperationException($"The menu has no item '{label}'.");
            item.Activate();
        });

        Add(application, "debug-menu-report", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            var text = $"open={ContextMenu.OpenMenus} live={WinoMenus.LiveMenus} invoked=[{string.Join(", ", Invoked)}] flag={_flag} density={_density}\n";
            File.WriteAllText(path + ".partial", text);
            File.Move(path + ".partial", path, overwrite: true);
            Serilog.Log.Information("Debug action: menu report {Text}", text.TrimEnd());
        });
    }

    private static Gtk.Label EnsureWindow(Adw.Application application)
    {
        if (_window is not null && _target is not null)
        {
            _window.Present();
            return _target;
        }

        var target = Gtk.Label.New("Right click, long press or press the Menu key here");
        target.SetFocusable(true);
        target.SetValign(Gtk.Align.Start);
        target.SetMarginTop(48);
        target.SetMarginBottom(48);
        target.SetMarginStart(48);
        target.SetMarginEnd(48);
        WinoA11y.Label(target, "Context menu target");

        var window = Adw.Window.New();
        window.SetApplication(application);
        window.SetTitle(WindowTitle);
        window.SetDefaultSize(640, 520);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(target);
        window.SetContent(toolbar);
        window.OnCloseRequest += (_, _) =>
        {
            _attachment?.Dispose();
            _attachment = null;
            _window = null;
            _target = null;
            return false;
        };

        _attachment = ContextMenu.Attach(target, Populate);
        _window = window;
        _target = target;
        window.Present();
        return target;
    }

    private static void Populate(WinoMenuBuilder builder)
    {
        ContextFlyoutMenus.AppendHeaders(builder, (ContextFlyoutHeaderEntry[])[
            new() { Label = "Reply", Command = Command("Reply"), Shortcut = new ContextFlyoutShortcut("Ctrl+R", "R", Control: true) },
        ]);
        ContextFlyoutMenus.Append(builder, (ContextFlyoutMenuEntry[])[
            new ContextFlyoutCommandEntry { Text = "Archive", Command = Command("Archive"), Shortcut = new ContextFlyoutShortcut("Ctrl+Shift+A", "A", Control: true, Shift: true) },
            new ContextFlyoutCommandEntry { Text = "Delete", Command = Command("Delete"), IsDestructive = true, Shortcut = new ContextFlyoutShortcut("Delete", "Delete") },
            new ContextFlyoutCommandEntry { Text = "Unavailable", Command = Command("Unavailable"), IsEnabled = false },
            ContextFlyoutSeparatorEntry.Instance,
            new ContextFlyoutToggleEntry { Text = "Flagged", IsChecked = _flag, Command = new RelayCommand(() => { _flag = !_flag; Record("Flagged"); }) },
            new ContextFlyoutRadioEntry { Text = "Comfortable", GroupName = "density", IsChecked = _density == "comfortable", Command = new RelayCommand(() => { _density = "comfortable"; Record("Comfortable"); }) },
            new ContextFlyoutRadioEntry { Text = "Compact", GroupName = "density", IsChecked = _density == "compact", Command = new RelayCommand(() => { _density = "compact"; Record("Compact"); }) },
            ContextFlyoutSeparatorEntry.Instance,
            new ContextFlyoutSubMenuEntry
            {
                Text = "Move to",
                Items = (ContextFlyoutMenuEntry[])[
                    new ContextFlyoutCommandEntry { Text = "Inbox", Command = Command("Move to Inbox") },
                    new ContextFlyoutCommandEntry { Text = "Archive folder", Command = Command("Move to Archive") },
                ],
            },
        ]);
    }

    // A menu item is a GtkModelButton whose activate signal is "clicked", so Activate takes the path of a click.
    private static Gtk.Widget? FindItem(Gtk.Widget widget, string text)
    {
        if (widget.GetCssName() == "modelbutton" && widget.GetMapped())
        {
            for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
            {
                if (child is Gtk.Label label && label.GetText() == text) return widget;
            }
        }
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (FindItem(child, text) is { } found) return found;
        }
        return null;
    }

    private static RelayCommand Command(string name) => new(() => Record(name));

    private static void Record(string name)
    {
        Invoked.Add(name);
        Serilog.Log.Information("Debug action: menu item {Name} invoked.", name);
    }

    private static void Capture(Gtk.PopoverMenu popover, string path, int attempt)
    {
        var width = popover.GetWidth();
        var height = popover.GetHeight();
        var renderer = popover.GetRenderer();
        var snapshot = Gtk.Snapshot.New();
        if (renderer is not null && width > 0 && height > 0 && popover.GetMapped()) Gtk.WidgetPaintable.New(popover).Snapshot(snapshot, width, height);
        if (renderer is null || snapshot.ToNode() is not { } node)
        {
            if (attempt >= 120)
            {
                Serilog.Log.Error("Debug action: {Path} not written, the menu never drew a frame.", path);
                return;
            }
            popover.AddTickCallback((_, _) =>
            {
                Capture(popover, path, attempt + 1);
                return false;
            });
            popover.QueueDraw();
            return;
        }

        var viewport = new Graphene.Rect();
        viewport.Init(0, 0, width, height);
        var texture = renderer.RenderTexture(node, viewport);
        if (!texture.SaveToPng(path + ".partial")) throw new IOException($"GDK could not write {path}.partial.");
        File.Move(path + ".partial", path, overwrite: true);
        Serilog.Log.Information("Debug action: {Width}x{Height} menu screenshot written to {Path}.", width, height, path);
    }

    private static void Add(Adw.Application application, string name, string? parameterType, Action<GLib.Variant> handler)
    {
        var action = Gio.SimpleAction.New(name, parameterType is null ? null : GLib.VariantType.New(parameterType));
        action.OnActivate += (_, args) =>
        {
            try
            {
                if (parameterType is null) handler(GLib.Variant.NewBoolean(true));
                else if (args.Parameter is { } parameter) handler(parameter);
                else throw new ArgumentException($"{name} needs a parameter of type {parameterType}.");
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }
}
#endif
