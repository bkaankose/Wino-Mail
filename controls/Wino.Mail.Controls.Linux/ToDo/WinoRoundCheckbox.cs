using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.ToDo;

/// <summary>
/// The round task check box. It is a button, so assistive technology can press it and a click never selects the list
/// row; it owns no state change: the owner sets <see cref="Checked"/> after its command ran.
/// </summary>
public sealed class WinoRoundCheckbox : IDisposable
{
    private readonly Gtk.Button _button = Gtk.Button.New();
    private readonly IDisposable _clicked;
    private bool _checked;
    private string? _label;

    public WinoRoundCheckbox()
    {
        WinoLiveObjects.Track(this);
        var mark = WinoIcons.Label(WinoIconGlyph.Checkmark, 10);
        mark.SetCanTarget(false);
        _button.SetChild(mark);
        _button.AddCssClass("flat");
        _button.AddCssClass(WinoCssClass.RoundCheck);
        _button.SetValign(Gtk.Align.Center);
        _button.SetHalign(Gtk.Align.Center);
        _clicked = GtkSignals.OnClicked(_button, () => Toggled?.Invoke());
    }

    public Gtk.Widget Root => _button;

    public Gtk.Button Button => _button;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (value) _button.AddCssClass(WinoCssClass.Checked);
            else _button.RemoveCssClass(WinoCssClass.Checked);
            WinoA11y.Checked(_button, value);
        }
    }

    public bool Enabled
    {
        get => _button.GetSensitive();
        set => _button.SetSensitive(value);
    }

    /// <summary>Accessible name and tooltip ("Mark as completed").</summary>
    public string? Label
    {
        get => _label;
        set
        {
            if (string.Equals(_label, value, StringComparison.Ordinal) || string.IsNullOrEmpty(value)) return;
            _label = value;
            WinoA11y.LabelWithTooltip(_button, value);
        }
    }

    public event Action? Toggled;

    public void Dispose()
    {
        _clicked.Dispose();
        Toggled = null;
    }
}
