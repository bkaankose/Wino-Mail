using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// A date picker for forms: <see cref="WinoMiniCalendar"/> in a popover, the picked day banded. A popover is parented to
/// a widget and must be disposed with its owner; an owner that forgets leaks the popover and its parent.
/// </summary>
public sealed class WinoDatePickerPopover : IDisposable
{
    private readonly WinoMiniCalendar _calendar = new(showToggle: false);
    private readonly EventHandler<DateOnly> _clicked;
    private DateOnly? _selected;
    private bool _disposed;

    public WinoDatePickerPopover(Gtk.Widget parent)
    {
        WinoLiveObjects.Track(this);
        _calendar.Root.RemoveCssClass("card");
        Popover = Gtk.Popover.New();
        Popover.SetChild(_calendar.Root);
        Popover.SetAutohide(true);
        Popover.SetPosition(Gtk.PositionType.Bottom);
        Popover.SetParent(parent);
        _calendar.Root.SetSizeRequest(260, -1);
        _clicked = (_, date) =>
        {
            SelectedDate = date;
            Popover.Popdown();
            DateSelected?.Invoke(this, date);
        };
        _calendar.DateClicked += _clicked;
    }

    public Gtk.Popover Popover { get; }

    public WinoMiniCalendar Calendar => _calendar;

    public DateOnly? SelectedDate
    {
        get => _selected;
        set
        {
            _selected = value;
            _calendar.SelectedDates = value is { } date ? new HashSet<DateOnly> { date } : new HashSet<DateOnly>();
            _calendar.DisplayMonth = value ?? _calendar.Today();
        }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _calendar.FirstDayOfWeek;
        set => _calendar.FirstDayOfWeek = value;
    }

    public event EventHandler<DateOnly>? DateSelected;

    public void Popup()
    {
        if (_disposed) return;
        Popover.Popup();
        var target = _selected ?? _calendar.Today();
        MainLoop.Post(() =>
        {
            if (!_disposed) _calendar.FocusDate(target);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _calendar.DateClicked -= _clicked;
        DateSelected = null;
        Popover.Popdown();
        Popover.Unparent();
        _calendar.Dispose();
    }
}
