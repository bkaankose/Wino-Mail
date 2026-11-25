using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.ToDo;

/// <summary>
/// A filter-row chip (Windows DropDownButton / ToggleButton in the To Do filter row): glyph, optional label and, for a
/// menu chip, a drop-down arrow. A click raises <see cref="Clicked"/>; a menu chip's owner opens its menu on
/// <see cref="Root"/> through <c>ContextMenu.Show</c>.
/// </summary>
public sealed class WinoChipButton : IDisposable
{
    private readonly Gtk.Button _button = Gtk.Button.New();
    private readonly Gtk.Label _glyph;
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly IDisposable _clicked;
    private WinoIconGlyph _icon;
    private bool _checked;

    public WinoChipButton(WinoIconGlyph glyph, string? title, string accessibleLabel, bool hasMenu = false)
    {
        WinoLiveObjects.Track(this);
        _icon = glyph;
        HasMenu = hasMenu;
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        _glyph = WinoIcons.Label(glyph, 14);
        content.Append(_glyph);
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        content.Append(_title);
        if (hasMenu)
        {
            var arrow = Gtk.Image.NewFromIconName("pan-down-symbolic");
            arrow.SetCanTarget(false);
            content.Append(arrow);
            WinoA11y.HasPopup(_button);
        }
        _button.SetChild(content);
        _button.AddCssClass(WinoCssClass.ChipButton);
        _button.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(_button, accessibleLabel);
        Title = title;
        _clicked = GtkSignals.OnClicked(_button, () => Clicked?.Invoke());
    }

    public Gtk.Widget Root => _button;

    public Gtk.Button Button => _button;

    public bool HasMenu { get; }

    public string? Title
    {
        get => _title.GetText();
        set
        {
            var text = value ?? string.Empty;
            if (!string.Equals(_title.GetText(), text, StringComparison.Ordinal)) _title.SetText(text);
            _title.SetVisible(text.Length > 0);
        }
    }

    public WinoIconGlyph Glyph
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            WinoIcons.SetGlyph(_glyph, WinoIcons.Glyph(value));
        }
    }

    /// <summary>The Important toggle's on state.</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (value) _button.AddCssClass(WinoCssClass.ChipChecked);
            else _button.RemoveCssClass(WinoCssClass.ChipChecked);
            WinoA11y.Checked(_button, value);
        }
    }

    public bool Enabled
    {
        get => _button.GetSensitive();
        set => _button.SetSensitive(value);
    }

    public bool Visible
    {
        get => _button.GetVisible();
        set => _button.SetVisible(value);
    }

    public event Action? Clicked;

    public void Dispose()
    {
        _clicked.Dispose();
        Clicked = null;
    }
}
