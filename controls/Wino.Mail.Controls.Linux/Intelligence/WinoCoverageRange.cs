using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>
/// The date-range selector under the coverage histogram: two sliders (newest edge, oldest edge) clamped against each
/// other. GTK sliders carry their value, keyboard steps and the slider role to assistive technology, which a drawn
/// two-thumb control would have to rebuild. <see cref="Changed"/> fires only for user changes.
/// </summary>
public sealed class WinoCoverageRange : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Gtk.Scale _start;
    private readonly Gtk.Scale _end;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _applying;

    public WinoCoverageRange()
    {
        WinoLiveObjects.Track(this);
        _start = Slider();
        _end = Slider();
        _root.Append(_start);
        _root.Append(_end);
        _subscriptions.Add(GtkSignals.Connect<Gtk.Range>(h => _start.OnValueChanged += h, h => _start.OnValueChanged -= h, (_, _) => UserChanged(start: true)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Range>(h => _end.OnValueChanged += h, h => _end.OnValueChanged -= h, (_, _) => UserChanged(start: false)));
    }

    public Gtk.Widget Root => _root;

    public int Start => (int)Math.Round(_start.GetValue());

    public int End => (int)Math.Round(_end.GetValue());

    public event Action<int, int>? Changed;

    /// <summary>Sets the range without raising <see cref="Changed"/>.</summary>
    public void SetRange(int maximum, int start, int end)
    {
        _applying = true;
        try
        {
            var upper = Math.Max(1, maximum);
            foreach (var scale in (Gtk.Scale[])[_start, _end])
            {
                scale.SetRange(0, upper);
                scale.SetIncrements(1, Math.Max(1, Math.Round(upper / 10.0)));
            }
            _start.SetValue(Math.Clamp(start, 0, upper));
            _end.SetValue(Math.Clamp(end, 0, upper));
        }
        finally { _applying = false; }
    }

    public void SetAccessibleNames(string start, string end)
    {
        WinoA11y.LabelWithTooltip(_start, start);
        WinoA11y.LabelWithTooltip(_end, end);
    }

    public void Dispose()
    {
        Changed = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private static Gtk.Scale Slider()
    {
        var scale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 0, 1, 1);
        scale.SetDrawValue(false);
        scale.SetHexpand(true);
        scale.SetDigits(0);
        return scale;
    }

    private void UserChanged(bool start)
    {
        if (_applying) return;
        _applying = true;
        try
        {
            if (start && _start.GetValue() > _end.GetValue()) _end.SetValue(_start.GetValue());
            if (!start && _end.GetValue() < _start.GetValue()) _start.SetValue(_end.GetValue());
        }
        finally { _applying = false; }
        Changed?.Invoke(Start, End);
    }
}
