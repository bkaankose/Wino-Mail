using System.Globalization;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// A time field (GTK has none): a menu button showing the formatted time whose popover holds an hour and a minute spin
/// button, one-minute steps as the Windows TimePicker. The hour runs 0 to 23 in both clock styles; the button text
/// follows <see cref="Formatter"/>, so a 12-hour calendar still reads its own style.
/// </summary>
public sealed class WinoTimePicker : IDisposable
{
    private readonly Gtk.MenuButton _button = Gtk.MenuButton.New();
    private readonly Gtk.SpinButton _hour = Gtk.SpinButton.NewWithRange(0, 23, 1);
    private readonly Gtk.SpinButton _minute = Gtk.SpinButton.NewWithRange(0, 59, 1);
    private readonly List<IDisposable> _subscriptions = [];
    private TimeSpan _time;
    private bool _applying;
    private bool _disposed;

    public WinoTimePicker(string accessibleLabel)
    {
        WinoLiveObjects.Track(this);
        foreach (var spin in (Gtk.SpinButton[])[_hour, _minute])
        {
            spin.SetWrap(true);
            spin.SetNumeric(true);
            spin.SetOrientation(Gtk.Orientation.Vertical);
            spin.SetWidthChars(2);
            spin.SetIncrements(1, 5);
            WinoA11y.Label(spin, accessibleLabel);
            var captured = spin;
            _subscriptions.Add(GtkSignals.Connect<Gtk.SpinButton>(h => captured.OnValueChanged += h, h => captured.OnValueChanged -= h, (_, _) => SpinChanged()));
        }
        var colon = Gtk.Label.New(":");
        colon.AddCssClass("title-3");
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        box.SetMarginTop(10);
        box.SetMarginBottom(10);
        box.SetMarginStart(10);
        box.SetMarginEnd(10);
        box.Append(_hour);
        box.Append(colon);
        box.Append(_minute);
        var popover = Gtk.Popover.New();
        popover.SetChild(box);
        _button.SetPopover(popover);
        _button.SetAlwaysShowArrow(false);
        WinoA11y.Label(_button, accessibleLabel);
        UpdateLabel();
    }

    public Gtk.Widget Root => _button;

    /// <summary>00:00 to 23:59 in whole minutes; setting it raises nothing.</summary>
    public TimeSpan Time
    {
        get => _time;
        set
        {
            var clamped = new TimeSpan(Math.Clamp(value.Hours, 0, 23), Math.Clamp(value.Minutes, 0, 59), 0);
            if (clamped == _time && _hour.GetValueAsInt() == clamped.Hours) return;
            _time = clamped;
            _applying = true;
            try
            {
                _hour.SetValue(clamped.Hours);
                _minute.SetValue(clamped.Minutes);
            }
            finally { _applying = false; }
            UpdateLabel();
        }
    }

    public Func<TimeSpan, string>? Formatter { get; set; }

    /// <summary>User changes only.</summary>
    public event EventHandler<TimeSpan>? TimeChanged;

    /// <summary>A change as the spin buttons make it, for checks without a pointer.</summary>
    public void SetFromUser(TimeSpan time)
    {
        _hour.SetValue(time.Hours);
        _minute.SetValue(time.Minutes);
    }

    public void RefreshLabel() => UpdateLabel();

    private void SpinChanged()
    {
        if (_applying || _disposed) return;
        _time = new TimeSpan(_hour.GetValueAsInt(), _minute.GetValueAsInt(), 0);
        UpdateLabel();
        TimeChanged?.Invoke(this, _time);
    }

    private void UpdateLabel()
        => _button.SetLabel(Formatter?.Invoke(_time) ?? DateTime.Today.Add(_time).ToString("t", CultureInfo.CurrentCulture));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        TimeChanged = null;
        _button.GetPopover()?.Popdown();
    }
}
