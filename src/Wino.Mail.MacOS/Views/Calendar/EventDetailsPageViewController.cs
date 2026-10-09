using System.Text;
using AppKit;
using Foundation;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// Event details (Windows EventDetailsPage) in the calendar page's 320pt details zone: an action
/// bar (Join, RSVP, Delete, Edit series, Show as, Reminder), the RSVP panel, the title with the
/// calendar colour, detail rows with Wino glyphs, the people card with RSVP glyphs, the
/// description and attachments. Driven by the shared EventDetailsPageViewModel.
/// </summary>
public sealed class EventDetailsPageViewController(EventDetailsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<EventDetailsPageViewModel>(viewModel, dispatcher, logger)
{
    private static readonly NSColor CardFill = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));

    private NSStackView _column = null!;
    private WinoInfoBar _readOnlyBar = null!;
    private bool _isReadOnly;
    private ActionBarButton _join = null!;
    private ActionBarButton _rsvp = null!;
    private ActionBarButton _delete = null!;
    private ActionBarButton _series = null!;
    private ActionBarButton _openInWindow = null!;
    private NSPopUpButton _showAs = null!;
    private NSPopUpButton _reminder = null!;
    private WinoSurfaceView _rsvpPanel = null!;
    private NSTextField _rsvpMessage = null!;
    private WinoSurfaceView _colorSwatch = null!;
    private NSTextField _title = null!;
    private DetailRow _when = null!;
    private DetailRow _location = null!;
    private DetailRow _online = null!;
    private DetailRow _repeat = null!;
    private DetailRow _reminderRow = null!;
    private DetailRow _calendar = null!;
    private WinoSurfaceView _peopleCard = null!;
    private NSTextField _peopleTitle = null!;
    private NSStackView _peopleList = null!;
    private NSTextView _description = null!;
    private NSScrollView _descriptionScroll = null!;
    private WinoSurfaceView _attachmentsCard = null!;
    private NSStackView _attachmentList = null!;
    private BindingScope? _eventBindings;
    private BindingScope? _attachmentBindings;
    private bool _saving;
    private bool _savePending;
    private bool _released;
    private CalendarItemShowAs? _persistedShowAs;
    private bool _reminderMenuQueued;

    public override void LoadView()
    {
        _column = WinoLayout.VStack(12);
        _column.EdgeInsets = new NSEdgeInsets(16, 16, 16, 16);

        // Read-only calendars (subscriptions, birthdays, shared without write access) keep the event
        // viewable but drop the actions that would change it.
        _readOnlyBar = new WinoInfoBar(WinoInfoBarSeverity.Warning, Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message)
        {
            IsClosable = false,
            Hidden = true
        };
        AddRow(_readOnlyBar);

        // Action bar
        _join = Action(WinoIconGlyph.EventJoinOnline, Translator.CalendarEventDetails_JoinOnline, ViewModel.JoinOnlineCommand);
        _rsvp = Action(WinoIconGlyph.EventRespond, Translator.CalendarEventResponse_NotResponded, ViewModel.ToggleRsvpPanelCommand);
        _delete = Action(WinoIconGlyph.Delete, Translator.Buttons_Delete, ViewModel.DeleteCommand);
        _series = Action(WinoIconGlyph.EventEditSeries, Translator.CalendarEventDetails_EditSeries, ViewModel.ViewSeriesCommand);
        // Mac addition: the details pane can open the event in its own window (EventDetailsWindow).
        _openInWindow = new ActionBarButton(WinoIconGlyph.OpenInNewWindow, null, Translator.CalendarEventDetails_OpenInNewWindow);
        _openInWindow.Activated += (_, _) => OpenInNewWindow();
        _openInWindow.Hidden = IsInOwnWindow || !EventDetailsWindow.IsAvailable;
        _showAs = new NSPopUpButton { ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.CalendarEventDetails_ShowAs };
        _showAs.Font = NSFont.SystemFontOfSize(12);
        _showAs.WidthAnchor.ConstraintEqualTo(130).Active = true;
        foreach (var option in ViewModel.ShowAsOptions) _showAs.AddItem(option.DisplayText);
        // The details pane is an inspector: a Show as choice commits at once (like the Windows context menu).
        _showAs.Activated += (_, _) => ShowAsChosen();
        var showAsRow = WinoLayout.HStack(6, new WinoIconView(WinoIconGlyph.CalendarShowAs, 16, WinoStyle.SecondaryText), _showAs);
        _reminder = new NSPopUpButton { PullsDown = true, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.CalendarEventDetails_Reminder };
        _reminder.Font = NSFont.SystemFontOfSize(12);
        // RebuildReminderMenu also fills the reminder detail row, so that row must exist first.
        _reminderRow = new DetailRow(WinoIconGlyph.Reminder);
        RebuildReminderMenu();
        ViewModel.ReminderOptions.CollectionChanged += ReminderOptionsChanged;
        var reminderRow = WinoLayout.HStack(6, new WinoIconView(WinoIconGlyph.Reminder, 16, WinoStyle.SecondaryText), _reminder);
        var actions = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Spacing = 2, TranslatesAutoresizingMaskIntoConstraints = false };
        actions.Alignment = NSLayoutAttribute.CenterY;
        foreach (var view in new NSView[] { _join, _rsvp, _delete, _series }) actions.AddArrangedSubview(view);
        actions.AddArrangedSubview(WinoLayout.Spacer());
        actions.AddArrangedSubview(_openInWindow);
        var secondary = WinoLayout.HStack(10, showAsRow, reminderRow, WinoLayout.Spacer());
        var actionColumn = WinoLayout.VStack(4, actions, secondary);
        actionColumn.EdgeInsets = new NSEdgeInsets(6, 6, 6, 6);
        actions.WidthAnchor.ConstraintEqualTo(actionColumn.WidthAnchor, 1, -12).Active = true;
        var actionCard = new WinoSurfaceView { Fill = CardFill, CornerRadius = 7 };
        WinoLayout.Fill(actionColumn, actionCard);
        AddRow(actionCard);

        // RSVP panel
        _rsvpPanel = new WinoSurfaceView { Fill = CardFill, Stroke = WinoStyle.ZoneStroke, CornerRadius = 7, Hidden = true };
        var accept = RsvpButton(WinoIconGlyph.EventAccept, WinoStyle.Success, Translator.CalendarEventRsvpPanel_Accept, AttendeeStatus.Accepted);
        var tentative = RsvpButton(WinoIconGlyph.EventTentative, WinoStyle.Caution, Translator.CalendarEventRsvpPanel_Tentative, AttendeeStatus.Tentative);
        var decline = RsvpButton(WinoIconGlyph.EventDecline, WinoStyle.Critical, Translator.CalendarEventRsvpPanel_Decline, AttendeeStatus.Declined);
        var close = new ActionBarButton(WinoIconGlyph.Dismiss, null, Translator.Buttons_Close);
        close.Activated += (_, _) => ViewModel.CloseRsvpPanelCommand.Execute(null);
        var rsvpButtons = WinoLayout.HStack(2, accept, tentative, decline, WinoLayout.Spacer(), close);
        _rsvpMessage = new NSTextField { PlaceholderString = Translator.CalendarEventRsvpPanel_AddMessage, TranslatesAutoresizingMaskIntoConstraints = false, Font = NSFont.SystemFontOfSize(12) };
        _rsvpMessage.Changed += (_, _) => ViewModel.RsvpMessage = _rsvpMessage.StringValue;
        var rsvpColumn = WinoLayout.VStack(8, rsvpButtons, _rsvpMessage);
        rsvpColumn.EdgeInsets = new NSEdgeInsets(10, 10, 10, 10);
        rsvpButtons.WidthAnchor.ConstraintEqualTo(rsvpColumn.WidthAnchor, 1, -20).Active = true;
        _rsvpMessage.WidthAnchor.ConstraintEqualTo(rsvpColumn.WidthAnchor, 1, -20).Active = true;
        WinoLayout.Fill(rsvpColumn, _rsvpPanel);
        AddRow(_rsvpPanel);

        // Title
        _colorSwatch = new WinoSurfaceView { CornerRadius = 3 };
        WinoLayout.Size(_colorSwatch, 12, 12);
        _title = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(16, NSFontWeight.Semibold), WinoStyle.PrimaryText, 3);
        var titleRow = WinoLayout.HStack(10, _colorSwatch, _title);
        titleRow.Alignment = NSLayoutAttribute.Top;
        AddRow(titleRow);

        // Detail rows
        _when = new DetailRow(WinoIconGlyph.Clock);
        _location = new DetailRow(WinoIconGlyph.Location);
        _online = new DetailRow(WinoIconGlyph.EventJoinOnline);
        _repeat = new DetailRow(WinoIconGlyph.CalendarEventRepeat);
        _calendar = new DetailRow(WinoIconGlyph.Calendar);
        var details = WinoLayout.VStack(8, _when, _location, _online, _repeat, _reminderRow, _calendar);
        details.DetachesHiddenViews = true;
        AddRow(details);

        // People
        _peopleTitle = WinoStyle.Label(Translator.CalendarEventDetails_People, WinoStyle.Caption, WinoStyle.SecondaryText);
        _peopleList = WinoLayout.VStack(2);
        var peopleColumn = WinoLayout.VStack(8, _peopleTitle, _peopleList);
        peopleColumn.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        _peopleList.WidthAnchor.ConstraintEqualTo(peopleColumn.WidthAnchor, 1, -24).Active = true;
        _peopleCard = new WinoSurfaceView { Fill = CardFill, CornerRadius = 4, Hidden = true };
        WinoLayout.Fill(peopleColumn, _peopleCard);
        AddRow(_peopleCard);

        // Description
        _description = new NSTextView { Editable = false, Selectable = true, DrawsBackground = false, BackgroundColor = NSColor.Clear, TextContainerInset = new CoreGraphics.CGSize(0, 0) };
        _description.TextContainer!.WidthTracksTextView = true;
        _description.VerticallyResizable = true;
        _description.HorizontallyResizable = false;
        _description.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _descriptionScroll = new NSScrollView { DocumentView = _description, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        _descriptionScroll.HeightAnchor.ConstraintGreaterThanOrEqualTo(80).Active = true;
        _descriptionScroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        AddRow(_descriptionScroll);

        // Attachments
        var attachmentsTitle = WinoStyle.Label(Translator.CalendarEventDetails_Attachments, WinoStyle.Caption, WinoStyle.SecondaryText);
        _attachmentList = WinoLayout.VStack(2);
        var attachmentsColumn = WinoLayout.VStack(8, attachmentsTitle, _attachmentList);
        attachmentsColumn.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        _attachmentList.WidthAnchor.ConstraintEqualTo(attachmentsColumn.WidthAnchor, 1, -24).Active = true;
        _attachmentsCard = new WinoSurfaceView { Fill = CardFill, CornerRadius = 4, Hidden = true };
        WinoLayout.Fill(attachmentsColumn, _attachmentsCard);
        AddRow(_attachmentsCard);

        _column.DetachesHiddenViews = true;
        var document = new FlippedView();
        WinoLayout.Fill(_column, document);
        var scroll = new NSScrollView { DocumentView = document, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        var root = new AppearanceView { AppearanceChanged = () => { if (ViewModel.CurrentEvent is { } current) ApplyDescription(current.CalendarItem.Description); } };
        WinoLayout.Fill(scroll, root);
        View = root;
    }

    private void AddRow(NSView view)
    {
        _column.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_column.WidthAnchor, 1, -32).Active = true;
    }

    /// <summary>True when this page is hosted by an <see cref="EventDetailsWindow"/> rather than the calendar pane.</summary>
    internal bool IsInOwnWindow { get; set; }

    private void OpenInNewWindow()
    {
        if (ViewModel.CurrentEvent?.CalendarItem is not { } item) return;
        _ = OpenAsync();

        async Task OpenAsync()
        {
            try { await EventDetailsWindow.OpenAsync(item); }
            catch (Exception exception) { ReportError(exception); }
        }
    }

    /// <summary>Whether the shown event is on a read-only calendar (debug bridge and tests).</summary>
    internal bool IsReadOnlyEvent => _isReadOnly;

    /// <summary>A one-line summary of the read-only presentation, for the debug bridge.</summary>
    internal string DescribeReadOnlyState()
        => $"title='{ViewModel.CurrentEvent?.Title}' readOnly={_isReadOnly} bar={!_readOnlyBar.Hidden} delete={!_delete.Hidden} rsvp={!_rsvp.Hidden} showAs={_showAs.Enabled}";

    private ActionBarButton Action(WinoIconGlyph glyph, string title, System.Windows.Input.ICommand command)
    {
        var button = new ActionBarButton(glyph, title, title);
        var binding = Bindings.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        button.Activated += (_, _) => binding.Execute();
        return button;
    }

    private ActionBarButton RsvpButton(WinoIconGlyph glyph, NSColor tint, string title, AttendeeStatus status)
    {
        var button = new ActionBarButton(glyph, title, title, tint);
        var binding = Bindings.Own(new CommandBinding(ViewModel.SendRsvpResponseCommand, () => status, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        button.Activated += (_, _) => binding.Execute();
        return button;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // Saving keeps the pane (or the pop-out window) open; the pop-out never navigates the shell.
        ViewModel.NavigatesBackAfterSave = false;
        ViewModel.CanNavigateShell = !IsInOwnWindow;
        ViewModel.OnNavigatedTo(mode, parameter!);
        Bind(nameof(ViewModel.CurrentEvent), vm => vm.CurrentEvent, _ => ApplyEvent());
        Bind(nameof(ViewModel.IsRsvpPanelVisible), vm => vm.IsRsvpPanelVisible, visible => _rsvpPanel.Hidden = !visible || _isReadOnly);
        Bind(nameof(ViewModel.RsvpMessage), vm => vm.RsvpMessage, value => { if (_rsvpMessage.StringValue != value) _rsvpMessage.StringValue = value ?? string.Empty; });
        Bind(nameof(ViewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, option => { if (option is not null) _showAs.SelectItem(option.DisplayText); });
        Bind(nameof(ViewModel.CurrentRsvpText), vm => vm.CurrentRsvpText, _ => ApplyRsvp());
        Bind(nameof(ViewModel.CanEditSeries), vm => vm.CanEditSeries, can => _series.Hidden = !can);
        Bind(nameof(ViewModel.Reminders), vm => vm.Reminders, _ => QueueReminderMenu());
        Bind(nameof(ViewModel.HasAttachments), vm => vm.HasAttachments, _ => ApplyAttachments());
        return Task.CompletedTask;
    }

    private void Bind<TValue>(string property, Func<EventDetailsPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<EventDetailsPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private void ApplyEvent()
    {
        var item = ViewModel.CurrentEvent;
        _eventBindings?.Dispose();
        _eventBindings = null;
        if (item is null) return;
        if (!_saving) _persistedShowAs = item.CalendarItem.ShowAs;
        _eventBindings = new BindingScope();
        _eventBindings.Own(new PropertyBinding<CalendarItemViewModel, string>(item, nameof(item.Title), i => i.Title, _ => ApplyEventFields(item), Dispatcher, ReportError));
        _eventBindings.Own(new PropertyBinding<CalendarItemViewModel, bool>(item, nameof(item.IsBusy), i => i.IsBusy, busy => View.AlphaValue = busy ? (nfloat)0.7 : 1, Dispatcher, ReportError));
        item.Attendees.CollectionChanged += AttendeesChanged;
        _eventBindings.Own(new ActionDisposable(() => item.Attendees.CollectionChanged -= AttendeesChanged));
        ApplyEventFields(item);
        ApplyAttendees();
        ApplyRsvp();
    }

    private void AttendeesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => _ = Dispatcher.ExecuteOnUIThread(ApplyAttendees);

    private void ApplyEventFields(CalendarItemViewModel item)
    {
        var settings = ViewModel.CurrentSettings;
        _title.StringValue = item.Title ?? string.Empty;
        _colorSwatch.Fill = CalendarTileMapper.Fill(item);
        _when.Text = CalendarTileMapper.DateString(item, settings);
        _location.Text = item.Location;
        _online.Text = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item.CalendarItem, out var uri) ? uri.Host : null;
        _repeat.Text = item.IsRecurringEvent ? CalendarTileMapper.RecurrenceString(item, settings) : null;
        var calendar = item.AssignedCalendar;
        _calendar.Text = calendar is null ? null : string.IsNullOrEmpty(calendar.MailAccount?.Name) ? calendar.Name : $"{calendar.Name} · {calendar.MailAccount.Name}";
        UpdateReminderRow();
        ApplyDescription(item.CalendarItem.Description);
        ApplyReadOnly(item);
    }

    /// <summary>Shows the read-only notice and hides Delete and RSVP, and locks Show as, for read-only calendars.</summary>
    private void ApplyReadOnly(CalendarItemViewModel item)
    {
        _isReadOnly = CalendarTileMapper.IsReadOnly(item);
        _readOnlyBar.Hidden = !_isReadOnly;
        _delete.Hidden = _isReadOnly;
        _rsvp.Hidden = _isReadOnly;
        _showAs.Enabled = !_isReadOnly;
        _reminder.Enabled = !_isReadOnly;
        if (_isReadOnly) _rsvpPanel.Hidden = true;
        else _rsvpPanel.Hidden = !ViewModel.IsRsvpPanelVisible;
    }

    // ------------------------------------------------------------ commit on change

    /// <summary>
    /// Saves the Show as and reminder choice. Saves never overlap: a request during a save runs one
    /// more save afterwards (the shared command refuses concurrent executions).
    /// </summary>
    private void RequestSave()
    {
        if (_isReadOnly || _released || ViewModel.CurrentEvent is null) return;
        if (_saving) { _savePending = true; return; }
        _ = SaveLoopAsync();
    }

    private async Task SaveLoopAsync()
    {
        _saving = true;
        try
        {
            do
            {
                _savePending = false;
                var persisted = _persistedShowAs;
                await ViewModel.SaveCommand.ExecuteAsync(null);
                bool succeeded = ViewModel.LastMutationSucceeded;
                await Dispatcher.ExecuteOnUIThread(() =>
                {
                    if (_released) return;
                    if (succeeded) _persistedShowAs = ViewModel.SelectedShowAsOption?.ShowAs ?? persisted;
                    else { RevertToPersisted(persisted); _savePending = false; }
                });
            }
            while (_savePending && !_released);
        }
        catch (Exception exception) { ReportError(exception); }
        finally { _saving = false; }
    }

    /// <summary>A failed save leaves the controls on the persisted values, not on the rejected choice.</summary>
    private void RevertToPersisted(CalendarItemShowAs? persistedShowAs)
    {
        if (persistedShowAs is { } showAs)
        {
            if (ViewModel.CurrentEvent?.CalendarItem is { } calendarItem) calendarItem.ShowAs = showAs;
            ViewModel.SelectedShowAsOption = ViewModel.ShowAsOptions.FirstOrDefault(option => option.ShowAs == showAs) ?? ViewModel.SelectedShowAsOption;
            if (ViewModel.SelectedShowAsOption is { } selected) _showAs.SelectItem(selected.DisplayText);
        }
        var minutes = (ViewModel.Reminders ?? []).Select(reminder => (int)(reminder.DurationInSeconds / 60)).ToHashSet();
        foreach (var option in ViewModel.ReminderOptions) option.IsSelected = minutes.Contains(option.Minutes);
        RebuildReminderMenu();
    }

    private void ReminderOptionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => QueueReminderMenu();

    /// <summary>The ViewModel replaces the reminder options when an event loads; rebuild the menu once per burst.</summary>
    private void QueueReminderMenu()
    {
        if (_reminderMenuQueued) return;
        _reminderMenuQueued = true;
        _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            _reminderMenuQueued = false;
            if (!_released) RebuildReminderMenu();
        });
    }

    private void ShowAsChosen()
    {
        var index = (int)_showAs.IndexOfSelectedItem;
        if (index < 0 || index >= ViewModel.ShowAsOptions.Count) return;
        var option = ViewModel.ShowAsOptions[index];
        if (ReferenceEquals(option, ViewModel.SelectedShowAsOption)) return;
        ViewModel.SelectedShowAsOption = option;
        RequestSave();
    }

    private void ToggleReminder(ReminderOption option, NSMenuItem item)
    {
        option.IsSelected = !option.IsSelected;
        item.State = option.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off;
        UpdateReminderRow();
        RequestSave();
    }

#if DEBUG
    /// <summary>Debug bridge: picks a Show as value through the popup path and waits for the save.</summary>
    internal async Task<string> DebugChooseShowAsAsync(string value)
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!Enum.TryParse<CalendarItemShowAs>(value, true, out var showAs)) return;
            var option = ViewModel.ShowAsOptions.FirstOrDefault(candidate => candidate.ShowAs == showAs);
            if (option is null) return;
            _showAs.SelectItem(ViewModel.ShowAsOptions.IndexOf(option));
            ShowAsChosen();
        });
        return await DebugAfterSaveAsync();
    }

    /// <summary>Debug bridge: toggles the reminder with <paramref name="minutes"/> through the menu path and waits for the save.</summary>
    internal async Task<string> DebugToggleReminderAsync(int minutes)
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            var option = ViewModel.ReminderOptions.FirstOrDefault(candidate => candidate.Minutes == minutes);
            var item = option is null ? null : _reminder.Menu!.Items.FirstOrDefault(candidate => candidate.Title == option.DisplayText);
            if (option is not null && item is not null) ToggleReminder(option, item);
        });
        return await DebugAfterSaveAsync();
    }

    private async Task<string> DebugAfterSaveAsync()
    {
        for (int attempt = 0; attempt < 50 && (_saving || _savePending); attempt++) await Task.Delay(100);
        string result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result =
            $"saved={ViewModel.LastMutationSucceeded} showAs={ViewModel.CurrentEvent?.CalendarItem.ShowAs} popup={_showAs.TitleOfSelectedItem} " +
            $"reminders=[{string.Join(",", (ViewModel.Reminders ?? []).Select(reminder => reminder.DurationInSeconds / 60))}] row='{_reminderRow.Text}' inWindow={IsInOwnWindow}");
        return result;
    }

    /// <summary>Debug bridge: runs Save on the attachment at <paramref name="index"/> (the folder panel needs a manual choice).</summary>
    internal string DebugSaveAttachment(int index)
    {
        if (index < 0 || index >= ViewModel.Attachments.Count) return $"only {ViewModel.Attachments.Count} attachments";
        SaveAttachment(ViewModel.Attachments[index]);
        return "save started for " + ViewModel.Attachments[index].FileName;
    }
#endif

    /// <summary>Pull-down: the first item is the title; each option toggles its checkmark and commits.</summary>
    private void RebuildReminderMenu()
    {
        var menu = _reminder.Menu!;
        menu.RemoveAllItems();
        menu.AddItem(new NSMenuItem(Translator.CalendarEventDetails_Reminder));
        foreach (var option in ViewModel.ReminderOptions)
        {
            var captured = option;
            var item = new NSMenuItem(option.DisplayText) { State = option.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off };
            item.Activated += (_, _) => ToggleReminder(captured, item);
            menu.AddItem(item);
        }
        UpdateReminderRow();
    }

    private void UpdateReminderRow()
    {
        var selected = ViewModel.ReminderOptions.Where(option => option.IsSelected).Select(option => option.DisplayText).ToList();
        _reminderRow.Text = selected.Count == 0 ? null : string.Join(", ", selected);
    }

    private void ApplyRsvp()
    {
        var (glyph, tint) = CalendarTileMapper.RsvpGlyph(ViewModel.CurrentRsvpStatus);
        _rsvp.SetGlyph(glyph, tint);
        _rsvp.Title = ViewModel.CurrentRsvpText;
    }

    private void ApplyAttendees()
    {
        foreach (var view in _peopleList.ArrangedSubviews.ToArray()) { _peopleList.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        var attendees = ViewModel.CurrentEvent?.Attendees;
        bool any = attendees is { Count: > 0 };
        _peopleCard.Hidden = !any;
        if (!any) return;
        _peopleTitle.StringValue = $"{Translator.CalendarEventDetails_People} · {attendees!.Count}";
        foreach (var attendee in attendees)
        {
            var row = AttendeeRow(attendee);
            _peopleList.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_peopleList.WidthAnchor).Active = true;
        }
    }

    private static NSView AttendeeRow(CalendarEventAttendee attendee)
    {
        var picture = new WinoContactPicture(24);
        picture.SetIdentity(string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name, attendee.Email);
        var name = WinoStyle.Label(string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name, WinoStyle.Body);
        name.ToolTip = attendee.Email;
        var children = new List<NSView> { picture, name };
        if (attendee.IsOrganizer)
        {
            var badge = new WinoSurfaceView { Fill = WinoStyle.Accent, CornerRadius = 4 };
            var badgeText = WinoStyle.Label(Translator.CalendarEventDetails_Organizer, WinoStyle.CaptionStrong, NSColor.White);
            WinoLayout.Fill(badgeText, badge, 1, 6, 1, 6);
            children.Add(badge);
        }
        children.Add(WinoLayout.Spacer());
        var (glyph, tint) = CalendarTileMapper.AttendeeStatusGlyph(attendee.AttendenceStatus);
        var status = new WinoIconView(glyph, 14, tint) { ToolTip = CalendarTileMapper.AttendeeStatusText(attendee.AttendenceStatus) };
        children.Add(status);
        var row = WinoLayout.HStack(8, children.ToArray());
        WinoLayout.Size(row, -1, 30);
        return row;
    }

    private void ApplyDescription(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            _descriptionScroll.Hidden = true;
            return;
        }
        NSAttributedString? attributed = null;
        try
        {
            string document = html.Contains('<') ? html : $"<p>{System.Net.WebUtility.HtmlEncode(html).Replace("\n", "<br>")}</p>";
            var styled = $"<meta charset=\"utf-8\"><style>body{{font-family:-apple-system;font-size:12px;color:{(WinoIcons.IsDark(View.EffectiveAppearance) ? "#e6e6e6" : "#1b1b1b")};}}</style>{document}";
            attributed = NSAttributedString.CreateWithHTML(NSData.FromString(styled, NSStringEncoding.UTF8), out NSDictionary _);
        }
        catch (Exception exception) { ReportError(exception); }
        attributed ??= new NSAttributedString(html, new NSStringAttributes { Font = NSFont.SystemFontOfSize(12), ForegroundColor = WinoStyle.PrimaryText });
        attributed = TrimEdges(attributed);
        // Drop baked-in foreground colours so the text follows light/dark through the view's TextColor; links keep their own style.
        var mutable = new NSMutableAttributedString(attributed);
        var whole = new NSRange(0, (nint)mutable.Length);
        mutable.RemoveAttribute(NSStringAttributeKey.ForegroundColor, whole);
        mutable.AddAttribute(NSStringAttributeKey.ForegroundColor, WinoStyle.PrimaryText, whole);
        _description.TextStorage?.SetString(mutable);
        _descriptionScroll.Hidden = false;
        _description.LayoutManager?.EnsureLayoutForTextContainer(_description.TextContainer!);
        var height = Math.Min(260, Math.Max(80, _description.LayoutManager?.GetUsedRect(_description.TextContainer!).Height ?? 80) + 8);
        _descriptionHeight ??= _descriptionScroll.HeightAnchor.ConstraintEqualTo((nfloat)height);
        _descriptionHeight.Constant = (nfloat)height;
        _descriptionHeight.Active = true;
    }

    private NSLayoutConstraint? _descriptionHeight;

    /// <summary>HTML import leaves blank paragraphs at both ends; drop them.</summary>
    private static NSAttributedString TrimEdges(NSAttributedString text)
    {
        var value = text.Value ?? string.Empty;
        int start = 0, end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start])) start++;
        while (end > start && char.IsWhiteSpace(value[end - 1])) end--;
        return start == 0 && end == value.Length ? text : text.Substring(start, end - start);
    }

    /// <summary>
    /// Attachment rows like the mail reader: a click opens, the right-click menu offers Open and Save,
    /// and the inline Open and Save buttons stay visible. A spinner replaces the buttons while busy.
    /// </summary>
    private void ApplyAttachments()
    {
        _attachmentBindings?.Dispose();
        _attachmentBindings = new BindingScope();
        foreach (var view in _attachmentList.ArrangedSubviews.ToArray()) { _attachmentList.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        _attachmentsCard.Hidden = !ViewModel.HasAttachments;
        if (!ViewModel.HasAttachments) return;
        foreach (var attachment in ViewModel.Attachments)
        {
            var captured = attachment;
            var icon = new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText);
            var name = WinoStyle.Label(attachment.FileName, WinoStyle.Body);
            name.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
            name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.TertiaryText);
            var open = InlineButton(Translator.Buttons_Open, $"{Translator.Buttons_Open} {attachment.FileName}", () => OpenAttachment(captured));
            var save = InlineButton(Translator.Buttons_Save, $"{Translator.Buttons_Save} {attachment.FileName}", () => SaveAttachment(captured));
            var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
            var content = WinoLayout.HStack(8, icon, name, size, WinoLayout.Spacer(), spinner, open, save);
            var row = new AttachmentRow(() => OpenAttachment(captured), () => AttachmentMenu(captured));
            WinoLayout.Fill(content, row);
            WinoLayout.Size(row, -1, 30);
            _attachmentBindings.Own(new PropertyBinding<CalendarAttachmentViewModel, bool>(attachment, nameof(attachment.IsBusy), a => a.IsBusy, busy =>
            {
                open.Hidden = save.Hidden = busy;
                spinner.Hidden = !busy;
                if (busy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
            }, Dispatcher, ReportError));
            _attachmentList.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_attachmentList.WidthAnchor).Active = true;
        }
    }

    private static NSButton InlineButton(string title, string accessibilityLabel, Action action)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Inline, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = accessibilityLabel };
        WinoAccessibility.Label(button, accessibilityLabel);
        button.Activated += (_, _) => action();
        return button;
    }

    private NSMenu AttachmentMenu(CalendarAttachmentViewModel attachment)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.Buttons_Open, (_, _) => OpenAttachment(attachment)) { Image = WinoIcons.Image(WinoIconGlyph.Open, 14), Enabled = !attachment.IsBusy });
        menu.AddItem(new NSMenuItem(Translator.Buttons_Save, (_, _) => SaveAttachment(attachment)) { Image = WinoIcons.Image(WinoIconGlyph.Save, 14), Enabled = !attachment.IsBusy });
        return menu;
    }

    private void OpenAttachment(CalendarAttachmentViewModel attachment)
    {
        if (!attachment.IsBusy) Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
    }

    private void SaveAttachment(CalendarAttachmentViewModel attachment)
    {
        if (!attachment.IsBusy) Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment));
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override Task DeactivateAsync()
    {
        _released = true;
        ViewModel.ReminderOptions.CollectionChanged -= ReminderOptionsChanged;
        _eventBindings?.Dispose();
        _eventBindings = null;
        _attachmentBindings?.Dispose();
        _attachmentBindings = null;
        return base.DeactivateAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _released = true;
            ViewModel.ReminderOptions.CollectionChanged -= ReminderOptionsChanged;
            _eventBindings?.Dispose(); _eventBindings = null;
            _attachmentBindings?.Dispose(); _attachmentBindings = null;
        }
        base.Dispose(disposing);
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() { TranslatesAutoresizingMaskIntoConstraints = false; }
        public override bool IsFlipped => true;
    }

    /// <summary>Root view that re-renders the HTML description (whose colours are baked in) on light/dark switches.</summary>
    private sealed class AppearanceView : NSView
    {
        public Action? AppearanceChanged { get; init; }
        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            AppearanceChanged?.Invoke();
        }
    }
}

/// <summary>An attachment row: a click opens (as in the mail reader), a right click shows the Open/Save menu.</summary>
internal sealed class AttachmentRow : NSView
{
    private readonly Action _open;
    private readonly Func<NSMenu> _menu;
    private bool _pressed;

    public AttachmentRow(Action open, Func<NSMenu> menu)
    {
        _open = open;
        _menu = menu;
        TranslatesAutoresizingMaskIntoConstraints = false;
    }

    public override void MouseDown(NSEvent theEvent) => _pressed = true;

    public override void MouseUp(NSEvent theEvent)
    {
        // The first click of a double click already opened the file.
        bool inside = Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null));
        if (_pressed && inside && theEvent.ClickCount <= 1) _open();
        _pressed = false;
    }

    public override NSMenu? MenuForEvent(NSEvent theEvent) => _menu();
}

/// <summary>A glyph + label row (Clock, Location, Reminder, Calendar) that hides itself when empty.</summary>
internal sealed class DetailRow : NSStackView
{
    private readonly NSTextField _label;

    public DetailRow(WinoIconGlyph glyph)
    {
        Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        Spacing = 10;
        Alignment = NSLayoutAttribute.Top;
        TranslatesAutoresizingMaskIntoConstraints = false;
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText);
        _label = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 3);
        AddArrangedSubview(icon);
        AddArrangedSubview(_label);
        Hidden = true;
    }

    public string? Text
    {
        get => _label.StringValue;
        set { _label.StringValue = value ?? string.Empty; Hidden = string.IsNullOrWhiteSpace(value); }
    }
}

/// <summary>Windows TransparentActionButtonStyle: glyph, label, hover fill, 28pt tall.</summary>
internal sealed class ActionBarButton : WinoSurfaceView
{
    private static readonly NSColor HoverFill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    private readonly WinoIconView _icon;
    private readonly NSTextField? _label;
    private bool _enabled = true;

    public ActionBarButton(WinoIconGlyph glyph, string? title, string accessibilityLabel, NSColor? tint = null)
    {
        CornerRadius = 5;
        _icon = new WinoIconView(glyph, 14, tint);
        var children = new List<NSView> { _icon };
        if (title is not null) { _label = WinoStyle.Label(title, NSFont.SystemFontOfSize(12)); children.Add(_label); }
        var stack = WinoLayout.HStack(6, children.ToArray());
        stack.EdgeInsets = new NSEdgeInsets(0, 8, 0, 8);
        WinoLayout.Fill(stack, this);
        HeightAnchor.ConstraintEqualTo(28).Active = true;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ButtonRole;
        AccessibilityLabel = accessibilityLabel;
        ToolTip = accessibilityLabel;
    }

    public event EventHandler? Activated;

    public string Title
    {
        set { if (_label is not null) _label.StringValue = value ?? string.Empty; }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; AlphaValue = value ? 1 : (nfloat)0.4; }
    }

    public void SetGlyph(WinoIconGlyph glyph, NSColor? tint)
    {
        _icon.Icon = glyph;
        _icon.Tint = tint;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        foreach (var area in TrackingAreas()) RemoveTrackingArea(area);
        AddTrackingArea(new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
    }

    public override void MouseEntered(NSEvent theEvent) { if (_enabled) Fill = HoverFill; }
    public override void MouseExited(NSEvent theEvent) => Fill = null;
    public override void MouseDown(NSEvent theEvent) { }
    public override void MouseUp(NSEvent theEvent)
    {
        if (_enabled && Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Activated?.Invoke(this, EventArgs.Empty);
    }

    public override bool AccessibilityPerformPress()
    {
        if (_enabled) Activated?.Invoke(this, EventArgs.Empty);
        return _enabled;
    }
}
