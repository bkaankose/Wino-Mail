using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.MailList;

/// <summary>The date group strip of the mail list. The floating copy sits over the list as its sticky header.</summary>
public sealed class WinoMailGroupHeader
{
    private readonly Gtk.Box _root;
    private readonly Gtk.Label _label;
    private string _title = string.Empty;

    public WinoMailGroupHeader(bool floating = false)
    {
        _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _root.AddCssClass(WinoCssClass.GroupHeader);
        if (floating) _root.AddCssClass(WinoCssClass.Floating);
        _root.SetHexpand(true);

        _label = Gtk.Label.New(string.Empty);
        _label.AccessibleRole = Gtk.AccessibleRole.Heading;
        _label.SetXalign(0);
        _label.SetHexpand(true);
        _label.SetEllipsize(Pango.EllipsizeMode.End);
        _label.SetMaxWidthChars(8);
        _label.SetSingleLineMode(true);
        _root.Append(_label);
    }

    public Gtk.Widget Root => _root;

    public string Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal)) return;
            _title = value;
            _label.SetText(value);
        }
    }
}
