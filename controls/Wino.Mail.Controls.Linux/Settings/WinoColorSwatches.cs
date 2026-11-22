using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// A wrapping row of round colour buttons, one radio group. <see cref="SelectionChanged"/> reports user choices only;
/// setting <see cref="SelectedHex"/> never raises it. A selected value that is not in <see cref="Colors"/> leaves no
/// button active. The owner calls <see cref="Release"/>.
/// </summary>
public sealed class WinoColorSwatches
{
    private readonly Adw.WrapBox _box;
    private readonly List<(string Hex, Gtk.ToggleButton Button)> _buttons = [];
    private readonly List<IDisposable> _subscriptions = [];
    private string? _selected;
    private bool _syncing;

    public WinoColorSwatches(string accessibleName)
    {
        _box = Adw.WrapBox.New();
        _box.SetChildSpacing(8);
        _box.SetLineSpacing(8);
        WinoA11y.Label(_box, accessibleName);
    }

    public Gtk.Widget Root => _box;

    public event Action<string>? SelectionChanged;

    public IReadOnlyList<string> Colors
    {
        set
        {
            Clear();
            Gtk.ToggleButton? first = null;
            foreach (var hex in value)
            {
                var css = WinoSwatchCss.Solid(hex);
                if (css.Length == 0) continue;
                var button = Gtk.ToggleButton.New();
                button.AddCssClass("wino-swatch");
                button.AddCssClass(css);
                button.SetSizeRequest(32, 32);
                button.SetValign(Gtk.Align.Center);
                var check = new WinoIcon(WinoIconGlyph.Checkmark, 14).Root;
                check.AddCssClass("wino-swatch-check");
                check.SetHalign(Gtk.Align.Center);
                check.SetValign(Gtk.Align.Center);
                button.SetChild(check);
                WinoA11y.Label(button, hex);
                if (first is null) first = button;
                else button.SetGroup(first);
                var current = hex;
                _subscriptions.Add(GtkSignals.OnToggled(button, () =>
                {
                    if (_syncing || !button.GetActive()) return;
                    _selected = current;
                    SelectionChanged?.Invoke(current);
                }));
                _buttons.Add((hex, button));
                _box.Append(button);
            }
            Sync();
        }
    }

    public string? SelectedHex
    {
        get => _selected;
        set
        {
            _selected = value;
            Sync();
        }
    }

    public bool IsEnabled
    {
        set => _box.SetSensitive(value);
    }

    public void Release()
    {
        Clear();
        SelectionChanged = null;
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var match = false;
            foreach (var (hex, button) in _buttons)
            {
                var active = string.Equals(hex.TrimStart('#'), _selected?.TrimStart('#'), StringComparison.OrdinalIgnoreCase);
                match |= active;
                button.SetActive(active);
            }
            // A radio group keeps one button active; with no match every button is turned off explicitly.
            if (!match)
            {
                foreach (var (_, button) in _buttons) button.SetActive(false);
            }
        }
        finally { _syncing = false; }
    }

    private void Clear()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        foreach (var (_, button) in _buttons) _box.Remove(button);
        _buttons.Clear();
    }
}
