using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Calendar;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Input;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>The command bar choices, the title, when (dates, times, all day, time zone), location and the attachments section.</summary>
internal sealed partial class CalendarEventComposeView
{
    private readonly Gtk.Button _calendar = Gtk.Button.New();
    private readonly CalendarColorDot _calendarDot = new();
    private readonly Gtk.Label _calendarText = Gtk.Label.New(string.Empty);
    private readonly OptionDropDown<ShowAsOption> _showAs = new(Translator.CalendarEventDetails_ShowAs);
    private readonly Gtk.Button _reminder = Gtk.Button.New();
    private readonly Gtk.Label _reminderText = Gtk.Label.New(string.Empty);
    private readonly Gtk.ToggleButton _private = Gtk.ToggleButton.NewWithLabel(Translator.CalendarEventCompose_Private);
    private readonly Gtk.ToggleButton _onlineMeeting = Gtk.ToggleButton.NewWithLabel(Translator.CalendarEventCompose_OnlineMeetingInfoTitle);
    private readonly Gtk.Entry _title = Gtk.Entry.New();
    private DateField? _startDate;
    private DateField? _endDate;
    private readonly WinoTimePicker _startTime = new(Translator.CalendarEventCompose_Starts);
    private readonly WinoTimePicker _endTime = new(Translator.CalendarEventCompose_Ends);
    private readonly Gtk.CheckButton _allDay = Gtk.CheckButton.NewWithLabel(Translator.CalendarEventCompose_AllDay);
    private readonly Gtk.Label _duration = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _rangeError = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _timeZoneRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly OptionDropDown<CalendarComposeTimeZoneOption> _timeZone = new(Translator.CalendarEventCompose_TimeZone);
    private readonly Gtk.Label _localTimeHint = Gtk.Label.New(string.Empty);
    private readonly Gtk.Entry _location = Gtk.Entry.New();
    private readonly Gtk.Label _attachmentsTitle = Gtk.Label.New(Translator.CalendarEventDetails_Attachments);
    private readonly Gtk.Button _addAttachment = Gtk.Button.NewWithLabel(Translator.CalendarEventCompose_AddAttachment);
    private readonly Gtk.Label _dropHint = Gtk.Label.New(Translator.CalendarEventCompose_AttachmentsDropHint);
    private readonly Gtk.Box _attachmentList = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private WinoMenuPopover? _openMenu;
    private BindingScope? _attachmentScope;
    private FileDropTarget? _drop;
    private bool _applying;

    private void BuildCommandBar(Gtk.Box bar)
    {
        var calendarContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        calendarContent.Append(_calendarDot.Root);
        _calendarText.SetEllipsize(Pango.EllipsizeMode.End);
        _calendarText.SetMaxWidthChars(18);
        _calendarText.SetWidthChars(10);
        calendarContent.Append(_calendarText);
        calendarContent.Append(Gtk.Image.NewFromIconName("pan-down-symbolic"));
        _calendar.SetChild(calendarContent);
        _calendar.AddCssClass("flat");
        WinoA11y.Rename(_calendar, Translator.CalendarEventCompose_SelectCalendar);
        WinoA11y.HasPopup(_calendar);

        var reminderContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        reminderContent.Append(WinoIcons.Label(WinoIconGlyph.Reminder, 16));
        _reminderText.SetEllipsize(Pango.EllipsizeMode.End);
        _reminderText.SetMaxWidthChars(16);
        _reminderText.SetWidthChars(10);
        reminderContent.Append(_reminderText);
        reminderContent.Append(Gtk.Image.NewFromIconName("pan-down-symbolic"));
        _reminder.SetChild(reminderContent);
        _reminder.AddCssClass("flat");
        _reminder.SetTooltipText(Translator.CalendarEventDetails_Reminder);
        WinoA11y.Rename(_reminder, Translator.CalendarEventDetails_Reminder);
        WinoA11y.HasPopup(_reminder);

        _private.SetTooltipText(Translator.CalendarEventCompose_PrivateTooltip);
        _onlineMeeting.SetTooltipText(Translator.CalendarEventCompose_OnlineMeetingInfo);
        _showAs.Root.SetTooltipText(Translator.CalendarEventDetails_ShowAs);

        bar.Append(_calendar);
        bar.Append(_showAs.Root);
        bar.Append(_reminder);
        bar.Append(_private);
        bar.Append(_onlineMeeting);
    }

    private void BuildForm(Gtk.Box form)
    {
        _title.SetPlaceholderText(Translator.CalendarEventCompose_TitlePlaceholder);
        _title.AddCssClass("title-2");
        _title.AddCssClass("wino-event-title");
        WinoA11y.Label(_title, Translator.CalendarEventCompose_Title);
        form.Append(_title);

        _startDate = new DateField(Translator.CalendarEventCompose_Starts);
        _endDate = new DateField(Translator.CalendarEventCompose_Ends);
        var startsLabel = FieldLabel(Translator.CalendarEventCompose_Starts);
        var endsLabel = FieldLabel(Translator.CalendarEventCompose_Ends);
        var startsRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        foreach (var part in (Gtk.Widget[])[startsLabel, _startDate.Root, _startTime.Root, _allDay]) startsRow.Append(part);
        _duration.AddCssClass("caption");
        _duration.AddCssClass("dim-label");
        var endsRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        foreach (var part in (Gtk.Widget[])[endsLabel, _endDate.Root, _endTime.Root, _duration]) endsRow.Append(part);
        _rangeError.AddCssClass("error");
        _rangeError.AddCssClass("caption");
        _rangeError.SetXalign(0);
        _rangeError.SetWrap(true);
        _rangeError.SetVisible(false);
        var globe = WinoIcons.Label(WinoIconGlyph.Globe, 14);
        globe.AddCssClass("dim-label");
        _timeZone.Root.SetSizeRequest(-1, -1);
        _timeZone.Root.SetHexpand(false);
        _timeZoneRow.Append(globe);
        _timeZoneRow.Append(_timeZone.Root);
        _localTimeHint.AddCssClass("caption");
        _localTimeHint.AddCssClass("dim-label");
        _localTimeHint.SetXalign(0);
        _localTimeHint.SetWrap(true);
        _localTimeHint.SetVisible(false);
        var when = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        foreach (var part in (Gtk.Widget[])[startsRow, endsRow, _rangeError, _timeZoneRow, _localTimeHint]) when.Append(part);
        form.Append(Row(WinoIconGlyph.Clock, when));

        form.Append(_repeatHost);

        _location.SetPlaceholderText(Translator.CalendarEventCompose_LocationPlaceholder);
        WinoA11y.Label(_location, Translator.CalendarEventCompose_LocationPlaceholder);
        form.Append(Row(WinoIconGlyph.Location, _location));

        form.Append(Row(WinoIconGlyph.Note, BuildNotes()));
    }

    private static Gtk.Label FieldLabel(string text)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        label.SetXalign(0);
        label.SetSizeRequest(48, -1);
        return label;
    }

    private void BuildAttachments(Gtk.Box side)
    {
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var icon = WinoIcons.Label(WinoIconGlyph.Attachment, 16);
        icon.AddCssClass("dim-label");
        _attachmentsTitle.AddCssClass("heading");
        _attachmentsTitle.SetXalign(0);
        _attachmentsTitle.SetHexpand(true);
        _addAttachment.AddCssClass("flat");
        header.Append(icon);
        header.Append(_attachmentsTitle);
        header.Append(_addAttachment);
        _dropHint.AddCssClass("caption");
        _dropHint.AddCssClass("dim-label");
        _dropHint.SetXalign(0);
        _dropHint.SetWrap(true);
        side.Append(header);
        side.Append(_dropHint);
        side.Append(_attachmentList);
    }

    private void BindFields()
    {
        Bind.BindText(_title, nameof(CalendarEventComposePageViewModel.Title), static vm => vm.Title, static (vm, text) => vm.Title = text);
        Bind.BindText(_location, nameof(CalendarEventComposePageViewModel.Location), static vm => vm.Location, static (vm, text) => vm.Location = text);
        Bind.BindActive(_allDay, nameof(CalendarEventComposePageViewModel.IsAllDay), static vm => vm.IsAllDay, static (vm, value) => vm.IsAllDay = value);
        Bind.BindActive(_private, nameof(CalendarEventComposePageViewModel.IsPrivate), static vm => vm.IsPrivate, static (vm, value) => vm.IsPrivate = value);
        Bind.BindActive(_onlineMeeting, nameof(CalendarEventComposePageViewModel.IsOnlineMeeting), static vm => vm.IsOnlineMeeting, static (vm, value) => vm.IsOnlineMeeting = value);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.IsAllDay), static vm => vm.IsAllDay, allDay =>
        {
            _startTime.Root.SetVisible(!allDay);
            _endTime.Root.SetVisible(!allDay);
            _timeZoneRow.SetVisible(!allDay);
        });
        Bind.BindAny(static vm => (vm.StartDate, vm.StartTime, vm.EndDate, vm.EndTime), _ => PullDates());
        Bind.BindAny(static vm => (vm.DurationText, vm.IsDateRangeValid, vm.HasEndDayOffset, vm.EndDayOffsetText, vm.DateRangeErrorText), _ => ApplyDuration());
        Bind.BindAny(static vm => (vm.LocalTimeHintText, vm.HasLocalTimeHint), _ =>
        {
            _localTimeHint.SetText(ViewModel.LocalTimeHintText ?? string.Empty);
            _localTimeHint.SetVisible(ViewModel.HasLocalTimeHint);
        });
        Bind.Bind(nameof(CalendarEventComposePageViewModel.CurrentSettings), static vm => vm.CurrentSettings, _ => ApplyClock());
        if (_startDate is { } startDate) startDate.Changed += date => PushDate(start: true, date);
        if (_endDate is { } endDate) endDate.Changed += date => PushDate(start: false, date);
        _startTime.TimeChanged += (_, time) => PushTime(start: true, time);
        _endTime.TimeChanged += (_, time) => PushTime(start: false, time);

        _showAs.Changed += option => ViewModel.SelectedShowAsOption = option;
        Bind.BindCollection(ViewModel.ShowAsOptions, () => _showAs.SetOptions([.. ViewModel.ShowAsOptions], static option => option.DisplayText, ViewModel.SelectedShowAsOption));
        Bind.Bind(nameof(CalendarEventComposePageViewModel.SelectedShowAsOption), static vm => vm.SelectedShowAsOption, option => _showAs.Selected = option);

        _timeZone.Changed += option =>
        {
            if (!ReferenceEquals(option, ViewModel.SelectedTimeZoneOption)) ViewModel.SelectedTimeZoneOption = option;
        };
        Bind.BindCollection(ViewModel.TimeZoneOptions, FillTimeZones);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.SelectedTimeZoneOption), static vm => vm.SelectedTimeZoneOption, option => _timeZone.Selected = option);

        Bind.Bind(nameof(CalendarEventComposePageViewModel.ReminderSummary), static vm => vm.ReminderSummary, summary =>
        {
            _reminderText.SetText(summary ?? Translator.CalendarEventDetails_Reminder);
            _reminder.SetTooltipText(summary ?? Translator.CalendarEventDetails_Reminder);
        });
        Bindings.Own(GtkSignals.OnClicked(_reminder, ShowReminderMenu));
        Bindings.Own(GtkSignals.OnClicked(_calendar, ShowCalendarMenu));
        Bind.BindAny(static vm => (vm.SelectedCalendar, vm.SelectedCalendarDisplayText, vm.CanChangeCalendar, vm.CanAddOnlineMeeting, vm.OnlineMeetingProviderText, vm.CanAddAttachments), _ => ApplyCalendar());

        Bind.BindCommand(_addAttachment, ViewModel.AddAttachmentsCommand);
        Bind.BindCollection(ViewModel.Attachments, ApplyAttachments);
        _drop = Bindings.Own(FileDropTarget.Attach(_sideScroller, capture: false));
        _drop.CanAccept = () => ViewModel.CanAddAttachments;
        _drop.Dropped += (_, paths, _, _) => AddDroppedFiles(paths);
    }

    private void ReleaseFields()
    {
        _openMenu?.Popdown();
        _openMenu = null;
        _startDate?.Dispose();
        _endDate?.Dispose();
        _startTime.Dispose();
        _endTime.Dispose();
        _showAs.Dispose();
        _timeZone.Dispose();
    }

    private void ApplyClock()
    {
        foreach (var picker in (WinoTimePicker[])[_startTime, _endTime])
        {
            picker.Formatter = time => ViewModel.CurrentSettings?.GetTimeString(time) ?? DateTime.Today.Add(time).ToString("t");
            picker.RefreshLabel();
        }
        var first = ViewModel.CurrentSettings?.FirstDayOfWeek ?? DayOfWeek.Monday;
        if (_startDate is { } start) start.FirstDayOfWeek = first;
        if (_endDate is { } end) end.FirstDayOfWeek = first;
    }

    /// <summary>The ViewModel's dates into the controls (Mac PullDates).</summary>
    private void PullDates()
    {
        _applying = true;
        try
        {
            if (_startDate is { } start) start.Date = DateOnly.FromDateTime(ViewModel.StartDate.Date);
            if (_endDate is { } end) end.Date = DateOnly.FromDateTime(ViewModel.EndDate.Date);
            _startTime.Time = ViewModel.StartTime;
            _endTime.Time = ViewModel.EndTime;
        }
        finally { _applying = false; }
    }

    /// <summary>
    /// One changed control into the ViewModel. Only that value is written: the ViewModel moves the end with the start,
    /// and writing the other controls' values afterwards would undo that.
    /// </summary>
    private void PushDate(bool start, DateOnly value)
    {
        if (_applying) return;
        var date = value.ToDateTime(TimeOnly.MinValue);
        var offset = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        if (start && ViewModel.StartDate.Date != date) ViewModel.StartDate = offset;
        else if (!start && ViewModel.EndDate.Date != date) ViewModel.EndDate = offset;
    }

    private void PushTime(bool start, TimeSpan time)
    {
        if (_applying) return;
        if (start && ViewModel.StartTime != time) ViewModel.StartTime = time;
        else if (!start && ViewModel.EndTime != time) ViewModel.EndTime = time;
    }

    private void ApplyDuration()
    {
        var valid = ViewModel.IsDateRangeValid;
        _rangeError.SetText(valid ? string.Empty : ViewModel.DateRangeErrorText ?? string.Empty);
        _rangeError.SetVisible(!valid);
        _duration.SetText(ViewModel.HasEndDayOffset ? $"{ViewModel.DurationText} · {ViewModel.EndDayOffsetText}" : ViewModel.DurationText ?? string.Empty);
    }

    /// <summary>Windows time zone list with the local zone first, so it is one click away.</summary>
    private void FillTimeZones()
    {
        var options = new List<CalendarComposeTimeZoneOption>(ViewModel.TimeZoneOptions.Count + 1);
        var local = ViewModel.TimeZoneOptions.FirstOrDefault(static option => option.Id == TimeZoneInfo.Local.Id);
        if (local is not null) options.Add(local);
        options.AddRange(ViewModel.TimeZoneOptions);
        _timeZone.SetOptions([.. options], static option => option.DisplayText, ViewModel.SelectedTimeZoneOption);
    }

    private void ApplyCalendar()
    {
        var calendar = ViewModel.SelectedCalendar;
        _calendarDot.SetColor(calendar?.BackgroundColorHex, calendar?.Name);
        _calendarText.SetText(ViewModel.SelectedCalendarDisplayText ?? string.Empty);
        _calendar.SetSensitive(ViewModel.CanChangeCalendar);
        _onlineMeeting.SetLabel(ViewModel.OnlineMeetingProviderText ?? Translator.CalendarEventCompose_OnlineMeetingInfoTitle);
        _onlineMeeting.SetVisible(ViewModel.CanAddOnlineMeeting);
        _addAttachment.SetTooltipText(ViewModel.CanAddAttachments ? null : ViewModel.AttachmentsDisabledTooltipText);
        _dropHint.SetVisible(ViewModel.CanAddAttachments && ViewModel.Attachments.Count == 0);
    }

    /// <summary>The calendar choice: account headings, then the account's writable calendars (the selected one even when read-only).</summary>
    private void ShowCalendarMenu()
    {
        var menu = WinoMenus.Create();
        var selected = ViewModel.SelectedCalendar;
        foreach (var group in ViewModel.AvailableCalendarGroups)
        {
            var calendars = group.AccountCalendars.Where(calendar => !calendar.IsReadOnly || ReferenceEquals(calendar, selected)).ToArray();
            if (calendars.Length == 0) continue;
            menu.Separator($"{group.Account.Name} ({group.Account.DisplayAddress})");
            foreach (var calendar in calendars)
            {
                var captured = calendar;
                menu.Radio("calendar", calendar.Name, ReferenceEquals(calendar, selected), () => ViewModel.SelectedCalendar = captured,
                    icon: new WinoMenuIcon(WinoIcons.Glyph(WinoIconGlyph.Calendar), calendar.BackgroundColorHex));
            }
        }
        ShowMenu(_calendar, menu);
    }

    private void ShowReminderMenu()
    {
        var menu = WinoMenus.Create();
        foreach (var option in ViewModel.ReminderOptions)
        {
            var captured = option;
            menu.Check(option.DisplayText, option.IsSelected, _ => captured.IsSelected = !captured.IsSelected);
        }
        menu.Separator();
        menu.Item(Translator.CalendarReminder_Customize, () =>
        {
            if (ViewModel.EditRemindersCommand.CanExecute(null)) ViewModel.EditRemindersCommand.Execute(null);
        });
        ShowMenu(_reminder, menu);
    }

    private void ShowMenu(Gtk.Button anchor, WinoMenuBuilder menu)
    {
        _openMenu?.Popdown();
        _openMenu = ContextMenu.Show(anchor, menu.Build(), anchor.GetWidth() / 2.0, anchor.GetHeight(), () => _openMenu = null);
    }

    private void ApplyAttachments()
    {
        _attachmentScope?.Dispose();
        _attachmentScope = Bindings.Own(new BindingScope());
        while (_attachmentList.GetFirstChild() is { } child) _attachmentList.Remove(child);
        var count = ViewModel.Attachments.Count;
        _attachmentsTitle.SetText(count > 0 ? $"{Translator.CalendarEventDetails_Attachments} {count}" : Translator.CalendarEventDetails_Attachments);
        _dropHint.SetVisible(ViewModel.CanAddAttachments && count == 0);
        foreach (var attachment in ViewModel.Attachments) _attachmentList.Append(AttachmentRow(attachment, _attachmentScope));
    }

    private Gtk.Widget AttachmentRow(CalendarComposeAttachmentViewModel attachment, BindingScope scope)
    {
        var name = Gtk.Label.New(attachment.FileName);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.Middle);
        var details = Gtk.Label.New(attachment.DetailsText);
        details.SetXalign(0);
        details.AddCssClass("caption");
        details.AddCssClass("dim-label");
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.Append(name);
        text.Append(details);
        var remove = WinoIcons.Button(WinoIconGlyph.Dismiss, $"{Translator.CalendarEventCompose_RemoveAttachment} {attachment.FileName}");
        scope.Own(GtkSignals.OnClicked(remove, () => ViewModel.RemoveAttachmentCommand.Execute(attachment)));
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.Append(AttachmentFileIcons.Image(attachment.FileName, 28));
        row.Append(text);
        row.Append(remove);
        row.SetTooltipText(attachment.FileName);
        WinoA11y.Label(row, $"{attachment.FileName}, {attachment.DetailsText}");
        return row;
    }

    /// <summary>
    /// A file dropped on the side pane is attached. A document-portal path is readable only while the portal grants it,
    /// so such a file is copied first (the Mac copies every drop for the same reason).
    /// </summary>
    internal void AddDroppedFiles(string[] paths)
    {
        if (!ViewModel.CanAddAttachments) return;
        foreach (var path in paths)
        {
            try
            {
                var source = path;
                if (path.StartsWith("/run/user/", StringComparison.Ordinal))
                {
                    var folder = Path.Combine(Path.GetTempPath(), "WinoEventAttachments", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    source = Path.Combine(folder, Path.GetFileName(path));
                    File.Copy(path, source);
                }
                ViewModel.TryAddAttachment(source, new FileInfo(source).Length);
            }
            catch (IOException exception) { ReportError(exception); }
            catch (UnauthorizedAccessException exception) { ReportError(exception); }
        }
    }

    /// <summary>A <see cref="Gtk.DropDown"/> over option objects; <see cref="Changed"/> raises for user changes only.</summary>
    private sealed class OptionDropDown<T> : IDisposable where T : class
    {
        private readonly IDisposable _selection;
        private T[] _options = [];
        private bool _applying;

        public OptionDropDown(string accessibleLabel)
        {
            Root = Gtk.DropDown.NewFromStrings([]);
            WinoA11y.Rename(Root, accessibleLabel);
            // GTK names a drop-down after its value again on every selection change.
            _selection = GtkSignals.OnNotify(Root, "selected", () =>
            {
                WinoA11y.Rename(Root, accessibleLabel);
                if (_applying) return;
                var index = Root.GetSelected();
                if (index < _options.Length) Changed?.Invoke(_options[index]);
            });
        }

        public Gtk.DropDown Root { get; }

        public T[] Options => _options;

        public event Action<T>? Changed;

        public T? Selected
        {
            get
            {
                var index = Root.GetSelected();
                return index < _options.Length ? _options[index] : null;
            }
            set
            {
                var index = value is null ? -1 : Array.IndexOf(_options, value);
                _applying = true;
                try { Root.SetSelected(index < 0 ? Gtk.Constants.INVALID_LIST_POSITION : (uint)index); }
                finally { _applying = false; }
            }
        }

        public void SetOptions(T[] options, Func<T, string> text, T? selected)
        {
            _applying = true;
            try
            {
                _options = options;
                Root.SetModel(Gtk.StringList.New([.. options.Select(text)]));
            }
            finally { _applying = false; }
            Selected = selected;
        }

        public void Dispose()
        {
            _selection.Dispose();
            Changed = null;
        }
    }

    /// <summary>A button showing a date; a click opens the date picker popover (CA-02).</summary>
    private sealed class DateField : IDisposable
    {
        private readonly WinoDatePickerPopover _popover;
        private DateOnly _date;

        public DateField(string accessibleLabel)
        {
            Root = Gtk.Button.NewWithLabel(string.Empty);
            WinoA11y.Rename(Root, accessibleLabel);
            WinoA11y.HasPopup(Root);
            _popover = new WinoDatePickerPopover(Root);
            _popover.DateSelected += (_, date) =>
            {
                Date = date;
                Changed?.Invoke(date);
            };
            Clicked = GtkSignals.OnClicked(Root, () =>
            {
                _popover.SelectedDate = _date;
                _popover.Popup();
            });
        }

        public Gtk.Button Root { get; }

        private IDisposable Clicked { get; }

        public DayOfWeek FirstDayOfWeek
        {
            set => _popover.FirstDayOfWeek = value;
        }

        public DateOnly Date
        {
            get => _date;
            set
            {
                _date = value;
                Root.SetLabel(value.ToDateTime(TimeOnly.MinValue).ToString("D"));
            }
        }

        public event Action<DateOnly>? Changed;

        /// <summary>A pick as the popover makes it, for checks without a pointer.</summary>
        public void Pick(DateOnly date)
        {
            Date = date;
            Changed?.Invoke(date);
        }

        public void Dispose()
        {
            Clicked.Dispose();
            Changed = null;
            _popover.Dispose();
        }
    }
}
