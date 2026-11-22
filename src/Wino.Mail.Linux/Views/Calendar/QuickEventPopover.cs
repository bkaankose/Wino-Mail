using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The quick event popover over an empty slot or a dragged range (macOS QuickEventPopover, Windows quick event popup):
/// a header in the calendar colour, event name and All day, start and end time, the range, location, More details and
/// Save. Everything binds to the page ViewModel's quick event state.
/// </summary>
internal sealed class QuickEventPopover : IDisposable
{
    private const int ContentWidth = 340;

    private readonly CalendarPageViewModel _viewModel;
    private readonly BindingScope _bindings = new();
    private readonly Gtk.Popover _popover = Gtk.Popover.New();
    private readonly Gtk.Box _header = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
    private readonly Gtk.Label _accountName = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _calendarName = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _readOnlyNote = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Entry _name = Gtk.Entry.New();
    private readonly Gtk.CheckButton _allDay = Gtk.CheckButton.NewWithLabel(Translator.QuickEventDialog_IsAllDay);
    private readonly Gtk.Box _timeRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Label _range = Gtk.Label.New(string.Empty);
    private readonly Gtk.Entry _location = Gtk.Entry.New();
    private readonly Gtk.Button _more = Gtk.Button.NewWithLabel(Translator.QuickEventDialogMoreDetailsButtonText);
    private readonly Gtk.Button _save = Gtk.Button.NewWithLabel(Translator.Buttons_Save);
    private readonly CommandBinding _saveBinding;
    private string? _headerFill;
    private bool _closedByUs;
    private bool _disposed;

    public QuickEventPopover(CalendarPageViewModel viewModel, IDispatcher dispatcher, Action<Exception> error)
    {
        WinoLiveObjects.Track(this);
        _viewModel = viewModel;
        var bind = new GtkBinder<CalendarPageViewModel>(viewModel, _bindings, dispatcher, error);

        _header.SetSizeRequest(-1, 36);
        _header.SetMarginBottom(0);
        _accountName.SetXalign(0);
        _accountName.SetEllipsize(Pango.EllipsizeMode.End);
        _accountName.AddCssClass("caption");
        _accountName.SetOpacity(0.85);
        _calendarName.SetXalign(0);
        _calendarName.SetEllipsize(Pango.EllipsizeMode.End);
        _calendarName.AddCssClass("heading");
        _calendarName.SetHexpand(true);
        var headerContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        headerContent.SetMarginStart(12);
        headerContent.SetMarginEnd(12);
        headerContent.SetValign(Gtk.Align.Center);
        headerContent.SetHexpand(true);
        headerContent.Append(_accountName);
        headerContent.Append(_calendarName);
        _header.Append(headerContent);
        _header.AddCssClass("wino-quick-event-header");

        var warning = WinoIcons.Label(WinoIconGlyph.Warning, 14);
        warning.AddCssClass("warning");
        warning.SetValign(Gtk.Align.Start);
        var readOnlyText = Gtk.Label.New($"{Translator.CalendarReadOnly_Title}. {Translator.CalendarReadOnly_Message}");
        readOnlyText.SetWrap(true);
        readOnlyText.SetXalign(0);
        readOnlyText.SetMaxWidthChars(40);
        readOnlyText.AddCssClass("caption");
        readOnlyText.AddCssClass("dim-label");
        _readOnlyNote.Append(warning);
        _readOnlyNote.Append(readOnlyText);
        _readOnlyNote.SetVisible(false);

        _name.SetPlaceholderText(Translator.QuickEventDialog_EventName);
        _name.SetHexpand(true);
        WinoA11y.Label(_name, Translator.QuickEventDialog_EventName);
        var nameRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        nameRow.Append(_name);
        nameRow.Append(_allDay);

        var hours = viewModel.HourSelectionStrings.ToArray();
        var start = Gtk.DropDown.NewFromStrings(hours);
        var end = Gtk.DropDown.NewFromStrings(hours);
        WinoA11y.Label(start, Translator.CalendarEventCompose_Starts);
        WinoA11y.Label(end, Translator.CalendarEventCompose_Ends);
        var dash = Gtk.Label.New("–");
        dash.AddCssClass("dim-label");
        _timeRow.Append(start);
        _timeRow.Append(dash);
        _timeRow.Append(end);

        _range.SetXalign(0);
        _range.AddCssClass("caption");
        _range.AddCssClass("dim-label");

        _location.SetPlaceholderText(Translator.QuickEventDialog_Location);
        _location.SetHexpand(true);
        WinoA11y.Label(_location, Translator.QuickEventDialog_Location);
        var locationIcon = WinoIcons.Label(WinoIconGlyph.Location, 16);
        locationIcon.AddCssClass("dim-label");
        var locationRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        locationRow.Append(locationIcon);
        locationRow.Append(_location);

        _save.AddCssClass("suggested-action");
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.Append(spacer);
        buttons.Append(_more);
        buttons.Append(_save);

        var form = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        form.SetMarginTop(12);
        form.SetMarginBottom(12);
        form.SetMarginStart(12);
        form.SetMarginEnd(12);
        form.Append(_readOnlyNote);
        form.Append(nameRow);
        form.Append(_timeRow);
        form.Append(_range);
        form.Append(locationRow);
        form.Append(buttons);

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        column.SetSizeRequest(ContentWidth, -1);
        column.Append(_header);
        column.Append(form);
        _popover.SetChild(column);
        _popover.AddCssClass("wino-quick-event");
        _popover.SetAutohide(true);
        _popover.SetHasArrow(true);
        WinoA11y.Label(_popover, Translator.QuickEventDialog_EventName);

        bind.BindText(_name, nameof(CalendarPageViewModel.EventName), static vm => vm.EventName, static (vm, text) => vm.EventName = text);
        bind.BindText(_location, nameof(CalendarPageViewModel.Location), static vm => vm.Location, static (vm, text) => vm.Location = text);
        bind.BindActive(_allDay, nameof(CalendarPageViewModel.IsAllDay), static vm => vm.IsAllDay, static (vm, value) => vm.IsAllDay = value);
        bind.Bind(nameof(CalendarPageViewModel.IsAllDay), static vm => vm.IsAllDay, allDay => _timeRow.SetVisible(!allDay));
        bind.BindSelected(start, hours, nameof(CalendarPageViewModel.SelectedStartTimeString), static vm => vm.SelectedStartTimeString, static (vm, value) => vm.SelectedStartTimeString = value);
        bind.BindSelected(end, hours, nameof(CalendarPageViewModel.SelectedEndTimeString), static vm => vm.SelectedEndTimeString, static (vm, value) => vm.SelectedEndTimeString = value);
        bind.BindText(_range, nameof(CalendarPageViewModel.QuickEventDateRangeText), static vm => vm.QuickEventDateRangeText);
        bind.Bind(nameof(CalendarPageViewModel.SelectedQuickEventAccountCalendar), static vm => vm.SelectedQuickEventAccountCalendar, _ => ApplyCalendar());
        bind.Bind(nameof(CalendarPageViewModel.SelectedQuickEventDate), static vm => vm.SelectedQuickEventDate, date =>
        {
            if (date is null && IsOpen) Close();
        });

        _saveBinding = _bindings.Own(new CommandBinding(viewModel.SaveQuickEventCommand, static () => null, _save.SetSensitive, dispatcher, error));
        _bindings.Own(GtkSignals.OnClicked(_save, Save));
        _bindings.Own(GtkSignals.OnClicked(_more, MoreDetails));
        var name = _name;
        _bindings.Own(GtkSignals.Connect<Gtk.Entry>(h => name.OnActivate += h, h => name.OnActivate -= h, (_, _) =>
        {
            if (_save.GetSensitive()) Save();
        }));
        var popover = _popover;
        _bindings.Own(GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) => HandleClosed()));
    }

    public event EventHandler? Closed;

    public bool IsOpen { get; private set; }

    internal Gtk.Popover Popover => _popover;

    public void Present(Gtk.Widget anchorWidget, Gdk.Rectangle anchor, Gtk.PositionType side)
    {
        if (_disposed) return;
        _popover.SetParent(anchorWidget);
        _popover.SetPointingTo(anchor);
        _popover.SetPosition(side);
        IsOpen = true;
        _popover.Popup();
        _name.GrabFocus();
    }

    /// <summary>
    /// Closes without clearing the quick event: a click on another slot starts the next quick event first, and this
    /// popover's closed signal arrives after that, so it must not reset the shared state.
    /// </summary>
    public void Dismiss() => Close();

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        Release();
    }

    internal void Save()
    {
        if (!_save.GetSensitive()) return;
        Close();
        _saveBinding.Execute();
        _viewModel.SelectedQuickEventDate = null;
    }

    internal void MoreDetails()
    {
        Close();
        if (_viewModel.GoToEventComposePageCommand.CanExecute(null)) _viewModel.GoToEventComposePageCommand.Execute(null);
        _viewModel.SelectedQuickEventDate = null;
    }

#if DEBUG
    internal string Describe()
        => $"open={IsOpen} name='{_name.GetText()}' location='{_location.GetText()}' allDay={_allDay.GetActive()} timeRow={_timeRow.GetVisible()} range='{_range.GetText()}' calendar='{_calendarName.GetText()}' readOnly={_readOnlyNote.GetVisible()} save={_save.GetSensitive()} position={_popover.GetPosition()}";

    internal void DebugSetAllDay(bool value) => _allDay.SetActive(value);

    internal void DebugSetText(bool location, string text) => (location ? _location : _name).SetText(text);
#endif

    private void Close()
    {
        _closedByUs = true;
        if (IsOpen) _popover.Popdown();
    }

    private void HandleClosed()
    {
        IsOpen = false;
        if (!_closedByUs) _viewModel.SelectedQuickEventDate = null;
        // Unparenting a popover inside its closed handler crashes GTK; it waits one main-loop turn.
        MainLoop.Post(Release);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Release()
    {
        if (_disposed) return;
        _disposed = true;
        IsOpen = false;
        _bindings.Dispose();
        SwapClass(_header, ref _headerFill, null);
        if (_popover.GetParent() is not null) _popover.Unparent();
        Closed = null;
    }

    private void ApplyCalendar()
    {
        var calendar = _viewModel.SelectedQuickEventAccountCalendar;
        SwapClass(_header, ref _headerFill, WinoTint.FillClassFor(calendar?.BackgroundColorHex ?? "#D3D3D3", null));
        _accountName.SetText(calendar?.Account?.Name ?? string.Empty);
        _calendarName.SetText(_viewModel.SelectedQuickEventAccountCalendarName ?? string.Empty);
        _readOnlyNote.SetVisible(calendar?.IsReadOnly == true);
    }

    private static void SwapClass(Gtk.Widget widget, ref string? current, string? next)
    {
        if (string.Equals(current, next, StringComparison.Ordinal)) return;
        if (current is not null) widget.RemoveCssClass(current);
        if (next is not null) widget.AddCssClass(next);
        current = next;
    }
}
