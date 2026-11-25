using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Contacts;

/// <summary>
/// A titled group of read-only values in the contact detail pane (Windows field card, macOS WinoContactFieldCard): a
/// caption over a boxed list of property rows whose values can be selected and copied. It connects no signal.
/// </summary>
public sealed class WinoContactFieldCard
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
    private readonly Gtk.ListBox _list = Gtk.ListBox.New();

    public WinoContactFieldCard(string title)
    {
        var caption = Gtk.Label.New(title);
        caption.AddCssClass("caption-heading");
        caption.AddCssClass("dim-label");
        caption.SetXalign(0);
        caption.SetMarginStart(4);
        _root.Append(caption);
        _list.AddCssClass("boxed-list");
        _list.SetSelectionMode(Gtk.SelectionMode.None);
        WinoA11y.Label(_list, title);
        _root.Append(_list);
    }

    public Gtk.Widget Root => _root;

    public int Count { get; private set; }

    /// <summary>Adds one value row. A blank value adds nothing, so optional fields can be added freely.</summary>
    public WinoContactFieldCard Add(string? value, string? label = null, WinoIconGlyph glyph = WinoIconGlyph.None, bool wrap = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        var row = Adw.ActionRow.New();
        row.SetUseMarkup(false);
        row.SetActivatable(false);
        if (!string.IsNullOrWhiteSpace(label))
        {
            row.AddCssClass("property");
            row.SetTitle(label);
            row.SetSubtitle(value);
            row.SetSubtitleSelectable(true);
            row.SetSubtitleLines(wrap ? 0 : 1);
        }
        else
        {
            row.SetTitle(value);
            row.SetTitleSelectable(true);
            row.SetTitleLines(wrap ? 0 : 1);
        }
        if (glyph != WinoIconGlyph.None)
        {
            var icon = WinoIcons.Label(glyph, 16);
            icon.AddCssClass("dim-label");
            row.AddPrefix(icon);
        }
        _list.Append(row);
        Count++;
        return this;
    }
}
