using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// One choice of a "Syncs with" pair (the AppKit WinoSourceChoiceCard, the Windows radio cards): a tile glyph, a title and a
/// wrapped description in a bordered card whose selected state draws an accent border. The card is a toggle button with the
/// radio role; only <see cref="IsSelected"/> decides how it looks, so a click raises <see cref="Clicked"/> and the owner
/// selects the card once its choice was taken (a refused change leaves the selection where it was).
/// </summary>
public sealed class WinoSourceChoiceCard : IDisposable
{
    private readonly Gtk.ToggleButton _button;
    private readonly string _title;
    private readonly IDisposable _toggled;
    private bool _isSelected;
    private bool _syncing;

    public WinoSourceChoiceCard(string title, string? description, WinoIconGlyph glyph)
    {
        _title = title;
        // The role can only be set at construction; a toggle button would otherwise read as a pressed button.
        using (var role = new GObject.Value(Gtk.AccessibleRole.Radio))
            _button = (Gtk.ToggleButton)Gtk.ToggleButton.NewWithProperties([new GObject.ConstructArgument("accessible-role", role)]);
        _button.AddCssClass("wino-source-card");
        _button.SetHexpand(true);
        _button.SetValign(Gtk.Align.Fill);

        var tile = new WinoIcon(glyph, 20).Root;
        tile.AddCssClass("wino-source-card-glyph");
        tile.SetHalign(Gtk.Align.Start);
        var heading = Gtk.Label.New(title);
        heading.AddCssClass("heading");
        heading.SetXalign(0);
        heading.SetWrap(true);
        heading.SetWrapMode(Pango.WrapMode.WordChar);
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        content.SetValign(Gtk.Align.Start);
        content.Append(tile);
        content.Append(heading);
        if (!string.IsNullOrEmpty(description))
        {
            var text = Gtk.Label.New(description);
            text.AddCssClass("dim-label");
            text.SetXalign(0);
            text.SetWrap(true);
            text.SetWrapMode(Pango.WrapMode.WordChar);
            content.Append(text);
            WinoA11y.Description(_button, description);
        }
        _button.SetChild(content);
        WinoA11y.Label(_button, title);
        WinoA11y.Checked(_button, false);

        _toggled = GtkSignals.OnToggled(_button, () =>
        {
            if (_syncing) return;
            Sync();
            Clicked?.Invoke();
        });
    }

    public Gtk.Widget Root => _button;

    /// <summary>User activation only (pointer, Space, Enter, an accessibility click).</summary>
    public event Action? Clicked;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Sync();
        }
    }

    public bool IsEnabled
    {
        get => _button.GetSensitive();
        set => _button.SetSensitive(value);
    }

    public string Describe() => $"{_title}|{(_isSelected ? "selected" : "-")}|{(IsEnabled ? "sensitive" : "insensitive")}";

    public void Dispose()
    {
        Clicked = null;
        _toggled.Dispose();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            if (_button.GetActive() != _isSelected) _button.SetActive(_isSelected);
        }
        finally { _syncing = false; }
        WinoA11y.Checked(_button, _isSelected);
    }
}
