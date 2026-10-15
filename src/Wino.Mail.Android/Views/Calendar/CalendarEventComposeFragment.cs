using System.Collections.Specialized;
using System.Globalization;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.DatePicker;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TimePicker;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Calendar;

/// <summary>
/// The event editor as a full-screen dialog (canvas Material pass board EventEditor; tablet board EventEditorTablet;
/// Windows CalendarEventComposePage) over the shared <see cref="CalendarEventComposePageViewModel"/>: Close and the filled
/// Save (Send with attendees) in the bar; the title; All day with the start and end rows, whose dates open the
/// MaterialDatePicker and times the MaterialTimePicker; time zone and repeat; attendees, location, the online meeting,
/// reminder, calendar, notes, show as, private and attachments. Choices open the Wino choice sheet. Validation, ranges and
/// creation are the ViewModel's. A pushed page: full screen on every width, the column capped and centred on wide ones.
/// </summary>
public sealed class CalendarEventComposeFragment : WinoFragment<CalendarEventComposePageViewModel>
{
    private const string NotesStateKey = "calendar-compose-notes";

    private LinearLayout? _column;
    private MaterialButton? _save;
    private CalendarFormRow? _startRow;
    private CalendarFormRow? _endRow;
    private ChipGroup? _attendees;
    private ChipGroup? _attachments;
    private string _originalNotesHtml = string.Empty;
    private bool _notesEdited;

    protected override int LayoutResource => Resource.Layout.fragment_calendar_form;

    private static CultureInfo Culture(CalendarEventComposePageViewModel viewModel) => viewModel.CurrentSettings?.CultureInfo ?? CultureInfo.CurrentCulture;

    protected override void OnBind(View view, CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        var context = view.RequireContext();
        WinoInsets.Apply(view.FindViewById(Resource.Id.calendar_form_root) ?? view, top: true, bottom: true);
        _column = view.FindViewById<LinearLayout>(Resource.Id.calendar_form_column);
        if (view.FindViewById<WinoMaxWidthLayout>(Resource.Id.calendar_form_width) is { } width) width.MaxWidthDp = WinoTokens.Dp(context, Resource.Dimension.wino_calendar_editor_max_width);
        if (_column is null) return;

        _originalNotesHtml = (Entry?.Parameter as CalendarEventComposeNavigationArgs)?.NotesHtml ?? string.Empty;
        viewModel.GetHtmlNotesAsync = NotesHtmlAsync;

        BuildBar(view, context, viewModel, dispatcher);
        BuildTitleAndTimes(context, viewModel, dispatcher);
        _column.AddView(CalendarFormRow.Divider(context));
        BuildPeopleAndPlace(context, viewModel, dispatcher);
        BuildDetails(context, viewModel, dispatcher);
    }

    // ---- Bar: Close and the filled Save ----

    private void BuildBar(View view, Context context, CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        if (view.FindViewById<MaterialToolbar>(Resource.Id.calendar_form_toolbar) is not { } toolbar) return;
        toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_close);
        toolbar.NavigationContentDescription = Translator.Buttons_Close;
        toolbar.NavigationClick += (_, _) => Close();
        _save = new MaterialButton(context, null, Resource.Attribute.materialButtonStyle);
        WinoMaterialAccent.Tint(_save, filled: true);
        var parameters = new MaterialToolbar.LayoutParams(ViewGroup.LayoutParams.WrapContent, WinoTokens.Px(context, Resource.Dimension.wino_size_control), (int)GravityFlags.End)
        {
            MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_m)
        };
        toolbar.AddView(_save, parameters);
        BindCommand(_save, viewModel.CreateCommand, dispatcher);
        Bind(viewModel, dispatcher, nameof(viewModel.SaveButtonText), vm => vm.SaveButtonText, text => _save.Text = text);
        Bind(viewModel, dispatcher, nameof(viewModel.HasAttendees), vm => vm.SaveButtonText, text => _save.Text = text);
    }

    /// <summary>Close leaves without saving (the ViewModel's Cancel would step back in the shell's history instead).</summary>
    private void Close() => MainApplication.Services?.GetService<AndroidNavigationService>()?.TryHandleBack();

    // ---- Title, all day, start, end, time zone, repeat ----

    private void BuildTitleAndTimes(Context context, CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        var title = new EditText(context) { Hint = Translator.CalendarEventCompose_TitlePlaceholder };
        StyleEditorTitle(context, title);
        Bind(viewModel, dispatcher, nameof(viewModel.Title), vm => vm.Title, value => { if (title.Text != value) title.Text = value; });
        title.ContentDescription = Translator.CalendarEventCompose_Title;
        title.AfterTextChanged += (_, _) => { if (viewModel.Title != title.Text) viewModel.Title = title.Text ?? string.Empty; };
        _column!.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var allDay = new CalendarFormRow(context, WinoIconGlyph.Clock) { Text = Translator.QuickEventDialog_IsAllDay };
        var allDaySwitch = new MaterialSwitch(context) { ContentDescription = Translator.QuickEventDialog_IsAllDay };
        WinoMaterialAccent.Tint(allDaySwitch);
        allDaySwitch.CheckedChange += (_, args) => { if (viewModel.IsAllDay != args.IsChecked) viewModel.IsAllDay = args.IsChecked; };
        allDay.SetTrailing(allDaySwitch);
        allDay.OnTap(() => allDaySwitch.Toggle());
        _column.AddView(allDay);
        Bind(viewModel, dispatcher, nameof(viewModel.IsAllDay), vm => vm.IsAllDay, value => { allDaySwitch.Checked = value; ApplyTimes(context, viewModel); });

        _startRow = TimeRow(context, viewModel, start: true);
        _endRow = TimeRow(context, viewModel, start: false);
        _column.AddView(_startRow);
        _column.AddView(_endRow);
        var timeProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(viewModel.StartDate), nameof(viewModel.StartTime), nameof(viewModel.EndDate), nameof(viewModel.EndTime),
            nameof(viewModel.DurationText), nameof(viewModel.DateRangeErrorText), nameof(viewModel.IsDateRangeValid), nameof(viewModel.CurrentSettings)
        };
        System.ComponentModel.PropertyChangedEventHandler timesChanged = (_, args) =>
        {
            if (args.PropertyName is null || timeProperties.Contains(args.PropertyName)) View?.Post(() => ApplyTimes(context, viewModel));
        };
        viewModel.PropertyChanged += timesChanged;
        Bindings.Own(new ActionDisposable(() => viewModel.PropertyChanged -= timesChanged));
        ApplyTimes(context, viewModel);

        var zone = new CalendarFormRow(context, WinoIconGlyph.Globe, compact: true) { ShowsGlyph = false };
        zone.OnTap(() => Pick(context, Translator.CalendarEventCompose_TimeZone, viewModel.TimeZoneOptions.ToList(), option => option.DisplayText,
            viewModel.SelectedTimeZoneOption, option => viewModel.SelectedTimeZoneOption = option));
        _column.AddView(zone);
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedTimeZoneOption), vm => vm.SelectedTimeZoneOption?.DisplayText, text => { zone.Text = text; zone.Supporting = viewModel.LocalTimeHintText; });

        var repeat = new CalendarFormRow(context, WinoIconGlyph.CalendarEventRepeat, compact: true) { ShowsGlyph = false };
        // The custom rule editor is not on Android; the preset rules are (Windows repeat dropdown).
        repeat.OnTap(() => Pick(context, Translator.CalendarEventCompose_Repeat,
            viewModel.RepeatOptions.Where(option => option.Kind != CalendarComposeRepeatKind.Custom || viewModel.IsCustomRecurrence).ToList(),
            option => option.DisplayText, viewModel.SelectedRepeatOption, option => viewModel.SelectedRepeatOption = option));
        _column.AddView(repeat);
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedRepeatOption), vm => vm.SelectedRepeatOption?.DisplayText, text => repeat.Text = text ?? Translator.CalendarEventCompose_RepeatNone);
        Bind(viewModel, dispatcher, nameof(viewModel.RecurrenceSummary), vm => vm.RecurrenceSummary, summary => repeat.Supporting = viewModel.IsRecurring ? summary : null);
    }

    private static void StyleEditorTitle(Context context, EditText title)
    {
        title.Background = null;
        title.InputType = InputTypes.ClassText | InputTypes.TextFlagCapSentences;
        title.SetTextSize(global::Android.Util.ComplexUnitType.Px, WinoTokens.PxF(context, Resource.Dimension.wino_calendar_editor_title_text));
        title.SetTextColor(WinoColors.PrimaryText(context));
        title.SetHintTextColor(WinoColors.TertiaryText(context));
        title.SetPadding(WinoTokens.Px(context, Resource.Dimension.wino_calendar_editor_title_indent), WinoTokens.Px(context, Resource.Dimension.wino_space_s),
            WinoTokens.Px(context, Resource.Dimension.wino_space_l), WinoTokens.Px(context, Resource.Dimension.wino_space_l));
    }

    /// <summary>A start or end row: the date (MaterialDatePicker) and, for timed events, the time (MaterialTimePicker).</summary>
    private CalendarFormRow TimeRow(Context context, CalendarEventComposePageViewModel viewModel, bool start)
    {
        var row = new CalendarFormRow(context, WinoIconGlyph.Clock, compact: true) { ShowsGlyph = false };
        var time = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle);
        time.SetTextColor(WinoColors.PrimaryText(context));
        time.Click += (_, _) => PickTime(context, viewModel, start);
        row.SetTrailing(time);
        row.OnTap(() => PickDate(context, viewModel, start));
        row.Tag = time;
        return row;
    }

    private void ApplyTimes(Context context, CalendarEventComposePageViewModel viewModel)
    {
        var culture = Culture(viewModel);
        void Apply(CalendarFormRow? row, DateTimeOffset date, TimeSpan time, string label, string? supporting)
        {
            if (row is null) return;
            row.Text = date.ToString("ddd, d MMM yyyy", culture);
            row.Supporting = supporting;
            if (row.Tag is MaterialButton button)
            {
                button.Visibility = viewModel.IsAllDay ? ViewStates.Gone : ViewStates.Visible;
                button.Text = viewModel.CurrentSettings is { } settings ? settings.GetTimeString(time) : DateTime.Today.Add(time).ToString("t", culture);
                button.ContentDescription = $"{label} {button.Text}";
            }
            row.ContentDescription = $"{label}: {row.Text}";
        }
        Apply(_startRow, viewModel.StartDate, viewModel.StartTime, Translator.CalendarEventCompose_Starts, null);
        Apply(_endRow, viewModel.EndDate, viewModel.EndTime, Translator.CalendarEventCompose_Ends,
            viewModel.IsDateRangeValid ? viewModel.DurationText : viewModel.DateRangeErrorText);
    }

    private void PickDate(Context context, CalendarEventComposePageViewModel viewModel, bool start)
    {
        if (WinoMaterialAccent.FragmentManager(context) is not { IsStateSaved: false } manager) return;
        var current = start ? viewModel.StartDate : viewModel.EndDate;
        long selection = new DateTimeOffset(current.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var picker = MaterialDatePicker.Builder.DatePicker()
            .SetTitleText(start ? Translator.CalendarEventCompose_StartDate : Translator.CalendarEventCompose_EndDate)
            .SetSelection(Java.Lang.Long.ValueOf(selection))
            .Build();
        picker.AddOnPositiveButtonClickListener(new DateListener(value =>
        {
            // The picker answers UTC midnight of the chosen day; the ViewModel keeps its own offset.
            var date = DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime.Date, DateTimeKind.Unspecified);
            try
            {
                if (start) viewModel.StartDate = new DateTimeOffset(date, viewModel.StartDate.Offset);
                else viewModel.EndDate = new DateTimeOffset(date, viewModel.EndDate.Offset);
            }
            catch (Exception exception) { ReportError(exception); }
        }));
        picker.Show(manager, "wino_event_date_picker");
    }

    private void PickTime(Context context, CalendarEventComposePageViewModel viewModel, bool start)
    {
        if (WinoMaterialAccent.FragmentManager(context) is not { IsStateSaved: false } manager) return;
        var current = start ? viewModel.StartTime : viewModel.EndTime;
        bool is24 = viewModel.CurrentSettings?.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour
            || viewModel.CurrentSettings is null && global::Android.Text.Format.DateFormat.Is24HourFormat(context);
        var picker = new MaterialTimePicker.Builder()
            .SetTimeFormat(is24 ? TimeFormat.Clock24h : TimeFormat.Clock12h)
            .SetHour(current.Hours)
            .SetMinute(current.Minutes)
            .SetTitleText(start ? Translator.CalendarEventCompose_StartTime : Translator.CalendarEventCompose_EndTime)
            .Build();
        picker.AddOnPositiveButtonClickListener(new ClickListener(() =>
        {
            var time = new TimeSpan(picker.Hour, picker.Minute, 0);
            try
            {
                if (start) viewModel.StartTime = time;
                else viewModel.EndTime = time;
            }
            catch (Exception exception) { ReportError(exception); }
        }));
        picker.Show(manager, "wino_event_time_picker");
    }

    // ---- Attendees, location, online meeting ----

    private void BuildPeopleAndPlace(Context context, CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        var people = new CalendarFormRow(context, WinoIconGlyph.People) { Text = Translator.CalendarEventDetails_InviteSomeone, IsPlaceholder = true };
        people.OnTap(() => AskAttendee(context, viewModel));
        _column!.AddView(people);
        _attendees = new ChipGroup(context);
        _column.AddView(_attendees, Indented(context));
        NotifyCollectionChangedEventHandler attendeesChanged = (_, _) => View?.Post(() => ApplyAttendees(context, viewModel));
        viewModel.Attendees.CollectionChanged += attendeesChanged;
        Bindings.Own(new ActionDisposable(() => viewModel.Attendees.CollectionChanged -= attendeesChanged));
        ApplyAttendees(context, viewModel);

        _column.AddView(TextRow(context, WinoIconGlyph.Location, Translator.CalendarEventCompose_LocationPlaceholder, viewModel.Location,
            text => viewModel.Location = text, multiLine: false));

        var online = new CalendarFormRow(context, WinoIconGlyph.EventJoinOnline);
        var onlineSwitch = new MaterialSwitch(context);
        WinoMaterialAccent.Tint(onlineSwitch);
        onlineSwitch.CheckedChange += (_, args) => { if (viewModel.IsOnlineMeeting != args.IsChecked) viewModel.IsOnlineMeeting = args.IsChecked; };
        online.SetTrailing(onlineSwitch);
        online.OnTap(() => onlineSwitch.Toggle());
        _column.AddView(online);
        Bind(viewModel, dispatcher, nameof(viewModel.IsOnlineMeeting), vm => vm.IsOnlineMeeting, value =>
        {
            onlineSwitch.Checked = value;
            online.Supporting = value ? Translator.CalendarEventCompose_OnlineMeetingInfo : null;
        });
        Bind(viewModel, dispatcher, nameof(viewModel.CanAddOnlineMeeting), vm => vm.CanAddOnlineMeeting, can =>
        {
            online.Visibility = can ? ViewStates.Visible : ViewStates.Gone;
            online.Text = viewModel.OnlineMeetingProviderText;
            onlineSwitch.ContentDescription = viewModel.OnlineMeetingProviderText;
        });
    }

    private void ApplyAttendees(Context context, CalendarEventComposePageViewModel viewModel)
    {
        if (_attendees is null) return;
        _attendees.RemoveAllViews();
        foreach (var attendee in viewModel.Attendees)
        {
            var chip = new Chip(context)
            {
                Text = attendee.HasDistinctDisplayName ? attendee.DisplayName : attendee.Email,
                CloseIconVisible = true,
                CloseIconContentDescription = Translator.CalendarEventCompose_RemoveAttendee
            };
            chip.Click += (_, _) => viewModel.ToggleAttendeeOptionalCommand.Execute(attendee);
            chip.SetOnCloseIconClickListener(new ClickListener(() => viewModel.RemoveAttendeeCommand.Execute(attendee)));
            chip.ContentDescription = $"{chip.Text}, {attendee.AttendanceTypeText}";
            _attendees.AddView(chip);
        }
        _attendees.Visibility = viewModel.Attendees.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>Asks for an address and adds it through the ViewModel's checks (valid, not already invited).</summary>
    private void AskAttendee(Context context, CalendarEventComposePageViewModel viewModel)
    {
        var field = new EditText(context) { Hint = Translator.CalendarEventCompose_Attendees, InputType = InputTypes.ClassText | InputTypes.TextVariationEmailAddress };
        int padding = WinoTokens.Px(context, Resource.Dimension.wino_space_2xl);
        var frame = new FrameLayout(context);
        frame.SetPadding(padding, WinoTokens.Px(context, Resource.Dimension.wino_space_s), padding, 0);
        frame.AddView(field);
        new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(context)
            .SetTitle(Translator.CalendarEventDetails_InviteSomeone)!
            .SetView(frame)!
            .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => { })!
            .SetPositiveButton(Translator.Buttons_Add, async (_, _) =>
            {
                var address = field.Text?.Trim() ?? string.Empty;
                if (address.Length == 0) return;
                if (viewModel.Attendees.Any(existing => existing.Email.Equals(address, StringComparison.OrdinalIgnoreCase)))
                {
                    viewModel.NotifyAddressExists();
                    return;
                }
                try
                {
                    var attendee = await viewModel.GetAttendeeAsync(address);
                    if (attendee is null) viewModel.NotifyInvalidEmail(address);
                    else viewModel.AddAttendee(attendee);
                }
                catch (Exception exception) { ReportError(exception); }
            })!
            .Show();
    }

    // ---- Reminder, calendar, notes, show as, private, attachments ----

    private void BuildDetails(Context context, CalendarEventComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        var reminder = new CalendarFormRow(context, WinoIconGlyph.Reminder);
        reminder.OnTap(() => Pick(context, Translator.CalendarEventDetails_Reminder, viewModel.ReminderOptions.ToList(), option => option.DisplayText,
            viewModel.SelectedReminderOption, option => viewModel.SelectedReminderOption = option));
        _column!.AddView(reminder);
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedReminderOption), vm => vm.SelectedReminderOption?.DisplayText, text => reminder.Text = text ?? Translator.CalendarReminder_None);

        var calendar = new CalendarFormRow(context, WinoIconGlyph.Calendar);
        calendar.OnTap(() => Pick(context, Translator.CalendarEventCompose_PickCalendarTitle, viewModel.AvailableCalendars.ToList(),
            option => $"{option.Name} · {option.Account?.Address}", viewModel.SelectedCalendar, option => viewModel.SelectedCalendar = option));
        _column.AddView(calendar);
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedCalendar), vm => vm.SelectedCalendar, _ =>
        {
            calendar.Text = viewModel.SelectedCalendarDisplayText;
            calendar.Supporting = viewModel.SelectedCalendarAccountText;
            calendar.ContentDescription = $"{Translator.CalendarEventCompose_SelectCalendar}: {calendar.Text}";
        });

        string notesText = Entry?.State.TryGetValue(NotesStateKey, out var saved) == true && saved is Java.Lang.String text
            ? text.ToString()
            : AndroidX.Core.Text.HtmlCompat.FromHtml(_originalNotesHtml, AndroidX.Core.Text.HtmlCompat.FromHtmlModeCompact)?.ToString().Trim() ?? string.Empty;
        var notesRow = TextRow(context, WinoIconGlyph.TextDescription, Translator.CalendarEventCompose_Notes, notesText, value =>
        {
            _notesEdited = true;
            if (Entry is { } entry) entry.State[NotesStateKey] = new Java.Lang.String(value);
        }, multiLine: true);
        _column.AddView(notesRow);

        var showAs = new CalendarFormRow(context, WinoIconGlyph.CalendarShowAs);
        showAs.OnTap(() => Pick(context, Translator.CalendarEventDetails_ShowAs, viewModel.ShowAsOptions.ToList(), option => option.DisplayText,
            viewModel.SelectedShowAsOption, option => viewModel.SelectedShowAsOption = option));
        _column.AddView(showAs);
        Bind(viewModel, dispatcher, nameof(viewModel.SelectedShowAsOption), vm => vm.SelectedShowAsOption?.DisplayText, text => showAs.Text = text ?? Translator.CalendarEventDetails_ShowAs);

        var privacy = new CalendarFormRow(context, WinoIconGlyph.LockClosed) { Text = Translator.CalendarEventCompose_Private, Supporting = Translator.CalendarEventCompose_PrivateTooltip };
        var privateSwitch = new MaterialSwitch(context) { ContentDescription = Translator.CalendarEventCompose_Private };
        WinoMaterialAccent.Tint(privateSwitch);
        privateSwitch.CheckedChange += (_, args) => { if (viewModel.IsPrivate != args.IsChecked) viewModel.IsPrivate = args.IsChecked; };
        privacy.SetTrailing(privateSwitch);
        privacy.OnTap(() => privateSwitch.Toggle());
        _column.AddView(privacy);
        Bind(viewModel, dispatcher, nameof(viewModel.IsPrivate), vm => vm.IsPrivate, value => privateSwitch.Checked = value);

        var attach = new CalendarFormRow(context, WinoIconGlyph.Attachment) { Text = Translator.CalendarEventCompose_AddAttachment };
        attach.OnTap(() => { if (viewModel.AddAttachmentsCommand.CanExecute(null)) viewModel.AddAttachmentsCommand.Execute(null); });
        _column.AddView(attach);
        Bind(viewModel, dispatcher, nameof(viewModel.CanAddAttachments), vm => vm.CanAddAttachments, can =>
        {
            attach.Enabled = can;
            attach.Alpha = can ? 1f : WinoColors.Fraction(context, Resource.Fraction.wino_m3_disabled_alpha) * 2;
            attach.Supporting = can ? null : viewModel.AttachmentsDisabledTooltipText;
        });
        _attachments = new ChipGroup(context);
        _column.AddView(_attachments, Indented(context));
        NotifyCollectionChangedEventHandler attachmentsChanged = (_, _) => View?.Post(() => ApplyAttachments(context, viewModel));
        viewModel.Attachments.CollectionChanged += attachmentsChanged;
        Bindings.Own(new ActionDisposable(() => viewModel.Attachments.CollectionChanged -= attachmentsChanged));
        ApplyAttachments(context, viewModel);
    }

    private void ApplyAttachments(Context context, CalendarEventComposePageViewModel viewModel)
    {
        if (_attachments is null) return;
        _attachments.RemoveAllViews();
        foreach (var attachment in viewModel.Attachments)
        {
            var chip = new Chip(context) { Text = attachment.FileName, CloseIconVisible = true, CloseIconContentDescription = Translator.CalendarEventCompose_RemoveAttachment };
            chip.SetOnCloseIconClickListener(new ClickListener(() => viewModel.RemoveAttachmentCommand.Execute(attachment)));
            _attachments.AddView(chip);
        }
        _attachments.Visibility = viewModel.Attachments.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>An inline text field row (location, notes) with the leading glyph.</summary>
    private static View TextRow(Context context, WinoIconGlyph glyph, string hint, string? value, Action<string> changed, bool multiLine)
    {
        var row = new CalendarFormRow(context, glyph);
        var field = new EditText(context) { Hint = hint, Text = value ?? string.Empty, Background = null, ContentDescription = hint };
        field.SetTextAppearance(Resource.Style.TextAppearance_WinoKit_BodyLarge);
        field.SetHintTextColor(WinoColors.SecondaryText(context));
        field.SetPadding(0, 0, 0, 0);
        field.InputType = multiLine ? InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences : InputTypes.ClassText | InputTypes.TextFlagCapWords;
        if (!multiLine) field.SetSingleLine(true);
        field.AfterTextChanged += (_, _) => changed(field.Text ?? string.Empty);
        // The field takes the text column; the row's own text line stays empty.
        var texts = (LinearLayout)row.GetChildAt(1)!;
        texts.RemoveAllViews();
        texts.AddView(field, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return row;
    }

    private static LinearLayout.LayoutParams Indented(Context context) => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
    {
        MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_calendar_editor_row_indent),
        MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_l)
    };

    private static void Pick<T>(Context context, string title, List<T> options, Func<T, string> text, T? selected, Action<T> chosen) where T : class
    {
        if (options.Count == 0) return;
        WinoChoiceSheet.Show(context, title, options.ConvertAll(option => text(option)), selected is null ? -1 : options.IndexOf(selected), index =>
        {
            if (index >= 0 && index < options.Count) chosen(options[index]);
        });
    }

    /// <summary>The notes as HTML: unchanged notes go back as they arrived (shared <see cref="CalendarNotesHtmlResolver"/>).</summary>
    private Task<string> NotesHtmlAsync()
    {
        string? current = null;
        if (Entry?.State.TryGetValue(NotesStateKey, out var saved) == true && saved is Java.Lang.String text)
            current = string.Join("<br>", text.ToString().Split('\n').Select(System.Net.WebUtility.HtmlEncode));
        return Task.FromResult(CalendarNotesHtmlResolver.Resolve(_originalNotesHtml, null, current, isEditorReady: current is not null, wasEdited: _notesEdited || current is not null));
    }

    private sealed class DateListener(Action<long> picked) : Java.Lang.Object, IMaterialPickerOnPositiveButtonClickListener
    {
        public void OnPositiveButtonClick(Java.Lang.Object? selection)
        {
            if (selection is Java.Lang.Long value) picked(value.LongValue());
        }
    }

    private sealed class ClickListener(Action action) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => action();
    }
}
