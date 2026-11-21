using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Compose;

/// <summary>One recipient in a <see cref="WinoRecipientTokenBox"/>: its name (or address) and a remove button.</summary>
public sealed class WinoRecipientChip : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Button _text;
    private readonly Gtk.Button _remove;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _selected;

    public WinoRecipientChip(string address, string? displayName)
    {
        WinoLiveObjects.Track(this);
        Address = address;
        DisplayName = string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName.Trim(), address, StringComparison.OrdinalIgnoreCase) ? null : displayName.Trim();
        _root.AddCssClass(WinoCssClass.RecipientChip);
        _root.SetValign(Gtk.Align.Center);

        var label = Gtk.Label.New(DisplayName ?? address);
        label.SetEllipsize(Pango.EllipsizeMode.Middle);
        label.SetMaxWidthChars(32);
        _text = Gtk.Button.New();
        _text.SetChild(label);
        _text.AddCssClass("flat");
        _text.AddCssClass(WinoCssClass.RecipientChipText);
        _text.SetFocusOnClick(false);
        _text.SetTooltipText(DisplayName is null ? address : $"{DisplayName} <{address}>");
        WinoA11y.Rename(_text, DisplayName is null ? address : $"{DisplayName} {address}");

        _remove = WinoIcons.Button(WinoIconGlyph.Dismiss, $"{Translator.Buttons_Remove} {address}", 12);
        _remove.AddCssClass(WinoCssClass.RecipientChipRemove);
        _remove.SetFocusOnClick(false);
        _remove.SetValign(Gtk.Align.Center);

        _root.Append(_text);
        _root.Append(_remove);
        _subscriptions.Add(GtkSignals.OnClicked(_remove, () => RemoveRequested?.Invoke(this)));
        _subscriptions.Add(GtkSignals.OnClicked(_text, () => Activated?.Invoke(this)));
        _subscriptions.Add(ContextMenu.AttachRequest(_root, (x, y) => MenuRequested?.Invoke(this, x, y)));
    }

    public Gtk.Widget Root => _root;
    public string Address { get; }
    public string? DisplayName { get; }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            if (value) _root.AddCssClass("selected");
            else _root.RemoveCssClass("selected");
        }
    }

    public event Action<WinoRecipientChip>? RemoveRequested;
    public event Action<WinoRecipientChip>? Activated;
    public event Action<WinoRecipientChip, double?, double?>? MenuRequested;

    internal void Own(IDisposable subscription) => _subscriptions.Add(subscription);

    public void Dispose()
    {
        RemoveRequested = null;
        Activated = null;
        MenuRequested = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }
}
