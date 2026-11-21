using Wino.Mail.Controls.Core;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Shell;

/// <summary>The sidebar unread count (the Windows InfoBadge): hidden while the count is zero.</summary>
public sealed class WinoUnreadPill
{
    private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);
    private int _count = -1;

    public WinoUnreadPill()
    {
        _label.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _label.AddCssClass(WinoCssClass.SidebarPill);
        _label.AddCssClass("numeric");
        _label.SetValign(Gtk.Align.Center);
        Count = 0;
    }

    public Gtk.Widget Root => _label;

    public int Count
    {
        get => Math.Max(0, _count);
        set
        {
            if (value == _count) return;
            _count = value;
            AccessibleText = value > 0 ? WinoControlText.UnreadPill(value) : string.Empty;
            _label.SetText(AccessibleText);
            _label.SetVisible(value > 0);
        }
    }

    public string AccessibleText { get; private set; } = string.Empty;
}
