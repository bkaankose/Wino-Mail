using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The sidebar's month picker (macOS WinoMiniCalendarView, Windows ShellCalendarView): month title with previous / next
/// month and an optional expand toggle, weekday initials and six week rows of real buttons. Today sits on an accent disc,
/// the selected dates form one band with rounded ends, and days of the neighbouring months are dimmed. Arrow keys move
/// by day and week, Page Up / Page Down by month, Home to today; a move past the grid edge pages the month.
/// </summary>
public sealed class WinoMiniCalendar : IDisposable
{
    private const byte OtherMonthBit = 1, SelectedBit = 2, StartBit = 4, EndBit = 8, TodayBit = 16;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Label _title = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _previous = Gtk.Button.New();
    private readonly Gtk.Button _next = Gtk.Button.New();
    private readonly Gtk.Button? _toggle;
    private readonly WinoIcon? _toggleIcon;
    private readonly Gtk.Grid _names = Gtk.Grid.New();
    private readonly Gtk.Grid _grid = Gtk.Grid.New();
    private readonly Gtk.Label[] _nameLabels = new Gtk.Label[WinoMiniCalendarGrid.Columns];
    private readonly Gtk.Button[] _days = new Gtk.Button[WinoMiniCalendarGrid.CellCount];
    private readonly Gtk.Label[] _dayLabels = new Gtk.Label[WinoMiniCalendarGrid.CellCount];
    private readonly byte[] _classBits = new byte[WinoMiniCalendarGrid.CellCount];
    private readonly string?[] _dayNames = new string?[WinoMiniCalendarGrid.CellCount];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly MainLoop.Coalescer _apply;
    private DateOnly[] _dates = new DateOnly[WinoMiniCalendarGrid.CellCount];
    private DateOnly _displayMonth;
    private IReadOnlySet<DateOnly> _selected = new HashSet<DateOnly>();
    private DayOfWeek _first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    private DateOnly _focused;
    private bool _collapsed;
    private bool _disposed;

    public WinoMiniCalendar(bool showToggle = true)
    {
        WinoLiveObjects.Track(this);
        _apply = new MainLoop.Coalescer(ApplyNow);
        var today = DateOnly.FromDateTime(DateTime.Today);
        _displayMonth = WinoMiniCalendarGrid.MonthStart(today);
        _focused = today;

        _root.AddCssClass("card");
        _root.AddCssClass(WinoCssClass.MiniCalendar);

        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, (int)WinoMiniCalendarGrid.HeaderButtonSpacing);
        header.SetSizeRequest(-1, (int)WinoMiniCalendarGrid.HeaderHeight);
        _title.AddCssClass(WinoCssClass.MiniTitle);
        _title.SetXalign(0);
        _title.SetHexpand(true);
        _title.SetEllipsize(Pango.EllipsizeMode.End);
        header.Append(_title);
        foreach (var (button, glyph) in ((Gtk.Button, WinoIconGlyph)[])[(_previous, WinoIconGlyph.ChevronUp), (_next, WinoIconGlyph.ChevronDown)])
        {
            button.AddCssClass("flat");
            button.AddCssClass(WinoCssClass.MiniButton);
            button.SetValign(Gtk.Align.Center);
            button.SetChild(new WinoIcon(glyph, 12, WinoIconMode.Mono).Root);
            header.Append(button);
        }
        if (showToggle)
        {
            _toggleIcon = new WinoIcon(WinoIconGlyph.PanelLeftContract, 14, WinoIconMode.Mono);
            _toggle = Gtk.Button.New();
            _toggle.AddCssClass("flat");
            _toggle.AddCssClass(WinoCssClass.MiniButton);
            _toggle.SetValign(Gtk.Align.Center);
            _toggle.SetChild(_toggleIcon.Root);
            header.Append(_toggle);
            _subscriptions.Add(GtkSignals.OnClicked(_toggle, () => ToggleClicked?.Invoke(this, EventArgs.Empty)));
        }
        _root.Append(header);

        _names.SetColumnHomogeneous(true);
        _names.SetMarginTop((int)WinoMiniCalendarGrid.NamesTopGap);
        for (var column = 0; column < WinoMiniCalendarGrid.Columns; column++)
        {
            var label = Gtk.Label.New(string.Empty);
            label.AddCssClass(WinoCssClass.MiniWeekday);
            label.SetSizeRequest(-1, (int)WinoMiniCalendarGrid.NamesHeight);
            _nameLabels[column] = label;
            _names.Attach(label, column, 0, 1, 1);
        }
        _root.Append(_names);

        _grid.SetColumnHomogeneous(true);
        for (var index = 0; index < WinoMiniCalendarGrid.CellCount; index++)
        {
            var label = Gtk.Label.New(string.Empty);
            label.AddCssClass(WinoCssClass.MiniDayLabel);
            label.SetHalign(Gtk.Align.Center);
            label.SetValign(Gtk.Align.Center);
            var button = Gtk.Button.New();
            button.AddCssClass("flat");
            button.AddCssClass(WinoCssClass.MiniDay);
            button.SetChild(label);
            _days[index] = button;
            _dayLabels[index] = label;
            _grid.Attach(button, index % WinoMiniCalendarGrid.Columns, index / WinoMiniCalendarGrid.Columns, 1, 1);
            var cell = index;
            _subscriptions.Add(GtkSignals.OnClicked(button, () => ClickDay(_dates[cell])));
        }
        _root.Append(_grid);

        _subscriptions.Add(GtkSignals.OnClicked(_previous, () => DisplayMonth = _displayMonth.AddMonths(-1)));
        _subscriptions.Add(GtkSignals.OnClicked(_next, () => DisplayMonth = _displayMonth.AddMonths(1)));
        var keys = Gtk.EventControllerKey.New();
        keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => OnKey(args.Keyval, args.State)));
        _grid.AddController(keys);
        var grid = _grid;
        _subscriptions.Add(new ActionDisposable(() => grid.RemoveController(keys)));
        ApplyNow();
    }

    public Gtk.Widget Root => _root;

    public DateOnly DisplayMonth
    {
        get => _displayMonth;
        set
        {
            var month = WinoMiniCalendarGrid.MonthStart(value);
            if (month == _displayMonth) return;
            _displayMonth = month;
            _focused = WinoMiniCalendarGrid.KeepFocusInMonth(_focused, month);
            _apply.Request();
        }
    }

    /// <summary>The band; pass a <see cref="HashSet{T}"/>.</summary>
    public IReadOnlySet<DateOnly> SelectedDates
    {
        get => _selected;
        set
        {
            _selected = value;
            _apply.Request();
        }
    }

    public DayOfWeek FirstDayOfWeek
    {
        get => _first;
        set
        {
            if (_first == value) return;
            _first = value;
            _apply.Request();
        }
    }

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            _collapsed = value;
            _names.SetVisible(!value);
            _grid.SetVisible(!value);
        }
    }

    public Func<DateOnly> Today { get; set; } = static () => DateOnly.FromDateTime(DateTime.Today);

    public DateOnly FocusedDate => _focused;

    public event EventHandler<DateOnly>? DateClicked;
    public event EventHandler? ToggleClicked;

    public void SetToggle(WinoIconGlyph glyph, string? toolTip)
    {
        if (_toggle is null || _toggleIcon is null) return;
        _toggleIcon.SetIcon(glyph);
        WinoA11y.LabelWithTooltip(_toggle, toolTip ?? string.Empty);
    }

    /// <summary>Today moved (midnight) or the culture changed.</summary>
    public void Refresh() => _apply.Request();

    /// <summary>Shows the month of <paramref name="date"/> and moves the keyboard focus to its day.</summary>
    public bool FocusDate(DateOnly date)
    {
        _focused = date;
        _displayMonth = WinoMiniCalendarGrid.MonthStart(date);
        ApplyNow();
        if (_collapsed) return false;
        var index = WinoMiniCalendarGrid.CellIndex(date, _displayMonth, _first);
        return index >= 0 && _days[index].GrabFocus();
    }

    /// <summary>The day button of <paramref name="date"/> when the shown month has it.</summary>
    public Gtk.Button? DayButton(DateOnly date)
    {
        var index = WinoMiniCalendarGrid.CellIndex(date, _displayMonth, _first);
        return index >= 0 ? _days[index] : null;
    }

    public Gtk.Button PreviousButton => _previous;

    public Gtk.Button NextButton => _next;

    public Gtk.Button? ToggleButton => _toggle;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _apply.Dispose();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        DateClicked = null;
        ToggleClicked = null;
    }

    private void ClickDay(DateOnly date)
    {
        _focused = date;
        DateClicked?.Invoke(this, date);
    }

    private bool OnKey(uint key, Gdk.ModifierType state)
    {
        if ((state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask)) != 0) return false;
        var rtl = _grid.GetDirection() == Gtk.TextDirection.Rtl;
        DateOnly? target = key switch
        {
            Gdk.Constants.KEY_Left or Gdk.Constants.KEY_KP_Left => _focused.AddDays(rtl ? 1 : -1),
            Gdk.Constants.KEY_Right or Gdk.Constants.KEY_KP_Right => _focused.AddDays(rtl ? -1 : 1),
            Gdk.Constants.KEY_Up or Gdk.Constants.KEY_KP_Up => _focused.AddDays(-7),
            Gdk.Constants.KEY_Down or Gdk.Constants.KEY_KP_Down => _focused.AddDays(7),
            Gdk.Constants.KEY_Page_Up or Gdk.Constants.KEY_KP_Page_Up => _focused.AddMonths(-1),
            Gdk.Constants.KEY_Page_Down or Gdk.Constants.KEY_KP_Page_Down => _focused.AddMonths(1),
            Gdk.Constants.KEY_Home or Gdk.Constants.KEY_KP_Home => Today(),
            _ => null
        };
        if (target is not { } date) return false;
        FocusDate(date);
        return true;
    }

    private void ApplyNow()
    {
        if (_disposed) return;
        var culture = CultureInfo.CurrentCulture;
        _title.SetText(_displayMonth.ToString("Y", culture));
        WinoA11y.LabelWithTooltip(_previous, _displayMonth.AddMonths(-1).ToString("Y", culture));
        WinoA11y.LabelWithTooltip(_next, _displayMonth.AddMonths(1).ToString("Y", culture));
        for (var column = 0; column < WinoMiniCalendarGrid.Columns; column++)
            _nameLabels[column].SetText(culture.DateTimeFormat.ShortestDayNames[WinoMiniCalendarGrid.DayNameIndex(column, _first)]);

        _dates = WinoMiniCalendarGrid.GridDates(_displayMonth, _first);
        var today = Today();
        for (var index = 0; index < _dates.Length; index++)
        {
            var date = _dates[index];
            var button = _days[index];
            _dayLabels[index].SetText(date.Day.ToString(culture));
            byte bits = 0;
            if (date.Month != _displayMonth.Month || date.Year != _displayMonth.Year) bits |= OtherMonthBit;
            if (_selected.Contains(date))
            {
                bits |= SelectedBit;
                var (leading, trailing) = WinoMiniCalendarGrid.SelectionEnds(_selected, date, index % WinoMiniCalendarGrid.Columns);
                if (leading) bits |= StartBit;
                if (trailing) bits |= EndBit;
            }
            if (date == today) bits |= TodayBit;
            var changed = (byte)(bits ^ _classBits[index]);
            if (changed != 0)
            {
                Toggle(button, changed, bits, OtherMonthBit, WinoCssClass.OtherMonth);
                Toggle(button, changed, bits, SelectedBit, WinoCssClass.Selected);
                Toggle(button, changed, bits, StartBit, WinoCssClass.RangeStart);
                Toggle(button, changed, bits, EndBit, WinoCssClass.RangeEnd);
                Toggle(button, changed, bits, TodayBit, WinoCssClass.Today);
                _classBits[index] = bits;
            }
            var name = date.ToString("D", culture);
            if (!string.Equals(_dayNames[index], name, StringComparison.Ordinal))
            {
                WinoA11y.Label(button, name);
                _dayNames[index] = name;
            }
        }
    }

    private static void Toggle(Gtk.Widget widget, byte changed, byte bits, byte bit, string cssClass)
    {
        if ((changed & bit) == 0) return;
        if ((bits & bit) != 0) widget.AddCssClass(cssClass);
        else widget.RemoveCssClass(cssClass);
    }
}
