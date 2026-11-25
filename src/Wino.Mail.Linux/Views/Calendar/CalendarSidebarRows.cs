using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.MenuItems;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Mail.ViewModels.Shell.Contracts;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The calendar mode's own sidebar rows (macOS ShellDatePickerCell and ShellCalendarGroupCell, Windows
/// AccountCalendarGroupTemplate): the date picker, one account group with its calendars, and an ungrouped calendar.
/// The shell asks for a row each time it repaints one, so a row's controller lives until the cell lets it go.
/// </summary>
internal sealed class CalendarSidebarRowProvider : IShellSidebarRowProvider, IDisposable
{
    private readonly Action<Exception> _error;
    private readonly IPreferencesService _preferences;
    private readonly Func<DayOfWeek> _firstDayOfWeek;
    private readonly EventHandler<string> _preferenceChanged;
    private readonly Dictionary<IMenuItem, ICalendarSidebarRow> _byItem = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Gtk.Widget, IMenuItem> _byWidget = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public CalendarSidebarRowProvider(IPreferencesService preferences, Action<Exception> error)
    {
        _preferences = preferences;
        _firstDayOfWeek = () => preferences.FirstDayOfWeek;
        _error = error;
        _preferenceChanged = (_, name) =>
        {
            if (name == nameof(IPreferencesService.FirstDayOfWeek)) MainLoop.Post(RefreshDatePickers);
        };
        preferences.PreferenceChanged += _preferenceChanged;
    }

    private void RefreshDatePickers()
    {
        if (_disposed) return;
        foreach (var row in _byItem.Values.OfType<CalendarDatePickerRow>()) row.Refresh();
    }

    public Gtk.Widget? CreateOrUpdate(IMenuItem item, Gtk.Widget? existing)
    {
        if (_disposed || item is not (CalendarDatePickerMenuItem or AccountCalendarGroupMenuItem or UngroupedCalendarMenuItem)) return null;
        if (!_byItem.TryGetValue(item, out var row))
        {
            row = item switch
            {
                CalendarDatePickerMenuItem picker => new CalendarDatePickerRow(picker, _firstDayOfWeek, _error),
                AccountCalendarGroupMenuItem group => new CalendarGroupRow(group),
                UngroupedCalendarMenuItem ungrouped => new CalendarUngroupedRow(ungrouped),
                _ => throw new InvalidOperationException()
            };
            _byItem[item] = row;
            _byWidget[row.Root] = item;
        }
        else row.Refresh();
        return row.Root;
    }

    public void Release(Gtk.Widget widget)
    {
        if (widget.GetParent() is not null) return;
        if (!_byWidget.Remove(widget, out var item) || !_byItem.Remove(item, out var row)) return;
        row.Dispose();
    }

    /// <summary>The calendar a row widget shows at a point of <paramref name="rowRoot"/>; null on a header.</summary>
    internal AccountCalendarViewModel? CalendarAt(Gtk.Widget rowRoot, double x, double y)
    {
        if (!_byWidget.TryGetValue(rowRoot, out var item) || !_byItem.TryGetValue(item, out var row)) return null;
        for (var widget = rowRoot.Pick(x, y, Gtk.PickFlags.Default); widget is not null; widget = widget.GetParent())
        {
            if (row.CalendarOf(widget) is { } calendar) return calendar;
            if (ReferenceEquals(widget, rowRoot)) break;
        }
        return null;
    }

#if DEBUG
    internal IEnumerable<ICalendarSidebarRow> Rows => _byItem.Values;
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preferences.PreferenceChanged -= _preferenceChanged;
        foreach (var row in _byItem.Values) row.Dispose();
        _byItem.Clear();
        _byWidget.Clear();
    }
}

internal interface ICalendarSidebarRow : IDisposable
{
    Gtk.Widget Root { get; }
    void Refresh();
    AccountCalendarViewModel? CalendarOf(Gtk.Widget widget);
}

/// <summary>A 12 px colour disc for a calendar or an account (a generated fill class, no provider per dot).</summary>
internal sealed class CalendarColorDot
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private string? _fill;

    public CalendarColorDot()
    {
        _root.AddCssClass(WinoCssClass.ColorDot);
        _root.SetValign(Gtk.Align.Center);
        _root.SetHalign(Gtk.Align.Center);
    }

    public Gtk.Widget Root => _root;

    public void SetColor(string? hex, string? fallbackName)
    {
        var color = WinoRgba.FromHexString(hex) is not null ? hex
            : fallbackName is not null ? WinoTokens.AvatarPalette[WinoTokens.AvatarPaletteIndex(fallbackName)]
            : AccentHex();
        var next = WinoTint.FillClassFor(color, null);
        if (string.Equals(next, _fill, StringComparison.Ordinal)) return;
        if (_fill is not null) _root.RemoveCssClass(_fill);
        if (next is not null) _root.AddCssClass(next);
        _fill = next;
    }

    private static string AccentHex()
    {
        if (WinoAppearance.Accent is { } accent) return $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        var rgba = Adw.StyleManager.GetDefault().GetAccentColorRgba();
        return $"#{(int)(rgba.Red * 255):X2}{(int)(rgba.Green * 255):X2}{(int)(rgba.Blue * 255):X2}";
    }
}

/// <summary>One calendar: check box, colour dot and name. The check box carries the calendar's name for assistive technology.</summary>
internal sealed class CalendarCheckRow : IDisposable
{
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly Gtk.CheckButton _check = Gtk.CheckButton.New();
    private readonly CalendarColorDot _dot = new();
    private readonly Gtk.Label _name = Gtk.Label.New(string.Empty);
    private readonly IDisposable _toggled;
    private readonly PropertyChangedEventHandler _changed;
    private bool _applying;

    public CalendarCheckRow(AccountCalendarViewModel calendar, int marginStart)
    {
        Calendar = calendar;
        _root.SetSizeRequest(-1, 30);
        _root.SetMarginStart(marginStart);
        _root.SetMarginEnd(10);
        _name.SetXalign(0);
        _name.SetHexpand(true);
        _name.SetEllipsize(Pango.EllipsizeMode.End);
        _root.Append(_check);
        _root.Append(_dot.Root);
        _root.Append(_name);
        _toggled = GtkSignals.OnToggled(_check, () =>
        {
            if (!_applying && Calendar.IsChecked != _check.GetActive()) Calendar.IsChecked = _check.GetActive();
        });
        _changed = (_, args) =>
        {
            if (args.PropertyName is null or "" or nameof(AccountCalendarViewModel.IsChecked) or nameof(AccountCalendarViewModel.Name)
                or nameof(AccountCalendarViewModel.BackgroundColorHex)) MainLoop.Post(Apply);
        };
        calendar.PropertyChanged += _changed;
        Apply();
    }

    public AccountCalendarViewModel Calendar { get; }

    public Gtk.Widget Root => _root;

    public bool IsChecked => _check.GetActive();

    private bool _disposed;

    public void Apply()
    {
        if (_disposed) return;
        _applying = true;
        try
        {
            _check.SetActive(Calendar.IsChecked);
            var name = Calendar.Name ?? string.Empty;
            _name.SetText(name);
            WinoA11y.Label(_check, name);
            _dot.SetColor(Calendar.BackgroundColorHex, null);
        }
        finally { _applying = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toggled.Dispose();
        Calendar.PropertyChanged -= _changed;
    }
}

/// <summary>
/// One account's calendars: a header with a three-state check box, the account colour, name and address (or the sync
/// status and a 2 px progress bar while syncing) and the chevron; then a check row per calendar while expanded.
/// </summary>
internal sealed class CalendarGroupRow : ICalendarSidebarRow
{
    private readonly AccountCalendarGroupMenuItem _item;
    private readonly GroupedAccountCalendarViewModel _group;
    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.CheckButton _check = Gtk.CheckButton.New();
    private readonly CalendarColorDot _dot = new();
    private readonly Gtk.Button _toggle = Gtk.Button.New();
    private readonly Gtk.Label _name = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _address = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _status = Gtk.Label.New(string.Empty);
    private readonly Gtk.ProgressBar _progress = Gtk.ProgressBar.New();
    private readonly WinoIcon _chevron = new(WinoIconGlyph.ChevronDown, 10, WinoIconMode.Mono);
    private readonly Gtk.Box _calendars = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly List<CalendarCheckRow> _rows = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly PropertyChangedEventHandler _groupChanged;
    private readonly NotifyCollectionChangedEventHandler _calendarsChanged;
    private IDisposable? _pulse;
    private bool _applying;
    private bool _disposed;

    public CalendarGroupRow(AccountCalendarGroupMenuItem item)
    {
        _item = item;
        _group = item.Parameter;
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        header.SetSizeRequest(-1, 46);
        header.SetMarginStart(14);
        header.SetMarginEnd(4);
        _check.SetValign(Gtk.Align.Center);
        header.Append(_check);
        header.Append(_dot.Root);

        _name.AddCssClass(WinoCssClass.CalendarGroupName);
        foreach (var label in (Gtk.Label[])[_name, _address, _status])
        {
            label.SetXalign(0);
            label.SetEllipsize(Pango.EllipsizeMode.End);
        }
        _address.AddCssClass("caption");
        _address.AddCssClass("dim-label");
        _status.AddCssClass("caption");
        _status.AddCssClass("dim-label");
        _progress.AddCssClass(WinoCssClass.CalendarSyncBar);
        _progress.SetMarginTop(3);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.Append(_name);
        text.Append(_address);
        text.Append(_status);
        text.Append(_progress);
        var toggleContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        toggleContent.Append(text);
        _chevron.Root.SetValign(Gtk.Align.Center);
        toggleContent.Append(_chevron.Root);
        _toggle.SetChild(toggleContent);
        _toggle.AddCssClass("flat");
        _toggle.SetHexpand(true);
        header.Append(_toggle);

        _root.Append(header);
        _root.Append(_calendars);

        _subscriptions.Add(GtkSignals.OnToggled(_check, OnCheckToggled));
        _subscriptions.Add(GtkSignals.OnClicked(_toggle, () => _group.IsExpanded = !_group.IsExpanded));
        _groupChanged = (_, _) => MainLoop.Post(Apply);
        _calendarsChanged = (_, _) => MainLoop.Post(Apply);
        _group.PropertyChanged += _groupChanged;
        _group.AccountCalendars.CollectionChanged += _calendarsChanged;
        Apply();
    }

    public Gtk.Widget Root => _root;

    public GroupedAccountCalendarViewModel Group => _group;

    public bool IsInconsistent => _check.GetInconsistent();

    public bool IsChecked => _check.GetActive();

    public void Refresh() => Apply();

    public AccountCalendarViewModel? CalendarOf(Gtk.Widget widget)
    {
        foreach (var row in _rows) if (ReferenceEquals(row.Root, widget)) return row.Calendar;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _group.PropertyChanged -= _groupChanged;
        _group.AccountCalendars.CollectionChanged -= _calendarsChanged;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _pulse?.Dispose();
        _pulse = null;
        ClearRows();
    }

    private void OnCheckToggled()
    {
        if (_applying) return;
        _check.SetInconsistent(false);
        _group.IsCheckedState = _check.GetActive();
    }

    private void Apply()
    {
        if (_disposed) return;
        _applying = true;
        try
        {
            _check.SetInconsistent(_group.IsCheckedState is null);
            _check.SetActive(_group.IsCheckedState == true);
            var name = _group.Account?.Name ?? string.Empty;
            WinoA11y.Label(_check, name);
            WinoA11y.Rename(_toggle, name);
            _dot.SetColor(_group.AccountColorHex, name);
            _name.SetText(name);
            _address.SetText(_group.Account?.DisplayAddress ?? string.Empty);
            _status.SetText(_group.SynchronizationStatus ?? string.Empty);
            var syncing = _group.IsSynchronizationProgressVisible;
            _status.SetVisible(syncing && _status.GetText().Length > 0);
            _address.SetVisible(!_status.GetVisible() && _address.GetText().Length > 0);
            _progress.SetVisible(syncing);
            if (syncing && _group.IsProgressIndeterminate)
            {
                _pulse ??= Pulse();
            }
            else
            {
                _pulse?.Dispose();
                _pulse = null;
                if (syncing) _progress.SetFraction(Math.Clamp(_group.SynchronizationProgressValue / 100, 0, 1));
            }
            _chevron.SetIcon(_group.IsExpanded ? WinoIconGlyph.ChevronDown : WinoIconGlyph.ChevronRight);
            if (!RowsMatch()) RebuildRows();
        }
        finally { _applying = false; }
    }

    private IDisposable Pulse()
    {
        var progress = _progress;
        var source = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 120, () =>
        {
            progress.Pulse();
            return true;
        });
        return new ActionDisposable(() => GLib.Functions.SourceRemove(source));
    }

    private bool RowsMatch()
    {
        if (!_group.IsExpanded) return _rows.Count == 0;
        var calendars = _group.AccountCalendars;
        if (_rows.Count != calendars.Count) return false;
        for (var index = 0; index < _rows.Count; index++)
            if (!ReferenceEquals(_rows[index].Calendar, calendars[index])) return false;
        return true;
    }

    private void RebuildRows()
    {
        ClearRows();
        if (!_group.IsExpanded) return;
        foreach (var calendar in _group.AccountCalendars)
        {
            var row = new CalendarCheckRow(calendar, 38);
            _calendars.Append(row.Root);
            _rows.Add(row);
        }
    }

    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            _calendars.Remove(row.Root);
            row.Dispose();
        }
        _rows.Clear();
    }

#if DEBUG
    internal IReadOnlyList<CalendarCheckRow> CalendarRows => _rows;
#endif
}

/// <summary>An ungrouped calendar: the calendar row of a group, at the header's indent.</summary>
internal sealed class CalendarUngroupedRow : ICalendarSidebarRow
{
    private readonly UngroupedCalendarMenuItem _item;
    private CalendarCheckRow _row;

    public CalendarUngroupedRow(UngroupedCalendarMenuItem item)
    {
        _item = item;
        _row = new CalendarCheckRow(item.Parameter, 14);
        Root = Adw.Bin.New();
        ((Adw.Bin)Root).SetChild(_row.Root);
    }

    public Gtk.Widget Root { get; }

    public void Refresh()
    {
        if (ReferenceEquals(_row.Calendar, _item.Parameter))
        {
            _row.Apply();
            return;
        }
        _row.Dispose();
        _row = new CalendarCheckRow(_item.Parameter, 14);
        ((Adw.Bin)Root).SetChild(_row.Root);
    }

    public AccountCalendarViewModel? CalendarOf(Gtk.Widget widget) => ReferenceEquals(widget, _row.Root) ? _row.Calendar : null;

    public void Dispose() => _row.Dispose();
}

/// <summary>
/// The date picker row (macOS ShellDatePickerCell): the mini calendar bound to the calendar shell. The band is the
/// visible range, a click moves the surface, the toggle collapses the grid and the item persists that.
/// </summary>
internal sealed partial class CalendarDatePickerRow : ICalendarSidebarRow
{
    private readonly CalendarDatePickerMenuItem _item;
    private readonly Adw.Bin _root = Adw.Bin.New();
    private readonly WinoMiniCalendar _calendar = new();
    private readonly Func<DayOfWeek> _firstDayOfWeek;
    private readonly Action<Exception> _error;
    private readonly EventHandler<DateOnly> _dateClicked;
    private readonly EventHandler _toggleClicked;
    private readonly PropertyChangedEventHandler _itemChanged;
    private readonly PropertyChangedEventHandler _clientChanged;
    private readonly ICalendarShellClient _client;
    private bool _disposed;

    public CalendarDatePickerRow(CalendarDatePickerMenuItem item, Func<DayOfWeek> firstDayOfWeek, Action<Exception> error)
    {
        _item = item;
        _client = item.Parameter;
        _firstDayOfWeek = firstDayOfWeek;
        _error = error;
        _root.SetMarginTop(2);
        _root.SetMarginStart(12);
        _root.SetMarginBottom(8);
        _root.SetMarginEnd(12);
        _root.SetChild(_calendar.Root);
        _dateClicked = (_, date) => DateClicked(date);
        _toggleClicked = (_, _) => _item.IsCalendarExpanded = !_item.IsCalendarExpanded;
        _calendar.DateClicked += _dateClicked;
        _calendar.ToggleClicked += _toggleClicked;
        _itemChanged = (_, _) => MainLoop.Post(Apply);
        _clientChanged = (_, args) =>
        {
            if (args.PropertyName is null or "" or nameof(ICalendarShellClient.CurrentVisibleRange) or nameof(ICalendarShellClient.VisibleDateRangeText)) MainLoop.Post(Synchronize);
        };
        _item.PropertyChanged += _itemChanged;
        _client.PropertyChanged += _clientChanged;
        Apply();
        RegisterDebugVerbs();
    }

    public Gtk.Widget Root => _root;

    public WinoMiniCalendar Calendar => _calendar;

    public void Refresh() => Apply();

    public AccountCalendarViewModel? CalendarOf(Gtk.Widget widget) => null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _item.PropertyChanged -= _itemChanged;
        _client.PropertyChanged -= _clientChanged;
        _calendar.DateClicked -= _dateClicked;
        _calendar.ToggleClicked -= _toggleClicked;
        DisposeDebugVerbs();
        _calendar.Dispose();
    }

    partial void RegisterDebugVerbs();
    partial void DisposeDebugVerbs();

    private void Apply()
    {
        if (_disposed) return;
        _calendar.FirstDayOfWeek = _firstDayOfWeek();
        _calendar.Collapsed = !_item.IsCalendarExpanded;
        _calendar.SetToggle(_item.IsCalendarExpanded ? WinoIconGlyph.PanelLeftContract : WinoIconGlyph.PanelLeftExpand, _item.ExpansionToolTip);
        Synchronize();
    }

    private void Synchronize()
    {
        if (_disposed) return;
        if (_client.CurrentVisibleRange is not { } range)
        {
            _calendar.SelectedDates = new HashSet<DateOnly>();
            return;
        }
        _calendar.SelectedDates = new HashSet<DateOnly>(range.Dates);
        _calendar.DisplayMonth = range.AnchorDate;
    }

    private void DateClicked(DateOnly date)
    {
        try
        {
            var args = new CalendarViewDayClickedEventArgs(date.ToDateTime(TimeOnly.MinValue));
            if (_client.DateClickedCommand.CanExecute(args)) _client.DateClickedCommand.Execute(args);
        }
        catch (Exception exception) { _error(exception); }
    }
}
