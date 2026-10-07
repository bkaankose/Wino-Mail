using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// The Windows quick event popup over an empty slot: calendar header in the calendar colour,
/// event name, all-day switch, start/end time, location, then Save and "More details".
/// Everything binds to the CalendarPageViewModel quick event state.
/// </summary>
internal sealed class QuickEventPopover : NSPopover
{
    private readonly CalendarPageViewModel _viewModel;
    private readonly BindingScope _bindings = new();
    private readonly NSTextField _name;
    private readonly NSButton _allDay;
    private readonly NSPopUpButton _start;
    private readonly NSPopUpButton _end;
    private readonly NSTextField _location;
    private readonly NSTextField _range;
    private readonly NSButton _save;
    private readonly WinoSurfaceView _header;
    private readonly NSTextField _calendarName;
    private readonly NSTextField _accountName;
    private bool _closedByUs;

    public QuickEventPopover(CalendarPageViewModel viewModel, IDispatcher dispatcher, Action<Exception> error)
    {
        _viewModel = viewModel;
        Behavior = NSPopoverBehavior.Transient;

        _header = new WinoSurfaceView { CornerRadius = 0 };
        _accountName = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12));
        _calendarName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        var headerRow = WinoLayout.HStack(10, _accountName, _calendarName, WinoLayout.Spacer());
        headerRow.EdgeInsets = new NSEdgeInsets(0, 12, 0, 12);
        WinoLayout.Fill(headerRow, _header);
        WinoLayout.Size(_header, -1, 36);

        _name = new NSTextField { PlaceholderString = Translator.QuickEventDialog_EventName, Font = NSFont.SystemFontOfSize(16), TranslatesAutoresizingMaskIntoConstraints = false };
        _name.Changed += (_, _) => _viewModel.EventName = _name.StringValue;
        _allDay = WinoCheckbox.Create(Translator.QuickEventDialog_IsAllDay, () => { });
        _allDay.Activated += (_, _) => _viewModel.IsAllDay = _allDay.State == NSCellStateValue.On;
        _allDay.TranslatesAutoresizingMaskIntoConstraints = false;
        var nameRow = WinoLayout.HStack(12, _name, _allDay);
        _name.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        _start = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        _end = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var hour in _viewModel.HourSelectionStrings) { _start.AddItem(hour); _end.AddItem(hour); }
        _start.Activated += (_, _) => _viewModel.SelectedStartTimeString = _start.SelectedItem?.Title ?? string.Empty;
        _end.Activated += (_, _) => _viewModel.SelectedEndTimeString = _end.SelectedItem?.Title ?? string.Empty;
        var dash = WinoStyle.Label("–", WinoStyle.Body, WinoStyle.SecondaryText);
        var timeRow = WinoLayout.HStack(8, _start, dash, _end);
        _range = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);

        _location = new NSTextField { PlaceholderString = Translator.QuickEventDialog_Location, TranslatesAutoresizingMaskIntoConstraints = false };
        _location.Changed += (_, _) => _viewModel.Location = _location.StringValue;
        var locationRow = WinoLayout.HStack(8, new WinoIconView(Wino.Core.Domain.Enums.WinoIconGlyph.Location, 16, WinoStyle.SecondaryText), _location);

        _save = new NSButton { Title = Translator.Buttons_Save, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", TranslatesAutoresizingMaskIntoConstraints = false };
        var saveBinding = _bindings.Own(new CommandBinding(_viewModel.SaveQuickEventCommand, () => null, enabled => _save.Enabled = enabled, dispatcher, error));
        _save.Activated += (_, _) => { _closedByUs = true; Close(); saveBinding.Execute(); _viewModel.SelectedQuickEventDate = null; };
        var more = new NSButton { Title = Translator.QuickEventDialogMoreDetailsButtonText, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        more.Activated += (_, _) => { _closedByUs = true; Close(); _viewModel.GoToEventComposePageCommand.Execute(null); _viewModel.SelectedQuickEventDate = null; };
        var buttons = WinoLayout.HStack(8, WinoLayout.Spacer(), more, _save);

        var form = WinoLayout.VStack(12, nameRow, timeRow, _range, locationRow, buttons);
        form.EdgeInsets = new NSEdgeInsets(12, 12, 12, 12);
        foreach (var view in new NSView[] { nameRow, timeRow, locationRow, buttons })
            view.WidthAnchor.ConstraintEqualTo(form.WidthAnchor, 1, -24).Active = true;
        _location.WidthAnchor.ConstraintGreaterThanOrEqualTo(200).Active = true;

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var column = WinoLayout.VStack(0, _header, form);
        _header.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        form.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        WinoLayout.Fill(column, root);
        root.WidthAnchor.ConstraintEqualTo(340).Active = true;
        ContentViewController = new NSViewController { View = root };

        Bind(nameof(_viewModel.EventName), vm => vm.EventName, value => { if (_name.StringValue != value) _name.StringValue = value ?? string.Empty; }, dispatcher, error);
        Bind(nameof(_viewModel.Location), vm => vm.Location, value => { if (_location.StringValue != value) _location.StringValue = value ?? string.Empty; }, dispatcher, error);
        Bind(nameof(_viewModel.IsAllDay), vm => vm.IsAllDay, value => { _allDay.State = value ? NSCellStateValue.On : NSCellStateValue.Off; timeRow.Hidden = value; }, dispatcher, error);
        Bind(nameof(_viewModel.SelectedStartTimeString), vm => vm.SelectedStartTimeString, value => _start.SelectItem(value), dispatcher, error);
        Bind(nameof(_viewModel.SelectedEndTimeString), vm => vm.SelectedEndTimeString, value => _end.SelectItem(value), dispatcher, error);
        Bind(nameof(_viewModel.QuickEventDateRangeText), vm => vm.QuickEventDateRangeText, value => _range.StringValue = value ?? string.Empty, dispatcher, error);
        Bind(nameof(_viewModel.SelectedQuickEventAccountCalendar), vm => vm.SelectedQuickEventAccountCalendar, _ => ApplyCalendar(), dispatcher, error);
        Bind(nameof(_viewModel.SelectedQuickEventDate), vm => vm.SelectedQuickEventDate, value => { if (value is null && Shown) { _closedByUs = true; Close(); } }, dispatcher, error);
        Delegate = new CloseDelegate(this);
    }

    private void HandleClosed()
    {
        if (!_closedByUs) _viewModel.SelectedQuickEventDate = null;
        _bindings.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class CloseDelegate(QuickEventPopover owner) : NSPopoverDelegate
    {
        public override void DidClose(NSNotification notification) => owner.HandleClosed();
    }

    public event EventHandler? Closed;

    private void Bind<TValue>(string property, Func<CalendarPageViewModel, TValue> read, Action<TValue> apply, IDispatcher dispatcher, Action<Exception> error)
        => _bindings.Own(new PropertyBinding<CalendarPageViewModel, TValue>(_viewModel, property, read, apply, dispatcher, error));

    private void ApplyCalendar()
    {
        var calendar = _viewModel.SelectedQuickEventAccountCalendar;
        var fill = WinoStyle.FromHexString(calendar?.BackgroundColorHex) ?? NSColor.LightGray;
        _header.Fill = fill;
        var text = WinoCalendarItemView.ReadableTextColor(fill);
        _accountName.StringValue = calendar?.Account?.Name ?? string.Empty;
        _accountName.TextColor = text.ColorWithAlphaComponent((nfloat)0.85);
        _calendarName.StringValue = _viewModel.SelectedQuickEventAccountCalendarName ?? string.Empty;
        _calendarName.TextColor = text;
    }

    public void Present(CGRect anchor, NSView positioningView, NSRectEdge edge)
    {
        Show(anchor, positioningView, edge);
        positioningView.Window?.MakeFirstResponder(_name);
    }
}
