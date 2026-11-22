using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The calendar page's period row (macOS CalendarToolbarView, Windows CalendarTitleBarContent): previous and next
/// period, Today, the range title and the Day / Week / Work week / Month switcher, which becomes a drop-down on a
/// narrow page.
/// </summary>
internal sealed class CalendarToolbar : IDisposable
{
    private static readonly CalendarDisplayType[] DisplayTypes = [CalendarDisplayType.Day, CalendarDisplayType.Week, CalendarDisplayType.WorkWeek, CalendarDisplayType.Month];

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Button _previous = Gtk.Button.New();
    private readonly Gtk.Button _next = Gtk.Button.New();
    private readonly Gtk.Button _today = Gtk.Button.NewWithLabel(Translator.Today);
    private readonly Gtk.Label _range = Gtk.Label.New(string.Empty);
    private readonly Adw.ToggleGroup _types = Adw.ToggleGroup.New();
    private readonly Gtk.DropDown _typesDropDown;
    private readonly Gtk.Widget _previousIcon;
    private readonly Gtk.Widget _nextIcon;
    private readonly List<IDisposable> _subscriptions = [];
    private CalendarDisplayType _selected = CalendarDisplayType.Week;
    private bool _applying;

    public CalendarToolbar()
    {
        _root.AddCssClass(WinoCssClass.CalendarToolbar);
        _root.SetMarginTop(4);
        _root.SetMarginBottom(8);
        _root.SetMarginStart(7);
        _root.SetMarginEnd(7);

        _previousIcon = new WinoIcon(WinoIconGlyph.ArrowLeft, 12, WinoIconMode.Mono).Root;
        _nextIcon = new WinoIcon(WinoIconGlyph.ArrowRight, 12, WinoIconMode.Mono).Root;
        _previousIcon.AddCssClass(WinoCssClass.CalendarArrow);
        _nextIcon.AddCssClass(WinoCssClass.CalendarArrow);
        _previous.SetChild(_previousIcon);
        _next.SetChild(_nextIcon);
        WinoA11y.LabelWithTooltip(_previous, Translator.Accessibility_PreviousCalendarPeriod);
        WinoA11y.LabelWithTooltip(_next, Translator.Accessibility_NextCalendarPeriod);
        var arrows = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        arrows.AddCssClass("linked");
        arrows.Append(_previous);
        arrows.Append(_next);
        arrows.SetValign(Gtk.Align.Center);
        _today.SetValign(Gtk.Align.Center);

        _range.AddCssClass(WinoCssClass.CalendarRange);
        _range.SetXalign(0);
        _range.SetEllipsize(Pango.EllipsizeMode.End);
        _range.SetHexpand(true);
        _range.SetWidthChars(4);
        _range.SetSelectable(false);

        foreach (var type in DisplayTypes)
        {
            var toggle = Adw.Toggle.New();
            toggle.SetName(type.ToString());
            toggle.SetLabel(Title(type));
            _types.Add(toggle);
        }
        _types.SetValign(Gtk.Align.Center);
        WinoA11y.Label(_types, Translator.KeyboardShortcuts_ModeCalendar);
        _typesDropDown = Gtk.DropDown.NewFromStrings([.. DisplayTypes.Select(Title)]);
        _typesDropDown.SetValign(Gtk.Align.Center);
        _typesDropDown.SetVisible(false);
        WinoA11y.Label(_typesDropDown, Translator.KeyboardShortcuts_ModeCalendar);

        _root.Append(arrows);
        _root.Append(_today);
        _root.Append(_range);
        _root.Append(_types);
        _root.Append(_typesDropDown);

        _subscriptions.Add(GtkSignals.OnClicked(_previous, () => PreviousRequested?.Invoke(this, EventArgs.Empty)));
        _subscriptions.Add(GtkSignals.OnClicked(_next, () => NextRequested?.Invoke(this, EventArgs.Empty)));
        _subscriptions.Add(GtkSignals.OnClicked(_today, () => TodayRequested?.Invoke(this, EventArgs.Empty)));
        _subscriptions.Add(GtkSignals.OnNotify(_types, "active-name", () =>
        {
            if (!_applying && Enum.TryParse<CalendarDisplayType>(_types.GetActiveName(), out var type)) Request(type);
        }));
        _subscriptions.Add(GtkSignals.OnNotify(_typesDropDown, "selected", () =>
        {
            var index = _typesDropDown.GetSelected();
            if (!_applying && index < DisplayTypes.Length) Request(DisplayTypes[index]);
        }));
        SelectedType = _selected;
    }

    public Gtk.Widget Root => _root;

    public string RangeText
    {
        get => _range.GetText();
        set => _range.SetText(value ?? string.Empty);
    }

    public CalendarDisplayType SelectedType
    {
        get => _selected;
        set
        {
            _selected = value;
            _applying = true;
            try
            {
                _types.SetActiveName(value.ToString());
                var index = Array.IndexOf(DisplayTypes, value);
                if (index >= 0) _typesDropDown.SetSelected((uint)index);
            }
            finally { _applying = false; }
            foreach (var icon in (Gtk.Widget[])[_previousIcon, _nextIcon])
            {
                if (value == CalendarDisplayType.Month) icon.AddCssClass(WinoCssClass.CalendarArrowMonth);
                else icon.RemoveCssClass(WinoCssClass.CalendarArrowMonth);
            }
        }
    }

    public bool IsCompact { get; private set; }

    public void SetCompact(bool compact)
    {
        IsCompact = compact;
        _types.SetVisible(!compact);
        _typesDropDown.SetVisible(compact);
    }

    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? TodayRequested;
    public event EventHandler<CalendarDisplayType>? DisplayTypeRequested;

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        PreviousRequested = null;
        NextRequested = null;
        TodayRequested = null;
        DisplayTypeRequested = null;
    }

    private void Request(CalendarDisplayType type)
    {
        if (type == _selected) return;
        DisplayTypeRequested?.Invoke(this, type);
    }

    internal static string Title(CalendarDisplayType type) => type switch
    {
        CalendarDisplayType.Day => Translator.CalendarTypeSelector_Day,
        CalendarDisplayType.WorkWeek => Translator.CalendarTypeSelector_WorkWeek,
        CalendarDisplayType.Month => Translator.CalendarTypeSelector_Month,
        _ => Translator.CalendarTypeSelector_Week
    };
}
