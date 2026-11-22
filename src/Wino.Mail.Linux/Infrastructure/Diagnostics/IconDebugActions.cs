#if DEBUG
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the icon font.
/// <list type="bullet">
/// <item><c>debug-icons</c> <c>s</c>: opens the window <c>Wino icons</c> with glyphs in every state and mode, then writes a report (font check, style, unknown glyph count) to the path.</item>
/// <item><c>debug-icon-style</c> <c>s</c>: <c>mono</c> or <c>colorful</c>.</item>
/// </list>
/// </summary>
internal static class IconDebugActions
{
    private const string WindowTitle = "Wino icons";
    private static readonly WinoIconGlyph[] StateGlyphs = [WinoIconGlyph.Archive, WinoIconGlyph.Delete, WinoIconGlyph.AppModeMail, WinoIconGlyph.AppModeCalendar, WinoIconGlyph.Flag, WinoIconGlyph.Alert, WinoIconGlyph.Google];
    private static readonly WinoIconGlyph[] SidebarGlyphs = [WinoIconGlyph.SpecialFolderInbox, WinoIconGlyph.SpecialFolderDraft, WinoIconGlyph.SpecialFolderSent, WinoIconGlyph.SpecialFolderDeleted, WinoIconGlyph.SpecialFolderJunk, WinoIconGlyph.SpecialFolderArchive, WinoIconGlyph.Folder];
    private static Adw.Window? _window;
    private static readonly List<Gtk.Label> MonoLabels = [];

    public static void Register(Adw.Application application)
    {
        var icons = Gio.SimpleAction.New("debug-icons", GLib.VariantType.New("s"));
        icons.OnActivate += (_, args) => Run("debug-icons", () =>
        {
            if (args.Parameter?.GetString(out var _) is not { Length: > 0 } path) throw new ArgumentException("A report path is required.");
            ShowGallery(application);
            MainLoop.Delay(600, () => Run("debug-icons", () => WriteReport(path)));
        });
        application.AddAction(icons);

        var style = Gio.SimpleAction.New("debug-icon-style", GLib.VariantType.New("s"));
        style.OnActivate += (_, args) => Run("debug-icon-style", () =>
        {
            WinoIcons.Style = args.Parameter?.GetString(out var _) switch
            {
                "mono" => WinoIconStyle.Monochrome,
                "colorful" => WinoIconStyle.Colorful,
                var other => throw new ArgumentException($"Unknown icon style '{other}'. Use mono or colorful."),
            };
            Serilog.Log.Information("Debug action: icon style {Style}.", WinoIcons.Style);
        });
        application.AddAction(style);
    }

    private static void ShowGallery(Adw.Application application)
    {
        if (_window is not null)
        {
            _window.Present();
            return;
        }

        MonoLabels.Clear();
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        content.SetMarginTop(12);
        content.SetMarginBottom(12);
        content.SetMarginStart(12);
        content.SetMarginEnd(12);

        content.Append(Caption("States at 24 px: mono, error, selected row, insensitive, suggested button, tinted, follow style, colourful, colourful in error"));
        foreach (var glyph in StateGlyphs) content.Append(StateRow(glyph));

        content.Append(Caption("Sizes 12, 16, 20, 24, 32, 48"));
        var sizes = Row();
        foreach (var size in (int[])[12, 16, 20, 24, 32, 48]) sizes.Append(Track(WinoIcons.Label(WinoIconGlyph.SpecialFolderInbox, size)));
        content.Append(sizes);

        content.Append(Caption("Sidebar set at 16 px, follow style"));
        var sidebar = Row();
        foreach (var glyph in SidebarGlyphs) sidebar.Append(new WinoIcon(glyph, 16).Root);
        content.Append(sidebar);

        content.Append(Caption("Buttons"));
        var buttons = Row();
        buttons.Append(WinoIcons.Button(WinoIconGlyph.Reply, "Reply"));
        buttons.Append(WinoIcons.ToggleButton(WinoIconGlyph.Flag, "Flag"));
        var withText = Gtk.Button.New();
        withText.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Archive, "Archive"));
        buttons.Append(withText);
        content.Append(buttons);

        content.Append(Caption("Every glyph, follow style, 20 px"));
        var all = Gtk.FlowBox.New();
        all.SetSelectionMode(Gtk.SelectionMode.None);
        all.SetMaxChildrenPerLine(40);
        foreach (var icon in Enum.GetValues<WinoIconGlyph>())
        {
            if (icon == WinoIconGlyph.None) continue;
            all.Append(new WinoIcon(icon, 20).Root);
        }
        content.Append(all);

        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetChild(content);
        scroller.SetVexpand(true);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(scroller);

        var window = Adw.Window.New();
        window.SetApplication(application);
        window.SetTitle(WindowTitle);
        window.SetDefaultSize(1000, 700);
        window.SetContent(toolbar);
        window.OnCloseRequest += (_, _) =>
        {
            _window = null;
            MonoLabels.Clear();
            return false;
        };
        _window = window;
        window.Present();
    }

    private static Gtk.Box StateRow(WinoIconGlyph glyph)
    {
        const int size = 24;
        var row = Row();
        row.Append(Track(WinoIcons.Label(glyph, size)));

        var error = Row();
        error.AddCssClass("error");
        error.Append(Track(WinoIcons.Label(glyph, size)));
        row.Append(error);

        var list = Gtk.ListBox.New();
        var listRow = Gtk.ListBoxRow.New();
        listRow.SetChild(Track(WinoIcons.Label(glyph, size)));
        list.Append(listRow);
        list.SelectRow(listRow);
        row.Append(list);

        var insensitive = Track(WinoIcons.Label(glyph, size));
        insensitive.SetSensitive(false);
        row.Append(insensitive);

        var suggested = Gtk.Button.New();
        suggested.AddCssClass("suggested-action");
        suggested.SetChild(Track(WinoIcons.Label(glyph, size)));
        WinoA11y.Label(suggested, glyph.ToString());
        row.Append(suggested);

        var tinted = new WinoIcon(glyph, size, WinoIconMode.Mono) { TintHex = "#E67E22" };
        row.Append(tinted.Root);
        row.Append(new WinoIcon(glyph, size).Root);
        row.Append(new WinoIcon(glyph, size, WinoIconMode.Colorful).Root);

        var colorfulError = Row();
        colorfulError.AddCssClass("error");
        colorfulError.Append(new WinoIcon(glyph, size, WinoIconMode.Colorful).Root);
        row.Append(colorfulError);
        return row;
    }

    private static Gtk.Label Track(Gtk.Label label)
    {
        MonoLabels.Add(label);
        return label;
    }

    private static Gtk.Box Row() => Gtk.Box.New(Gtk.Orientation.Horizontal, 16);

    private static Gtk.Label Caption(string text)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.AddCssClass("dim-label");
        return label;
    }

    private static void WriteReport(string path)
    {
        var check = WinoIcons.Verify();
        var unknown = 0;
        foreach (var label in MonoLabels) unknown += label.GetLayout().GetUnknownGlyphsCount();
        var text = $"fonts ok={check.Ok} {check.Detail}\nstyle={WinoIcons.Style} colorful={WinoIcons.IsColorful} dark={Adw.StyleManager.GetDefault().GetDark()}\nlabels={MonoLabels.Count} unknown glyphs {unknown}\n";
        File.WriteAllText(path + ".partial", text);
        File.Move(path + ".partial", path, overwrite: true);
        Serilog.Log.Information("Debug action: icon report written to {Path}.", path);
    }

    private static void Run(string name, Action handler)
    {
        try { handler(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }
}
#endif
