#if DEBUG
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the menu helpers. A window titled <c>Wino menus</c> holds one label with an attached context
/// menu built from shared <c>ContextFlyout</c> entries. Every action except <c>debug-menu-open</c> works on the menu that
/// is open anywhere in the app (<see cref="ContextMenu.Current"/>).
/// <list type="bullet">
/// <item><c>debug-menu-open</c> <c>s</c>: opens the sample menu through the Menu key's action and writes a PNG of the popover to the path.</item>
/// <item><c>debug-menu-submenu</c> <c>as</c>: [label, png]: opens that submenu of the open menu and writes a PNG of it (and of the root to png.root.png).</item>
/// <item><c>debug-menu-shot</c> <c>s</c>: writes a PNG of the open menu's root popover.</item>
/// <item><c>debug-menu-close</c>: pops the open menu down.</item>
/// <item><c>debug-menu-invoke</c> <c>s</c>: runs the item at that label or label path ("Move to/Inbox"), as a click does.</item>
/// <item><c>debug-menu-key</c> <c>s</c>: a GDK key name ("Down", "Return", "Escape") or one character for type-ahead, handled by the deepest level.</item>
/// <item><c>debug-menu-report</c> <c>s</c>: writes the open popover count, the live menu count, the invoked items and the open menu's rows.</item>
/// </list>
/// </summary>
internal static class MenuDebugActions
{
    private const string WindowTitle = "Wino menus";
    private static readonly List<string> Invoked = [];
    private static Adw.Window? _window;
    private static Gtk.Box? _target;
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
                    else Capture(open.Root, path, 0);
                });
            });
        });

        Add(application, "debug-menu-close", null, _ => ContextMenu.Current?.Popdown());

        Add(application, "debug-menu-shot", "s", parameter => Capture(Open().Root, parameter.GetString(out _), 0));

        Add(application, "debug-menu-submenu", "as", parameter =>
        {
            var arguments = parameter.GetStrv(out _);
            if (arguments.Length < 2) throw new ArgumentException("Expected [label, png].");
            var open = Open();
            if (!open.OpenSubmenu(arguments[0])) throw new InvalidOperationException($"The open menu has no submenu '{arguments[0]}'.");
            MainLoop.Delay(600, () =>
            {
                Capture(open.Root, arguments[1] + ".root.png", 0);
                if (open.Submenu is { } submenu) Capture(submenu, arguments[1], 0);
                else Serilog.Log.Error("Debug action: the submenu did not open.");
            });
        });

        Add(application, "debug-menu-invoke", "s", parameter =>
        {
            var label = parameter.GetString(out _);
            PopoverDebug.Click(Open(), label);
        });

        Add(application, "debug-menu-key", "s", parameter =>
        {
            var key = parameter.GetString(out _);
            var keyval = Gdk.Functions.KeyvalFromName(key);
            if (key.Length == 1 && (keyval == 0 || keyval == (uint)Gdk.Constants.KEY_VoidSymbol)) keyval = Gdk.Functions.UnicodeToKeyval(key[0]);
            if (keyval == 0 || keyval == (uint)Gdk.Constants.KEY_VoidSymbol) throw new ArgumentException($"Unknown key '{key}'.");
            var open = Open();
            var handled = open.HandleKey(keyval, 0);
            // Escape is GTK's own popover handling; the debug path closes the deepest level as GTK does.
            if (!handled && keyval == (uint)Gdk.Constants.KEY_Escape)
            {
                if (open.Submenu is { } submenu) submenu.Popdown();
                else open.Popdown();
                handled = true;
            }
            Serilog.Log.Information("Debug action: menu key {Key} handled={Handled} depth={Depth}", key, handled, open.Depth);
        });

        Add(application, "debug-menu-report", "s", parameter =>
        {
            var path = parameter.GetString(out _);
            var text = $"open={ContextMenu.OpenMenus} live={WinoMenus.LiveMenus} invoked=[{string.Join(", ", Invoked)}] flag={_flag} density={_density} depth={ContextMenu.Current?.Depth ?? 0}\n{ContextMenu.Current?.Describe()}";
            File.WriteAllText(path + ".partial", text);
            File.Move(path + ".partial", path, overwrite: true);
            Serilog.Log.Information("Debug action: menu report {Text}", text.Split('\n')[0]);
        });
    }

    private static WinoMenuPopover Open()
        => ContextMenu.Current is { IsOpen: true } open ? open : throw new InvalidOperationException("No menu is open.");

    private static Gtk.Box EnsureWindow(Adw.Application application)
    {
        if (_window is not null && _target is not null)
        {
            _window.Present();
            return _target;
        }

        // A label exposes only its links to accessibility clients, so the menu hangs from a box around it.
        var target = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        target.Append(Gtk.Label.New("Right click, long press or press the Menu key here"));
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
            new() { Label = "Reply", Command = Command("Reply"), Icon = Icon(WinoIconGlyph.Reply), Shortcut = new ContextFlyoutShortcut("Ctrl+R", "R", Control: true) },
        ]);
        ContextFlyoutMenus.Append(builder, (ContextFlyoutMenuEntry[])[
            new ContextFlyoutCommandEntry { Text = "Archive", Command = Command("Archive"), Icon = Icon(WinoIconGlyph.Archive), Shortcut = new ContextFlyoutShortcut("Ctrl+Shift+A", "A", Control: true, Shift: true) },
            new ContextFlyoutCommandEntry { Text = "Delete", Command = Command("Delete"), Icon = Icon(WinoIconGlyph.Delete), IsDestructive = true, Shortcut = new ContextFlyoutShortcut("Delete", "Delete") },
            new ContextFlyoutCommandEntry { Text = "Unavailable", Command = Command("Unavailable"), IsEnabled = false },
            ContextFlyoutSeparatorEntry.Instance,
            new ContextFlyoutToggleEntry { Text = "Flagged", IsChecked = _flag, Icon = Icon(WinoIconGlyph.Flag), Command = new RelayCommand(() => { _flag = !_flag; Record("Flagged"); }) },
            new ContextFlyoutRadioEntry { Text = "Comfortable", GroupName = "density", IsChecked = _density == "comfortable", Command = new RelayCommand(() => { _density = "comfortable"; Record("Comfortable"); }) },
            new ContextFlyoutRadioEntry { Text = "Compact", GroupName = "density", IsChecked = _density == "compact", Command = new RelayCommand(() => { _density = "compact"; Record("Compact"); }) },
            ContextFlyoutSeparatorEntry.Instance,
            new ContextFlyoutSubMenuEntry
            {
                Text = "Move to",
                Icon = Icon(WinoIconGlyph.Move),
                Items = (ContextFlyoutMenuEntry[])[
                    new ContextFlyoutCommandEntry { Text = "Inbox", Command = Command("Move to Inbox"), Icon = Icon(WinoIconGlyph.SpecialFolderInbox) },
                    new ContextFlyoutCommandEntry { Text = "Archive folder", Command = Command("Move to Archive"), Icon = Icon(WinoIconGlyph.SpecialFolderArchive) },
                    new ContextFlyoutSubMenuEntry
                    {
                        Text = "Projects",
                        Icon = Icon(WinoIconGlyph.Folder),
                        Items = (ContextFlyoutMenuEntry[])[
                            new ContextFlyoutCommandEntry { Text = "Alpha", Command = Command("Move to Alpha"), Icon = Icon(WinoIconGlyph.Folder) },
                            new ContextFlyoutCommandEntry { Text = "Beta", Command = Command("Move to Beta"), Icon = Icon(WinoIconGlyph.Folder) },
                        ],
                    },
                ],
            },
            new ContextFlyoutSubMenuEntry
            {
                Text = "Categories",
                Icon = Icon(WinoIconGlyph.SpecialFolderCategory),
                Items = (ContextFlyoutMenuEntry[])[
                    new ContextFlyoutToggleEntry { Text = "Red", IsChecked = true, Icon = Icon(WinoIconGlyph.SpecialFolderCategory, "#c62828"), Command = Command("Red") },
                    new ContextFlyoutToggleEntry { Text = "Blue", Icon = Icon(WinoIconGlyph.SpecialFolderCategory, "#1565c0"), Command = Command("Blue") },
                ],
            },
        ]);
    }

    private static ContextFlyoutIcon Icon(WinoIconGlyph glyph, string? tint = null) => new(WinoIcons.Glyph(glyph), tint);

    private static RelayCommand Command(string name) => new(() => Record(name));

    private static void Record(string name)
    {
        Invoked.Add(name);
        Serilog.Log.Information("Debug action: menu item {Name} invoked.", name);
    }

    private static void Capture(Gtk.Popover popover, string path, int attempt)
    {
        var width = popover.GetWidth();
        var height = popover.GetHeight();
        var renderer = popover.GetNative()?.GetRenderer();
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
