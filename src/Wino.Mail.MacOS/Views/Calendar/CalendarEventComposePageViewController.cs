using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// The event composer (Windows CalendarEventComposePage) as a sheet over the shell window: a
/// command bar (calendar, show as, reminder, private, online meeting, Cancel, Save/Send), the
/// event form (title, when, repeat, location, notes) and the 340pt side pane with attendees and
/// attachments. The calendar page hosts it through the rendering frame route.
/// </summary>
public sealed class CalendarEventComposePageViewController(CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<CalendarEventComposePageViewModel>(viewModel, dispatcher, logger)
{
    private const double SidePaneWidth = 340;
    private static readonly NSColor PaneFill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.03), WinoStyle.Hex(0xFFFFFF, 0.04));

    private NSPopUpButton _calendarPicker = null!;
    private WinoSurfaceView _calendarDot = null!;
    private NSPopUpButton _showAs = null!;
    private NSPopUpButton _reminder = null!;
    private NSButton _private = null!;
    private NSButton _onlineMeeting = null!;
    private NSButton _save = null!;
    private NSTextField _title = null!;
    private NSDatePicker _startDate = null!;
    private NSDatePicker _startTime = null!;
    private NSDatePicker _endDate = null!;
    private NSDatePicker _endTime = null!;
    private NSButton _allDay = null!;
    private NSTextField _duration = null!;
    private NSTextField _rangeError = null!;
    private NSPopUpButton _repeat = null!;
    private NSTextField _recurrenceSummary = null!;
    private NSTextField _location = null!;
    private NSTextView _notes = null!;
    private NSTextField _attendeesTitle = null!;
    private NSTextField _invite = null!;
    private NSStackView _attendeeList = null!;
    private NSTextField _attachmentsTitle = null!;
    private NSButton _addAttachment = null!;
    private NSStackView _attachmentList = null!;
    private bool _applying;

    /// <summary>Raised when the composer wants its sheet closed (Cancel, or the event was created).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The sheet window that shows this controller; the calendar page begins and ends it.</summary>
    public NSWindow CreateSheetWindow()
    {
        var window = new NSWindow(new CGRect(0, 0, 980, 680), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = Translator.CalendarEventCompose_NewEventButton,
            ContentViewController = this,
            MinSize = new CGSize(760, 520)
        };
        return window;
    }

    public override void LoadView()
    {
        PreferredContentSize = new CGSize(980, 680);
        var root = new NSView();

        // Command bar.
        _calendarDot = new WinoSurfaceView { CornerRadius = 7, Fill = WinoStyle.Accent };
        WinoLayout.Size(_calendarDot, 14, 14);
        _calendarPicker = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        _calendarPicker.Activated += (_, _) => { if (_calendarPicker.SelectedItem?.RepresentedObject is CalendarChoice choice) ViewModel.SelectedCalendar = choice.Calendar; };
        var calendarGroup = WinoLayout.HStack(6, _calendarDot, _calendarPicker);
        _showAs = Popup(Translator.CalendarEventDetails_ShowAs);
        _showAs.Activated += (_, _) => Select(ViewModel.ShowAsOptions, _showAs, option => ViewModel.SelectedShowAsOption = option);
        _reminder = Popup(Translator.CalendarEventDetails_Reminder);
        _reminder.Activated += (_, _) => Select(ViewModel.ReminderOptions, _reminder, option => ViewModel.SelectedReminderOption = option);
        _private = Checkbox(Translator.CalendarEventCompose_Private, () => ViewModel.IsPrivate = _private.State == NSCellStateValue.On);
        _private.ToolTip = Translator.CalendarEventCompose_PrivateTooltip;
        _onlineMeeting = Checkbox(string.Empty, () => ViewModel.IsOnlineMeeting = _onlineMeeting.State == NSCellStateValue.On);
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b", TranslatesAutoresizingMaskIntoConstraints = false };
        cancel.Activated += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _save = new NSButton { Title = Translator.Buttons_Save, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", TranslatesAutoresizingMaskIntoConstraints = false };
        var createBinding = Bindings.Own(new CommandBinding(ViewModel.CreateCommand, () => null, enabled => _save.Enabled = enabled, Dispatcher, ReportError));
        _save.Activated += (_, _) => createBinding.Execute();
#if DEBUG
        MacDebugBridge.Register("calsave", _ => { createBinding.Execute(); return Task.FromResult("ok"); });
#endif
        var bar = WinoLayout.HStack(10,
            Glyph(WinoIconGlyph.Calendar), calendarGroup,
            Glyph(WinoIconGlyph.CalendarShowAs), _showAs,
            Glyph(WinoIconGlyph.Reminder), _reminder,
            _private, _onlineMeeting, WinoLayout.Spacer(), cancel, _save);
        bar.EdgeInsets = new NSEdgeInsets(8, 12, 8, 12);
        var barCard = new WinoSurfaceView { Fill = WinoStyle.ZoneFill, Stroke = WinoStyle.ZoneStroke, CornerRadius = 7 };
        WinoLayout.Fill(bar, barCard);

        // Form.
        _title = new NSTextField
        {
            PlaceholderString = Translator.CalendarEventCompose_TitlePlaceholder,
            Font = NSFont.SystemFontOfSize(24, NSFontWeight.Semibold),
            Bordered = false, DrawsBackground = false, FocusRingType = NSFocusRingType.None,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _title.Changed += (_, _) => ViewModel.Title = _title.StringValue;
        var titleRule = new WinoSeparator();
        var titleBlock = WinoLayout.VStack(6, _title, titleRule);

        _startDate = DatePicker(NSDatePickerElementFlags.YearMonthDateDay);
        _startTime = DatePicker(NSDatePickerElementFlags.HourMinute);
        _endDate = DatePicker(NSDatePickerElementFlags.YearMonthDateDay);
        _endTime = DatePicker(NSDatePickerElementFlags.HourMinute);
        _startDate.Activated += (_, _) => PushDates();
        _startTime.Activated += (_, _) => PushDates();
        _endDate.Activated += (_, _) => PushDates();
        _endTime.Activated += (_, _) => PushDates();
        _allDay = Checkbox(Translator.CalendarEventCompose_AllDay, () => ViewModel.IsAllDay = _allDay.State == NSCellStateValue.On);
        _duration = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        _rangeError = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.Critical, 2);
        var startsLabel = FieldLabel(Translator.CalendarEventCompose_Starts);
        var endsLabel = FieldLabel(Translator.CalendarEventCompose_Ends);
        var startsRow = WinoLayout.HStack(8, startsLabel, _startDate, _startTime, _allDay);
        var endsRow = WinoLayout.HStack(8, endsLabel, _endDate, _endTime, _duration);
        var whenColumn = WinoLayout.VStack(8, startsRow, endsRow, _rangeError);
        var whenRow = Row(WinoIconGlyph.Clock, whenColumn);

        _repeat = Popup(Translator.CalendarEventCompose_Repeat);
        _repeat.Activated += (_, _) => Select(ViewModel.RepeatOptions, _repeat, option => ViewModel.SelectedRepeatOption = option);
        _recurrenceSummary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 2);
        var repeatRow = Row(WinoIconGlyph.CalendarEventRepeat, WinoLayout.VStack(6, _repeat, _recurrenceSummary));

        _location = new NSTextField { PlaceholderString = Translator.CalendarEventCompose_LocationPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        _location.Changed += (_, _) => ViewModel.Location = _location.StringValue;
        var locationRow = Row(WinoIconGlyph.Location, _location);

        _notes = new NSTextView { Font = NSFont.SystemFontOfSize(13), RichText = false, AutomaticSpellingCorrectionEnabled = ViewModel.IsComposerAutoCorrectEnabled, ContinuousSpellCheckingEnabled = ViewModel.IsComposerSpellCheckEnabled };
        _notes.TextContainerInset = new CGSize(6, 8);
        _notes.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _notes.TextContainer!.WidthTracksTextView = true;
        _notes.VerticallyResizable = true;
        var notesScroll = new NSScrollView { DocumentView = _notes, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };
        var notesCard = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, Stroke = WinoStyle.GroupStroke, CornerRadius = 6 };
        WinoLayout.Fill(notesScroll, notesCard);
        notesCard.HeightAnchor.ConstraintGreaterThanOrEqualTo(180).Active = true;
        var notesLabel = WinoStyle.Label(Translator.CalendarEventCompose_Notes, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        var notesRow = Row(WinoIconGlyph.Note, WinoLayout.VStack(6, notesLabel, notesCard));

        var form = WinoLayout.VStack(20, titleBlock, whenRow, repeatRow, locationRow, notesRow);
        form.EdgeInsets = new NSEdgeInsets(24, 32, 32, 32);
        foreach (var view in new NSView[] { titleBlock, whenRow, repeatRow, locationRow, notesRow })
            view.WidthAnchor.ConstraintEqualTo(form.WidthAnchor, 1, -64).Active = true;
        _title.WidthAnchor.ConstraintEqualTo(titleBlock.WidthAnchor).Active = true;
        titleRule.WidthAnchor.ConstraintEqualTo(titleBlock.WidthAnchor).Active = true;
        _location.WidthAnchor.ConstraintEqualTo(locationRow.WidthAnchor, 1, -32).Active = true;
        notesCard.WidthAnchor.ConstraintEqualTo(notesRow.WidthAnchor, 1, -32).Active = true;
        var formDocument = new FlippedView();
        WinoLayout.Fill(form, formDocument);
        var formScroll = new NSScrollView { DocumentView = formDocument, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        formDocument.WidthAnchor.ConstraintEqualTo(formScroll.ContentView.WidthAnchor).Active = true;

        // Side pane.
        _attendeesTitle = WinoStyle.Label(Translator.CalendarEventCompose_Attendees, WinoStyle.BodyStrong);
        var attendeesHeader = WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.CalendarAttendees, 16, WinoStyle.SecondaryText), _attendeesTitle);
        _invite = new NSTextField { PlaceholderString = Translator.CalendarEventDetails_InviteSomeone, TranslatesAutoresizingMaskIntoConstraints = false };
        _invite.Activated += (_, _) => Observe(AddAttendeeAsync());
        _attendeeList = WinoLayout.VStack(2);
        _attachmentsTitle = WinoStyle.Label(Translator.CalendarEventDetails_Attachments, WinoStyle.BodyStrong);
        _addAttachment = new NSButton { Title = Translator.CalendarEventCompose_AddAttachment, BezelStyle = NSBezelStyle.Rounded, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        var addBinding = Bindings.Own(new CommandBinding(ViewModel.AddAttachmentsCommand, () => null, enabled => _addAttachment.Enabled = enabled && ViewModel.CanAddAttachments, Dispatcher, ReportError));
        _addAttachment.Activated += (_, _) => addBinding.Execute();
        var attachmentsHeader = WinoLayout.HStack(8, new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText), _attachmentsTitle, WinoLayout.Spacer(), _addAttachment);
        _attachmentList = WinoLayout.VStack(2);
        var side = WinoLayout.VStack(12, attendeesHeader, _invite, _attendeeList, Spacer(12), attachmentsHeader, _attachmentList);
        side.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);
        foreach (var view in new NSView[] { attendeesHeader, _invite, _attendeeList, attachmentsHeader, _attachmentList })
            view.WidthAnchor.ConstraintEqualTo(side.WidthAnchor, 1, -40).Active = true;
        var sideDocument = new FlippedView();
        WinoLayout.Fill(side, sideDocument);
        var sideScroll = new NSScrollView { DocumentView = sideDocument, DrawsBackground = false, HasVerticalScroller = true, AutohidesScrollers = true, BorderType = NSBorderType.NoBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        sideDocument.WidthAnchor.ConstraintEqualTo(sideScroll.ContentView.WidthAnchor).Active = true;
        var sidePane = new WinoSurfaceView { Fill = PaneFill };
        WinoLayout.Fill(sideScroll, sidePane);
        var sideRule = new WinoSeparator(vertical: true);

        root.AddSubview(barCard);
        root.AddSubview(formScroll);
        root.AddSubview(sideRule);
        root.AddSubview(sidePane);
        NSLayoutConstraint.ActivateConstraints(
        [
            barCard.TopAnchor.ConstraintEqualTo(root.TopAnchor, 12),
            barCard.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 12),
            barCard.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -12),
            formScroll.TopAnchor.ConstraintEqualTo(barCard.BottomAnchor, 12),
            formScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            formScroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            sideRule.LeadingAnchor.ConstraintEqualTo(formScroll.TrailingAnchor),
            sideRule.TopAnchor.ConstraintEqualTo(formScroll.TopAnchor),
            sideRule.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            sidePane.LeadingAnchor.ConstraintEqualTo(sideRule.TrailingAnchor),
            sidePane.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            sidePane.TopAnchor.ConstraintEqualTo(formScroll.TopAnchor),
            sidePane.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            sidePane.WidthAnchor.ConstraintEqualTo((nfloat)SidePaneWidth)
        ]);
        View = root;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.GetHtmlNotesAsync = () => Task.FromResult(NotesHtml());
        ViewModel.OnNavigatedTo(mode, parameter!);
        if (parameter is CalendarEventComposeNavigationArgs { NotesHtml: { Length: > 0 } notes }) _notes.Value = StripHtml(notes);

        Bind(nameof(ViewModel.Title), vm => vm.Title, value => { if (_title.StringValue != value) _title.StringValue = value ?? string.Empty; });
        Bind(nameof(ViewModel.Location), vm => vm.Location, value => { if (_location.StringValue != value) _location.StringValue = value ?? string.Empty; });
        Bind(nameof(ViewModel.IsAllDay), vm => vm.IsAllDay, value => { _allDay.State = value ? NSCellStateValue.On : NSCellStateValue.Off; _startTime.Hidden = _endTime.Hidden = value; });
        Bind(nameof(ViewModel.IsPrivate), vm => vm.IsPrivate, value => _private.State = value ? NSCellStateValue.On : NSCellStateValue.Off);
        Bind(nameof(ViewModel.IsOnlineMeeting), vm => vm.IsOnlineMeeting, value => _onlineMeeting.State = value ? NSCellStateValue.On : NSCellStateValue.Off);
        Bind(nameof(ViewModel.StartDate), vm => vm.StartDate, _ => PullDates());
        Bind(nameof(ViewModel.StartTime), vm => vm.StartTime, _ => PullDates());
        Bind(nameof(ViewModel.EndDate), vm => vm.EndDate, _ => PullDates());
        Bind(nameof(ViewModel.EndTime), vm => vm.EndTime, _ => PullDates());
        Bind(nameof(ViewModel.DurationText), vm => vm.DurationText, _ => ApplyDuration());
        Bind(nameof(ViewModel.IsDateRangeValid), vm => vm.IsDateRangeValid, _ => ApplyDuration());
        Bind(nameof(ViewModel.RecurrenceSummary), vm => vm.RecurrenceSummary, value => _recurrenceSummary.StringValue = value ?? string.Empty);
        Bind(nameof(ViewModel.SelectedRepeatOption), vm => vm.SelectedRepeatOption, option => { if (option is not null) _repeat.SelectItem(option.DisplayText); });
        Bind(nameof(ViewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption, option => { if (option is not null) _showAs.SelectItem(option.DisplayText); });
        Bind(nameof(ViewModel.SelectedReminderOption), vm => vm.SelectedReminderOption, option => { if (option is not null) _reminder.SelectItem(option.DisplayText); });
        Bind(nameof(ViewModel.SelectedCalendar), vm => vm.SelectedCalendar, _ => ApplyCalendar());
        Bind(nameof(ViewModel.SaveButtonText), vm => vm.SaveButtonText, text => _save.Title = text ?? Translator.Buttons_Save);
        Bind(nameof(ViewModel.AttendeeCount), vm => vm.AttendeeCount, _ => ApplyAttendees());
        Bind(nameof(ViewModel.AttachmentCount), vm => vm.AttachmentCount, _ => ApplyAttachments());
        Bind(nameof(ViewModel.CanAddOnlineMeeting), vm => vm.CanAddOnlineMeeting, _ => ApplyCalendar());
        Bind(nameof(ViewModel.LastCreatedResult), vm => vm.LastCreatedResult, result => { if (result is not null) CloseRequested?.Invoke(this, EventArgs.Empty); });
        FillPopups();
        View.Window?.MakeFirstResponder(_title);
        return Task.CompletedTask;
    }

    private void Bind<TValue>(string property, Func<CalendarEventComposePageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<CalendarEventComposePageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private void FillPopups()
    {
        _showAs.RemoveAllItems();
        foreach (var option in ViewModel.ShowAsOptions) _showAs.AddItem(option.DisplayText);
        _reminder.RemoveAllItems();
        foreach (var option in ViewModel.ReminderOptions) _reminder.AddItem(option.DisplayText);
        _repeat.RemoveAllItems();
        foreach (var option in ViewModel.RepeatOptions) _repeat.AddItem(option.DisplayText);
        if (ViewModel.SelectedShowAsOption is { } showAs) _showAs.SelectItem(showAs.DisplayText);
        if (ViewModel.SelectedReminderOption is { } reminder) _reminder.SelectItem(reminder.DisplayText);
        if (ViewModel.SelectedRepeatOption is { } repeat) _repeat.SelectItem(repeat.DisplayText);
        ApplyCalendar();
    }

    private sealed class CalendarChoice(AccountCalendarViewModel calendar) : NSObject
    {
        public AccountCalendarViewModel Calendar { get; } = calendar;
    }

    private void ApplyCalendar()
    {
        var menu = _calendarPicker.Menu!;
        menu.RemoveAllItems();
        var selectedCalendar = ViewModel.SelectedCalendar;
        foreach (var group in ViewModel.AvailableCalendarGroups)
        {
            // The ViewModel already offers only writable calendars; never list a read-only one as a save target.
            var calendars = group.AccountCalendars.Where(calendar => !calendar.IsReadOnly || ReferenceEquals(calendar, selectedCalendar)).ToList();
            if (calendars.Count == 0) continue;
            var header = new NSMenuItem($"{group.Account.Name} ({group.Account.Address})") { Enabled = false };
            menu.AddItem(header);
            foreach (var calendar in calendars)
            {
                var item = new NSMenuItem(calendar.Name) { RepresentedObject = new CalendarChoice(calendar), IndentationLevel = 1 };
                item.Image = ColorDot(WinoStyle.FromHexString(calendar.BackgroundColorHex) ?? WinoStyle.Accent);
                menu.AddItem(item);
            }
        }
        var selected = ViewModel.SelectedCalendar;
        if (selected is not null)
        {
            foreach (var item in menu.Items)
                if (item.RepresentedObject is CalendarChoice choice && ReferenceEquals(choice.Calendar, selected)) { _calendarPicker.SelectItem(item); break; }
            _calendarDot.Fill = WinoStyle.FromHexString(selected.BackgroundColorHex) ?? WinoStyle.Accent;
        }
        else
        {
            menu.InsertItem(new NSMenuItem(Translator.CalendarEventCompose_SelectCalendar), 0);
            _calendarPicker.SelectItem(0);
        }
        _onlineMeeting.Title = ViewModel.OnlineMeetingProviderText ?? Translator.CalendarEventCompose_OnlineMeetingInfoTitle;
        _onlineMeeting.Hidden = !ViewModel.CanAddOnlineMeeting;
        _addAttachment.Enabled = ViewModel.CanAddAttachments;
        _addAttachment.ToolTip = ViewModel.CanAddAttachments ? null : ViewModel.AttachmentsDisabledTooltipText;
    }

    private static NSImage ColorDot(NSColor color)
        => NSImage.ImageWithSize(new CGSize(12, 12), false, rect => { color.SetFill(); NSBezierPath.FromOvalInRect(rect.Inset(1, 1)).Fill(); return true; });

    private void ApplyDuration()
    {
        bool valid = ViewModel.IsDateRangeValid;
        _rangeError.StringValue = valid ? string.Empty : ViewModel.DateRangeErrorText ?? string.Empty;
        _rangeError.Hidden = valid;
        _duration.StringValue = ViewModel.HasEndDayOffset ? $"{ViewModel.DurationText} · {ViewModel.EndDayOffsetText}" : ViewModel.DurationText ?? string.Empty;
    }

    private void PullDates()
    {
        _applying = true;
        try
        {
            _startDate.DateValue = ToNSDate(ViewModel.StartDate.Date);
            _startTime.DateValue = ToNSDate(ViewModel.StartDate.Date + ViewModel.StartTime);
            _endDate.DateValue = ToNSDate(ViewModel.EndDate.Date);
            _endTime.DateValue = ToNSDate(ViewModel.EndDate.Date + ViewModel.EndTime);
        }
        finally { _applying = false; }
    }

    private void PushDates()
    {
        if (_applying) return;
        var startDate = FromNSDate(_startDate.DateValue).Date;
        var endDate = FromNSDate(_endDate.DateValue).Date;
        var startTime = FromNSDate(_startTime.DateValue).TimeOfDay;
        var endTime = FromNSDate(_endTime.DateValue).TimeOfDay;
        if (ViewModel.StartDate.Date != startDate) ViewModel.StartDate = new DateTimeOffset(startDate, TimeZoneInfo.Local.GetUtcOffset(startDate));
        if (ViewModel.StartTime != startTime) ViewModel.StartTime = startTime;
        if (ViewModel.EndDate.Date != endDate) ViewModel.EndDate = new DateTimeOffset(endDate, TimeZoneInfo.Local.GetUtcOffset(endDate));
        if (ViewModel.EndTime != endTime) ViewModel.EndTime = endTime;
    }

    private static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static NSDate ToNSDate(DateTime local) => NSDate.FromTimeIntervalSinceReferenceDate((DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime() - Reference).TotalSeconds);
    private static DateTime FromNSDate(NSDate date) => Reference.AddSeconds(date.SecondsSinceReferenceDate).ToLocalTime();

    private async Task AddAttendeeAsync()
    {
        var text = _invite.StringValue.Trim();
        if (text.Length == 0) return;
        try
        {
            var attendee = await ViewModel.GetAttendeeAsync(text);
            if (attendee is null) { ViewModel.NotifyInvalidEmail(text); return; }
            if (ViewModel.Attendees.Any(existing => string.Equals(existing.Email, attendee.Email, StringComparison.OrdinalIgnoreCase))) { ViewModel.NotifyAddressExists(); return; }
            ViewModel.AddAttendee(attendee);
            _invite.StringValue = string.Empty;
        }
        catch (Exception exception) { ReportError(exception); }
    }

    private void ApplyAttendees()
    {
        Clear(_attendeeList);
        _attendeesTitle.StringValue = ViewModel.HasAttendees ? $"{Translator.CalendarEventCompose_Attendees} {ViewModel.AttendeeCount}" : Translator.CalendarEventCompose_Attendees;
        // Organizer row like Windows.
        if (ViewModel.SelectedCalendar is not null)
            _attendeeList.AddArrangedSubview(PersonRow(ViewModel.OrganizerDisplayName, ViewModel.OrganizerAddress, Translator.CalendarEventCompose_Organizer, null, null));
        foreach (var attendee in ViewModel.Attendees)
        {
            var captured = attendee;
            _attendeeList.AddArrangedSubview(PersonRow(attendee.DisplayName, attendee.Email, attendee.AttendanceTypeText,
                () => ViewModel.ToggleAttendeeOptionalCommand.Execute(captured), () => ViewModel.RemoveAttendeeCommand.Execute(captured)));
        }
        foreach (var row in _attendeeList.ArrangedSubviews) row.WidthAnchor.ConstraintEqualTo(_attendeeList.WidthAnchor).Active = true;
    }

    private static NSView PersonRow(string name, string address, string badge, Action? toggle, Action? remove)
    {
        var picture = new WinoContactPicture(28);
        picture.SetIdentity(string.IsNullOrWhiteSpace(name) ? address : name, address);
        var nameLabel = WinoStyle.Label(string.IsNullOrWhiteSpace(name) ? address : name, WinoStyle.BodyMedium);
        var addressLabel = WinoStyle.Label(address, WinoStyle.Caption, WinoStyle.SecondaryText);
        var text = WinoLayout.VStack(1, nameLabel, addressLabel);
        var badgeButton = new NSButton { Title = badge, BezelStyle = NSBezelStyle.Inline, ControlSize = NSControlSize.Small, Font = NSFont.SystemFontOfSize(11), TranslatesAutoresizingMaskIntoConstraints = false, Enabled = toggle is not null };
        if (toggle is not null) { badgeButton.ToolTip = Translator.CalendarEventCompose_AttendeeTypeTooltip; badgeButton.Activated += (_, _) => toggle(); }
        var children = new List<NSView> { picture, text, WinoLayout.Spacer(), badgeButton };
        if (remove is not null)
        {
            var removeButton = new NSButton { Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12), Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.CalendarEventCompose_RemoveAttendee };
            removeButton.Activated += (_, _) => remove();
            children.Add(removeButton);
        }
        var row = WinoLayout.HStack(10, children.ToArray());
        row.EdgeInsets = new NSEdgeInsets(4, 6, 4, 6);
        return row;
    }

    private void ApplyAttachments()
    {
        Clear(_attachmentList);
        _attachmentsTitle.StringValue = ViewModel.HasAttachments ? $"{Translator.CalendarEventDetails_Attachments} {ViewModel.AttachmentCount}" : Translator.CalendarEventDetails_Attachments;
        foreach (var attachment in ViewModel.Attachments)
        {
            var captured = attachment;
            var icon = new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText);
            var name = WinoStyle.Label(attachment.FileName, WinoStyle.Body);
            var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.TertiaryText);
            var remove = new NSButton { Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12), Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = Translator.CalendarEventCompose_RemoveAttachment };
            remove.Activated += (_, _) => ViewModel.RemoveAttachmentCommand.Execute(captured);
            var row = WinoLayout.HStack(8, icon, name, size, WinoLayout.Spacer(), remove);
            row.EdgeInsets = new NSEdgeInsets(4, 6, 4, 6);
            _attachmentList.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_attachmentList.WidthAnchor).Active = true;
        }
    }

    private static void Clear(NSStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews.ToArray()) { stack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
    }

    private string NotesHtml()
    {
        var text = _notes.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var paragraphs = text.Replace("\r\n", "\n").Split('\n').Select(line => $"<p>{System.Net.WebUtility.HtmlEncode(line)}</p>");
        return string.Concat(paragraphs);
    }

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<br\\s*/?>|</p>|</div>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static void Select<T>(IList<T> options, NSPopUpButton popup, Action<T> apply)
    {
        int index = (int)popup.IndexOfSelectedItem;
        if (index >= 0 && index < options.Count) apply(options[index]);
    }

    private static NSPopUpButton Popup(string tooltip)
        => new() { TranslatesAutoresizingMaskIntoConstraints = false, ToolTip = tooltip };

    private static NSButton Checkbox(string title, Action action)
    {
        var button = WinoCheckbox.Create(title, action);
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        return button;
    }

    private static NSDatePicker DatePicker(NSDatePickerElementFlags elements)
    {
        var picker = new NSDatePicker { DatePickerStyle = NSDatePickerStyle.TextFieldAndStepper, DatePickerElements = elements, Bezeled = true, TranslatesAutoresizingMaskIntoConstraints = false };
        picker.WidthAnchor.ConstraintGreaterThanOrEqualTo(elements == NSDatePickerElementFlags.YearMonthDateDay ? 120 : 84).Active = true;
        return picker;
    }

    private static NSTextField FieldLabel(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, WinoStyle.SecondaryText);
        label.WidthAnchor.ConstraintEqualTo(48).Active = true;
        return label;
    }

    private static NSView Glyph(WinoIconGlyph glyph) => new WinoIconView(glyph, 16, WinoStyle.SecondaryText);

    private static NSView Spacer(double height) => WinoLayout.Size(new NSView { TranslatesAutoresizingMaskIntoConstraints = false }, -1, height);

    private static NSStackView Row(WinoIconGlyph glyph, NSView content)
    {
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText);
        WinoLayout.Size(icon, 20, 20);
        var row = WinoLayout.HStack(12, icon, content);
        row.Alignment = NSLayoutAttribute.Top;
        return row;
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    private sealed class FlippedView : NSView
    {
        public FlippedView() { TranslatesAutoresizingMaskIntoConstraints = false; }
        public override bool IsFlipped => true;
    }
}
