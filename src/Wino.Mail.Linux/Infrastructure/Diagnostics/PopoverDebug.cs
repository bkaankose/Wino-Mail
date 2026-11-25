#if DEBUG
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>Captures an open popover to a PNG and activates one of its menu items by its text, as a click does.</summary>
internal static class PopoverDebug
{
    // A menu item is a button whose activate signal is "clicked", so Activate takes the path of a click.
    public static Gtk.Widget? FindItem(Gtk.Widget widget, string text)
    {
        if ((widget.GetCssName() == "modelbutton" || widget.HasCssClass(WinoCssClass.MenuItem)) && widget.GetMapped() && HasLabel(widget, text)) return widget;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (FindItem(child, text) is { } found) return found;
        }
        return null;
    }

    private static bool HasLabel(Gtk.Widget widget, string text)
    {
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (child is Gtk.Label label && label.GetText() == text) return true;
            if (child is Gtk.Box && HasLabel(child, text)) return true;
        }
        return false;
    }

    /// <summary>Runs a menu item by its label or label path ("Move/Inbox"), else clicks a mapped row with that label.</summary>
    public static void Click(WinoMenuPopover menu, string text)
    {
        if (!menu.Activate(text)) Click(menu.Root, text);
    }

    public static void Capture(WinoMenuPopover menu, string path) => Capture(menu.Root, path);

    public static void Click(Gtk.Widget popover, string text)
    {
        if (FindItem(popover, text) is not { } item) throw new InvalidOperationException($"The menu has no item '{text}'.");
        item.Activate();
    }

    public static void Capture(Gtk.Widget popover, string path, int attempt = 0)
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
                Serilog.Log.Error("Debug action: {Path} not written, the popover never drew a frame.", path);
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
        Serilog.Log.Information("Debug action: {Width}x{Height} popover screenshot written to {Path}.", width, height, path);
    }
}
#endif
