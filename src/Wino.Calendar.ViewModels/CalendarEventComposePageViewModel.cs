using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmailValidation;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels;

public partial class CalendarEventComposePageViewModel : CalendarBaseViewModel
{
    private static readonly TimeSpan DefaultEventDuration = TimeSpan.FromMinutes(30);

    private static readonly DayOfWeek[] WorkWeekDays =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    ];

    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly IMailDialogService _dialogService;
    private readonly IContactService _contactService;
    private readonly IRecipientSuggestionService _recipientSuggestionService;
    private readonly IPreferencesService _preferencesService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly CalendarEventComposeResultValidator _composeResultValidator = new();
    private readonly IAttachmentFileService _attachmentFileService;

    // Set while code writes several date fields at once, so the start-follows-end logic stays quiet.
    private bool _isSyncingDateRange;
    private bool _isApplyingRecurrence;
    private DateTime? _lastEffectiveStart;

    public Func<Task<string>> GetHtmlNotesAsync { get; set; }

    public ObservableCollection<AccountCalendarViewModel> AvailableCalendars { get; } = [];
    public ObservableCollection<GroupedAccountCalendarViewModel> AvailableCalendarGroups { get; } = [];
    public ObservableCollection<CalendarComposeAttendeeViewModel> Attendees { get; } = [];
    public ObservableCollection<CalendarComposeAttachmentViewModel> Attachments { get; } = [];
    public ObservableCollection<ShowAsOption> ShowAsOptions { get; } = [];
    public ObservableCollection<ReminderOption> ReminderOptions { get; } = [];
    public ObservableCollection<int> RecurrenceIntervalOptions { get; } = [];
    public ObservableCollection<CalendarComposeFrequencyOption> RecurrenceFrequencyOptions { get; } = [];
    public ObservableCollection<CalendarComposeWeekdayOption> WeekdayOptions { get; } = [];
    public ObservableCollection<CalendarComposeRepeatOption> RepeatOptions { get; } = [];
    public ObservableCollection<CalendarComposeTimeZoneOption> TimeZoneOptions { get; } = [];

    [ObservableProperty]
    public partial AccountCalendarViewModel SelectedCalendar { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAllDay { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset StartDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan StartTime { get; set; }

    /// <summary>
    /// The last day of the event. For all-day events this day is included in the event.
    /// </summary>
    [ObservableProperty]
    public partial DateTimeOffset EndDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan EndTime { get; set; }

    [ObservableProperty]
    public partial CalendarComposeRepeatOption SelectedRepeatOption { get; set; }

    [ObservableProperty]
    public partial int SelectedRecurrenceInterval { get; set; } = 1;

    [ObservableProperty]
    public partial CalendarComposeFrequencyOption SelectedRecurrenceFrequencyOption { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? RecurrenceEndDate { get; set; }

    [ObservableProperty]
    public partial string RecurrenceSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ReminderOption SelectedReminderOption { get; set; }

    [ObservableProperty]
    public partial ShowAsOption SelectedShowAsOption { get; set; }

    [ObservableProperty]
    public partial CalendarComposeTimeZoneOption SelectedTimeZoneOption { get; set; }

    [ObservableProperty]
    public partial bool IsPrivate { get; set; }

    [ObservableProperty]
    public partial bool IsOnlineMeeting { get; set; }

    [ObservableProperty]
    public partial bool IsDarkWebviewRenderer { get; set; }

    [ObservableProperty]
    public partial CalendarEventComposeResult LastCreatedResult { get; set; }

    public CalendarSettings CurrentSettings { get; }
    public string TimePickerClockIdentifier => CurrentSettings.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour ? "24HourClock" : "12HourClock";

    // CalendarDatePicker works with nullable dates. A cleared picker keeps the previous date.
    public DateTimeOffset? StartDateValue
    {
        get => StartDate;
        set
        {
            if (value.HasValue)
                StartDate = value.Value;
            else
                OnPropertyChanged();
        }
    }

    public DateTimeOffset? EndDateValue
    {
        get => EndDate;
        set
        {
            if (value.HasValue)
                EndDate = value.Value;
            else
                OnPropertyChanged();
        }
    }

    public bool IsDateRangeValid => GetEffectiveEndDateTime() > GetEffectiveStartDateTime();
    public string DateRangeErrorText => IsAllDay
        ? Translator.CalendarEventCompose_ValidationInvalidAllDayRange
        : Translator.CalendarEventCompose_ValidationInvalidTimeRange;
    public string DurationText => BuildDurationText();
    public int EndDayOffset => (EndDate.Date - StartDate.Date).Days;
    public bool HasEndDayOffset => IsDateRangeValid && EndDayOffset > 0;
    public string EndDayOffsetText => EndDayOffset == 1
        ? Translator.CalendarEventCompose_EndsNextDay
        : string.Format(Translator.CalendarEventCompose_EndsDaysLater, EndDayOffset);
    public string LocalTimeHintText => BuildLocalTimeHintText();
    public bool HasLocalTimeHint => !string.IsNullOrEmpty(LocalTimeHintText);

    public bool IsRecurring => SelectedRepeatOption != null && SelectedRepeatOption.Kind != CalendarComposeRepeatKind.None;
    public bool IsCustomRecurrence => SelectedRepeatOption?.Kind == CalendarComposeRepeatKind.Custom;
    public bool IsWeeklyCustomRecurrence => IsCustomRecurrence && SelectedRecurrenceFrequencyOption?.Frequency == CalendarItemRecurrenceFrequency.Weekly;

    public bool HasAttachments => Attachments.Count > 0;
    public int AttachmentCount => Attachments.Count;
    public bool HasAttendees => Attendees.Count > 0;
    public int AttendeeCount => Attendees.Count;
    public string SaveButtonText => HasAttendees ? Translator.Buttons_Send : Translator.Buttons_Save;

    public bool IsSelectedCalendarCalDav => SelectedCalendar?.Account?.ProviderType == MailProviderType.IMAP4 &&
                                            SelectedCalendar.Account.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav;
    public bool CanAddAttachments => !IsSelectedCalendarCalDav;
    public string AttachmentsDisabledTooltipText => IsSelectedCalendarCalDav
        ? Translator.CalendarEventCompose_AttachmentsNotSupportedForCalDav
        : string.Empty;
    public bool CanAddOnlineMeeting => SelectedCalendar?.Account?.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail;
    public string OnlineMeetingProviderText => SelectedCalendar?.Account?.ProviderType == MailProviderType.Gmail
        ? Translator.CalendarEventCompose_OnlineMeetingGoogleMeet
        : Translator.CalendarEventCompose_OnlineMeetingTeams;
    public string SelectedCalendarDisplayText => SelectedCalendar?.Name ?? Translator.CalendarEventCompose_SelectCalendar;
    public string SelectedCalendarAccountText => SelectedCalendar?.Account?.Address ?? string.Empty;
    public string OrganizerDisplayName => string.IsNullOrWhiteSpace(SelectedCalendar?.Account?.SenderName)
        ? SelectedCalendar?.Account?.Name ?? string.Empty
        : SelectedCalendar.Account.SenderName;
    public string OrganizerAddress => SelectedCalendar?.Account?.Address ?? string.Empty;
    public bool IsComposerSpellCheckEnabled => _preferencesService.IsComposerSpellCheckEnabled;
    public bool IsComposerAutoCorrectEnabled => _preferencesService.IsComposerAutoCorrectEnabled;
    public string ComposerSpellCheckLanguageCode => _preferencesService.ComposerSpellCheckLanguageCode;

    public CalendarEventComposePageViewModel(IAccountService accountService,
                                             ICalendarService calendarService,
                                             INavigationService navigationService,
                                             IMailDialogService dialogService,
                                             IContactService contactService,
                                             IPreferencesService preferencesService,
                                             IUnderlyingThemeService underlyingThemeService,
                                             IWinoRequestDelegator winoRequestDelegator,
                                             IAttachmentFileService attachmentFileService = null,
                                             IRecipientSuggestionService recipientSuggestionService = null)
    {
        _accountService = accountService;
        _calendarService = calendarService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _contactService = contactService;
        _preferencesService = preferencesService;
        _underlyingThemeService = underlyingThemeService;
        _winoRequestDelegator = winoRequestDelegator;
        _attachmentFileService = attachmentFileService;
        _recipientSuggestionService = recipientSuggestionService;

        CurrentSettings = _preferencesService.GetCurrentCalendarSettings();
        IsDarkWebviewRenderer = _underlyingThemeService.IsUnderlyingThemeDark();

        Attachments.CollectionChanged += AttachmentsCollectionChanged;
        Attendees.CollectionChanged += AttendeesCollectionChanged;

        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Free));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Tentative));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Busy));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.OutOfOffice));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.WorkingElsewhere));

        foreach (var reminderMinutes in _calendarService.GetPredefinedReminderMinutes().OrderByDescending(x => x))
        {
            ReminderOptions.Add(new ReminderOption(reminderMinutes));
        }

        foreach (var interval in Enumerable.Range(1, 99))
        {
            RecurrenceIntervalOptions.Add(interval);
        }

        RecurrenceFrequencyOptions.Add(new CalendarComposeFrequencyOption(CalendarItemRecurrenceFrequency.Daily, Translator.CalendarEventCompose_FrequencyDay));
        RecurrenceFrequencyOptions.Add(new CalendarComposeFrequencyOption(CalendarItemRecurrenceFrequency.Weekly, Translator.CalendarEventCompose_FrequencyWeek));
        RecurrenceFrequencyOptions.Add(new CalendarComposeFrequencyOption(CalendarItemRecurrenceFrequency.Monthly, Translator.CalendarEventCompose_FrequencyMonth));
        RecurrenceFrequencyOptions.Add(new CalendarComposeFrequencyOption(CalendarItemRecurrenceFrequency.Yearly, Translator.CalendarEventCompose_FrequencyYear));
        SelectedRecurrenceFrequencyOption = GetFrequencyOption(CalendarItemRecurrenceFrequency.Weekly);

        var culture = GetCulture();
        foreach (var dayOfWeek in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            WeekdayOptions.Add(CreateWeekdayOption(dayOfWeek, culture));
        }

        foreach (var kind in Enum.GetValues<CalendarComposeRepeatKind>())
        {
            RepeatOptions.Add(new CalendarComposeRepeatOption(kind));
        }

        SelectedRepeatOption = RepeatOptions[0];

        LoadTimeZoneOptions();

        SelectedReminderOption = GetDefaultReminderOption();
        SelectedShowAsOption = ShowAsOptions.FirstOrDefault(option => option.ShowAs == CalendarItemShowAs.Busy);

        var (defaultStart, defaultEnd) = GetDefaultComposeDateRange();
        ApplyDateRange(defaultStart, defaultEnd, false);
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await LoadAvailableCalendarsAsync();

        var args = parameters as CalendarEventComposeNavigationArgs;
        ApplyNavigationArgs(args);
        UpdateRecurrenceSummary();
    }

    partial void OnSelectedCalendarChanged(AccountCalendarViewModel value)
    {
        if (value == null)
            return;

        SelectedShowAsOption = ShowAsOptions.FirstOrDefault(option => option.ShowAs == value.DefaultShowAs)
                               ?? ShowAsOptions.FirstOrDefault();

        if (IsSelectedCalendarCalDav && Attachments.Count > 0)
        {
            Attachments.Clear();
        }

        if (!CanAddOnlineMeeting)
        {
            IsOnlineMeeting = false;
        }

        OnPropertyChanged(nameof(IsSelectedCalendarCalDav));
        OnPropertyChanged(nameof(CanAddAttachments));
        OnPropertyChanged(nameof(AttachmentsDisabledTooltipText));
        OnPropertyChanged(nameof(CanAddOnlineMeeting));
        OnPropertyChanged(nameof(OnlineMeetingProviderText));
        OnPropertyChanged(nameof(SelectedCalendarDisplayText));
        OnPropertyChanged(nameof(SelectedCalendarAccountText));
        OnPropertyChanged(nameof(OrganizerDisplayName));
        OnPropertyChanged(nameof(OrganizerAddress));
    }

    partial void OnIsAllDayChanged(bool value)
    {
        if (!_isSyncingDateRange)
        {
            _isSyncingDateRange = true;

            try
            {
                if (value)
                    NormalizeRangeForAllDay();
                else
                    NormalizeRangeForTimedEvent();
            }
            finally
            {
                _isSyncingDateRange = false;
            }

            _lastEffectiveStart = GetEffectiveStartDateTime();
        }

        OnPropertyChanged(nameof(DateRangeErrorText));
        UpdateDateState();
    }

    partial void OnStartDateChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(StartDateValue));

        if (IsWeeklyCustomRecurrence && WeekdayOptions.All(option => !option.IsSelected))
        {
            SelectSingleWeekday(value.DayOfWeek);
        }

        RefreshRepeatOptionTexts();
        MoveEndWithStart();
        UpdateDateState();
    }

    partial void OnStartTimeChanged(TimeSpan value)
    {
        MoveEndWithStart();
        UpdateDateState();
    }

    partial void OnEndDateChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(EndDateValue));
        UpdateDateState();
    }

    partial void OnEndTimeChanged(TimeSpan value) => UpdateDateState();

    partial void OnSelectedTimeZoneOptionChanged(CalendarComposeTimeZoneOption value) => UpdateDateState();

    partial void OnSelectedRepeatOptionChanged(CalendarComposeRepeatOption oldValue, CalendarComposeRepeatOption newValue)
    {
        // Custom starts from what the user already picked, so switching to it never loses the rule.
        if (!_isApplyingRecurrence &&
            newValue?.Kind == CalendarComposeRepeatKind.Custom &&
            oldValue != null &&
            oldValue.Kind is not CalendarComposeRepeatKind.None and not CalendarComposeRepeatKind.Custom)
        {
            var (frequency, interval, days) = GetRecurrencePattern(oldValue.Kind);
            SetCustomRecurrence(frequency, interval, days);
        }

        if (!_isApplyingRecurrence &&
            newValue?.Kind == CalendarComposeRepeatKind.Custom &&
            IsWeeklyCustomRecurrence &&
            WeekdayOptions.All(option => !option.IsSelected))
        {
            SelectSingleWeekday(StartDate.DayOfWeek);
        }

        OnPropertyChanged(nameof(IsRecurring));
        OnPropertyChanged(nameof(IsCustomRecurrence));
        OnPropertyChanged(nameof(IsWeeklyCustomRecurrence));
        UpdateRecurrenceSummary();
    }

    partial void OnSelectedRecurrenceIntervalChanged(int value) => UpdateRecurrenceSummary();

    partial void OnSelectedRecurrenceFrequencyOptionChanged(CalendarComposeFrequencyOption value)
    {
        if (!_isApplyingRecurrence &&
            value?.Frequency == CalendarItemRecurrenceFrequency.Weekly &&
            WeekdayOptions.All(option => !option.IsSelected))
        {
            SelectSingleWeekday(StartDate.DayOfWeek);
        }

        OnPropertyChanged(nameof(IsWeeklyCustomRecurrence));
        UpdateRecurrenceSummary();
    }

    partial void OnRecurrenceEndDateChanged(DateTimeOffset? value) => UpdateRecurrenceSummary();

    [RelayCommand]
    private async Task AddAttachmentsAsync()
    {
        if (!CanAddAttachments)
            return;

        var pickedFiles = await _dialogService.PickFilesMetadataAsync("*");
        if (pickedFiles.Count == 0)
            return;

        await ExecuteUIThread(() =>
        {
            foreach (var file in pickedFiles)
            {
                TryAddAttachment(file.FileName, file.FullFilePath, file.FileExtension, file.Size);
            }
        });
    }

    public bool TryAddAttachment(string filePath, long size)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var fileName = Path.GetFileName(filePath);
        var fileExtension = Path.GetExtension(filePath);
        return TryAddAttachment(fileName, filePath, fileExtension, size);
    }

    [RelayCommand]
    private void RemoveAttachment(CalendarComposeAttachmentViewModel attachment)
    {
        if (attachment == null)
            return;

        Attachments.Remove(attachment);
    }

    [RelayCommand]
    private void ClearRecurrenceEndDate()
    {
        RecurrenceEndDate = null;
    }

    [RelayCommand]
    private void RemoveOnlineMeeting()
    {
        IsOnlineMeeting = false;
    }

    [RelayCommand]
    private void ToggleAttendeeOptional(CalendarComposeAttendeeViewModel attendee)
    {
        if (attendee == null)
            return;

        attendee.IsOptional = !attendee.IsOptional;
    }

    [RelayCommand]
    private void Cancel()
    {
        _navigationService.GoBack();
    }

    private bool CanCreate() => IsDateRangeValid;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var uniqueAttendees = Attendees
            .GroupBy(attendee => attendee.Email, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        CalendarEventComposeResult createdResult;

        try
        {
            createdResult = await BuildResultAsync(uniqueAttendees);
            _composeResultValidator.Validate(createdResult);
        }
        catch (CalendarEventComposeValidationException ex)
        {
            ShowValidationMessage(ex.Message);
            return;
        }

        LastCreatedResult = createdResult;

        await _winoRequestDelegator.ExecuteAsync(new CalendarOperationPreparationRequest(
            CalendarSynchronizerOperation.CreateEvent,
            ComposeResult: createdResult));

        NavigateBackToCalendar(createdResult.StartDate);
    }

    private void NavigateBackToCalendar(DateTime targetDate)
    {
        _navigationService.Navigate(
            WinoPage.CalendarPage,
            new CalendarPageNavigationArgs
            {
                NavigationDate = targetDate,
                ForceReload = true
            });
    }

    public async Task<List<AccountContact>> SearchContactsAsync(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText) || queryText.Length < 2)
            return [];

        var accountId = SelectedCalendar?.Account?.Id;

        // Attendees are people, so contact lists are left out; ranking matches the mail composer.
        if (_recipientSuggestionService != null)
            return [.. await _recipientSuggestionService.SuggestAsync(accountId, queryText, includeLists: false).ConfigureAwait(false)];

        return await _contactService.ResolveRecipientCandidatesAsync(accountId, queryText).ConfigureAwait(false) ?? [];
    }

    public async Task<CalendarComposeAttendeeViewModel> GetAttendeeAsync(string tokenText)
    {
        if (!EmailValidator.Validate(tokenText))
            return null;

        var existing = Attendees.Any(attendee => attendee.Email.Equals(tokenText, StringComparison.OrdinalIgnoreCase));
        if (existing)
            return null;

        var info = await _contactService.GetContactByAddressAsync(SelectedCalendar?.Account?.Id, tokenText).ConfigureAwait(false);
        if (info != null)
        {
            return CalendarComposeAttendeeViewModel.FromContact(info);
        }

        return new CalendarComposeAttendeeViewModel(string.Empty, tokenText);
    }

    public void AddAttendee(CalendarComposeAttendeeViewModel attendee)
    {
        if (Attendees.Any(existing => existing.Email.Equals(attendee.Email, StringComparison.OrdinalIgnoreCase)))
            return;

        Attendees.Add(attendee);
    }

    [RelayCommand]
    private void RemoveAttendee(CalendarComposeAttendeeViewModel attendee)
    {
        if (attendee == null)
            return;

        Attendees.Remove(attendee);
    }

    public void NotifyAddressExists()
    {
        _dialogService.InfoBarMessage(
            Translator.Info_ContactExistsTitle,
            Translator.Info_ContactExistsMessage,
            InfoBarMessageType.Warning);
    }

    public void NotifyInvalidEmail(string address)
    {
        _dialogService.InfoBarMessage(
            Translator.Info_InvalidAddressTitle,
            string.Format(Translator.Info_InvalidAddressMessage, address),
            InfoBarMessageType.Warning);
    }

    private async Task LoadAvailableCalendarsAsync()
    {
        var accountCalendars = new List<AccountCalendarViewModel>();
        var groupedCalendars = new List<GroupedAccountCalendarViewModel>();
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

        foreach (var account in accounts)
        {
            if (!GroupedAccountCalendarViewModel.SupportsCalendar(account))
                continue;

            var calendars = await _calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
            var viewModels = calendars
                .Select(calendar => new AccountCalendarViewModel(account, calendar))
                .ToList();

            accountCalendars.AddRange(viewModels);

            if (viewModels.Count > 0)
            {
                groupedCalendars.Add(new GroupedAccountCalendarViewModel(account, viewModels));
            }
        }

        await ExecuteUIThread(() =>
        {
            AvailableCalendars.Clear();
            AvailableCalendarGroups.Clear();

            foreach (var calendar in accountCalendars.OrderBy(calendar => calendar.Account.Name).ThenBy(calendar => calendar.Name))
            {
                AvailableCalendars.Add(calendar);
            }

            foreach (var group in groupedCalendars.OrderBy(group => group.Account.Name))
            {
                AvailableCalendarGroups.Add(group);
            }
        });
    }

    private void ApplyNavigationArgs(CalendarEventComposeNavigationArgs args)
    {
        var (defaultStart, defaultEnd) = GetDefaultComposeDateRange();
        var startDate = args?.StartDate != default ? args!.StartDate : defaultStart;
        var endDate = args?.EndDate != default ? args!.EndDate : defaultEnd;
        var isAllDay = args?.IsAllDay ?? false;

        Title = args?.Title ?? string.Empty;
        Location = args?.Location ?? string.Empty;

        ApplyDateRange(startDate, endDate, isAllDay);

        SelectedCalendar = ResolveSelectedCalendar(args?.SelectedCalendarId);
        if (SelectedCalendar != null)
        {
            SelectedShowAsOption = ShowAsOptions.FirstOrDefault(option => option.ShowAs == SelectedCalendar.DefaultShowAs)
                                   ?? SelectedShowAsOption
                                   ?? ShowAsOptions.FirstOrDefault();
        }

        if (args?.ShowAs is CalendarItemShowAs importedShowAs)
        {
            SelectedShowAsOption = ShowAsOptions.FirstOrDefault(option => option.ShowAs == importedShowAs)
                                   ?? SelectedShowAsOption;
        }

        Attendees.Clear();
        foreach (var attendee in args?.Attendees ?? [])
        {
            if (string.IsNullOrWhiteSpace(attendee.Email) ||
                Attendees.Any(existing => existing.Email.Equals(attendee.Email, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Attendees.Add(new CalendarComposeAttendeeViewModel(attendee.Name, attendee.Email));
        }

        ApplyImportedRecurrence(args?.Recurrence);
        ApplyImportedReminder(
            args?.ReminderMinutesBeforeStart,
            isImportedEvent: args?.RequireCalendarPickerWhenUnresolved == true);
    }

    private void ApplyImportedRecurrence(CalendarEventRecurrenceDraft recurrence)
    {
        _isApplyingRecurrence = true;

        try
        {
            SetCustomRecurrence(CalendarItemRecurrenceFrequency.Weekly, 1, Array.Empty<DayOfWeek>());
            RecurrenceEndDate = null;

            if (recurrence == null)
            {
                SelectedRepeatOption = GetRepeatOption(CalendarComposeRepeatKind.None);
                return;
            }

            var frequency = recurrence.Frequency;
            var interval = Math.Clamp(recurrence.Interval, 1, 99);
            var days = recurrence.Weekdays?.Distinct().ToList() ?? [];

            // Daily limited to some weekdays is the same rule as weekly on those days.
            // Providers such as Outlook ignore BYDAY on daily rules, so it is not kept there.
            if (frequency == CalendarItemRecurrenceFrequency.Daily && days.Count > 0)
            {
                if (interval == 1)
                    frequency = CalendarItemRecurrenceFrequency.Weekly;
                else
                    days = [];
            }

            if (frequency != CalendarItemRecurrenceFrequency.Weekly)
                days = [];

            SetCustomRecurrence(frequency, interval, days);
            RecurrenceEndDate = recurrence.EndDate.HasValue
                ? new DateTimeOffset(recurrence.EndDate.Value.Date)
                : null;

            SelectedRepeatOption = GetRepeatOption(ResolveRepeatKind(frequency, interval, days));
        }
        finally
        {
            _isApplyingRecurrence = false;
        }

        UpdateRecurrenceSummary();
    }

    private CalendarComposeRepeatKind ResolveRepeatKind(CalendarItemRecurrenceFrequency frequency, int interval, IReadOnlyCollection<DayOfWeek> days)
    {
        if (interval != 1)
            return CalendarComposeRepeatKind.Custom;

        return frequency switch
        {
            CalendarItemRecurrenceFrequency.Daily => CalendarComposeRepeatKind.Daily,
            CalendarItemRecurrenceFrequency.Weekly when days.Count == 0 || (days.Count == 1 && days.Contains(StartDate.DayOfWeek)) => CalendarComposeRepeatKind.Weekly,
            CalendarItemRecurrenceFrequency.Weekly when days.Count == WorkWeekDays.Length && WorkWeekDays.All(days.Contains) => CalendarComposeRepeatKind.Weekdays,
            CalendarItemRecurrenceFrequency.Monthly => CalendarComposeRepeatKind.Monthly,
            CalendarItemRecurrenceFrequency.Yearly => CalendarComposeRepeatKind.Yearly,
            _ => CalendarComposeRepeatKind.Custom
        };
    }

    private void ApplyImportedReminder(int? reminderMinutes, bool isImportedEvent)
    {
        if (reminderMinutes is not > 0)
        {
            if (isImportedEvent)
                SelectedReminderOption = null;

            return;
        }

        var reminder = ReminderOptions.FirstOrDefault(option => option.Minutes == reminderMinutes.Value);
        if (reminder == null)
        {
            reminder = new ReminderOption(reminderMinutes.Value, isCustom: true);
            ReminderOptions.Add(reminder);
        }

        SelectedReminderOption = reminder;
    }

    private AccountCalendarViewModel ResolveSelectedCalendar(Guid? selectedCalendarId)
    {
        if (selectedCalendarId.HasValue)
        {
            var selectedCalendar = AvailableCalendars.FirstOrDefault(calendar => calendar.Id == selectedCalendarId.Value);
            if (selectedCalendar != null)
                return selectedCalendar;
        }

        return AvailableCalendars.FirstOrDefault(calendar => calendar.IsPrimary) ?? AvailableCalendars.FirstOrDefault();
    }

    /// <summary>
    /// Applies a range whose end is exclusive, as providers and navigation arguments describe it.
    /// </summary>
    private void ApplyDateRange(DateTime startDate, DateTime endDate, bool isAllDay)
    {
        _isSyncingDateRange = true;

        try
        {
            IsAllDay = isAllDay;
            StartDate = new DateTimeOffset(startDate.Date);
            StartTime = isAllDay ? TimeSpan.Zero : startDate.TimeOfDay;

            if (isAllDay)
            {
                var lastDay = endDate.Date.AddDays(-1);
                EndDate = new DateTimeOffset(lastDay < startDate.Date ? startDate.Date : lastDay);
                EndTime = TimeSpan.Zero;
            }
            else
            {
                if (endDate <= startDate)
                    endDate = startDate.Add(DefaultEventDuration);

                EndDate = new DateTimeOffset(endDate.Date);
                EndTime = endDate.TimeOfDay;
            }
        }
        finally
        {
            _isSyncingDateRange = false;
        }

        _lastEffectiveStart = GetEffectiveStartDateTime();
        RefreshRepeatOptionTexts();
        UpdateDateState();
    }

    /// <summary>
    /// Moving the start keeps the event length, the same way Outlook and Google Calendar do.
    /// </summary>
    private void MoveEndWithStart()
    {
        if (_isSyncingDateRange)
            return;

        var newStart = GetEffectiveStartDateTime();

        if (_lastEffectiveStart is DateTime previousStart && previousStart != newStart)
        {
            var movedEnd = GetEffectiveEndDateTime() + (newStart - previousStart);
            SetEffectiveEnd(movedEnd);
        }

        _lastEffectiveStart = newStart;
    }

    private void SetEffectiveEnd(DateTime end)
    {
        var wasSyncing = _isSyncingDateRange;
        _isSyncingDateRange = true;

        try
        {
            if (IsAllDay)
            {
                EndDate = new DateTimeOffset(end.Date.AddDays(-1));
            }
            else
            {
                EndDate = new DateTimeOffset(end.Date);
                EndTime = end.TimeOfDay;
            }
        }
        finally
        {
            _isSyncingDateRange = wasSyncing;
        }
    }

    private void NormalizeRangeForAllDay()
    {
        // A timed event that ends at midnight does not occupy the next day.
        if (EndTime == TimeSpan.Zero && EndDate.Date > StartDate.Date)
        {
            EndDate = EndDate.AddDays(-1);
        }

        if (EndDate.Date < StartDate.Date)
        {
            EndDate = StartDate;
        }
    }

    private void NormalizeRangeForTimedEvent()
    {
        if (StartTime == TimeSpan.Zero && EndTime == TimeSpan.Zero)
        {
            var (defaultStart, _) = GetDefaultComposeDateRange();
            StartTime = defaultStart.TimeOfDay;
            EndTime = defaultStart.TimeOfDay;
        }

        if (GetEffectiveEndDateTime() <= GetEffectiveStartDateTime())
        {
            SetEffectiveEnd(GetEffectiveStartDateTime().Add(DefaultEventDuration));
        }
    }

    private void UpdateDateState()
    {
        OnPropertyChanged(nameof(IsDateRangeValid));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(EndDayOffset));
        OnPropertyChanged(nameof(HasEndDayOffset));
        OnPropertyChanged(nameof(EndDayOffsetText));
        OnPropertyChanged(nameof(LocalTimeHintText));
        OnPropertyChanged(nameof(HasLocalTimeHint));
        CreateCommand.NotifyCanExecuteChanged();
        UpdateRecurrenceSummary();
    }

    private string BuildDurationText()
    {
        if (!HasInitializedComposeDateRange() || !IsDateRangeValid)
            return string.Empty;

        var duration = GetEffectiveEndDateTime() - GetEffectiveStartDateTime();

        if (IsAllDay)
        {
            var dayCount = (int)Math.Round(duration.TotalDays);
            return dayCount == 1
                ? Translator.CalendarEventCompose_DurationDay
                : string.Format(Translator.CalendarEventCompose_DurationDays, dayCount);
        }

        var parts = new List<string>();

        if (duration.Days > 0)
        {
            parts.Add(duration.Days == 1
                ? Translator.CalendarEventCompose_DurationDay
                : string.Format(Translator.CalendarEventCompose_DurationDays, duration.Days));
        }

        if (duration.Hours > 0)
            parts.Add(string.Format(Translator.CalendarEventCompose_DurationHours, duration.Hours));

        if (duration.Minutes > 0)
            parts.Add(string.Format(Translator.CalendarEventCompose_DurationMinutes, duration.Minutes));

        return string.Join(" ", parts);
    }

    private string BuildLocalTimeHintText()
    {
        if (IsAllDay || !HasInitializedComposeDateRange())
            return string.Empty;

        var selectedTimeZone = SelectedTimeZoneOption?.TimeZone;
        if (selectedTimeZone == null || selectedTimeZone.Id == TimeZoneInfo.Local.Id)
            return string.Empty;

        try
        {
            var start = DateTime.SpecifyKind(GetEffectiveStartDateTime(), DateTimeKind.Unspecified);
            var localStart = TimeZoneInfo.ConvertTime(start, selectedTimeZone, TimeZoneInfo.Local);
            var culture = GetCulture();
            var localText = $"{localStart.ToString("ddd", culture)} {DateTimeDisplayFormatter.FormatTime(localStart, CurrentSettings.DayHeaderDisplayType, culture)}";

            return string.Format(culture, Translator.CalendarEventCompose_LocalTimeHint, localText, TimeZoneInfo.Local.StandardName);
        }
        catch (ArgumentException)
        {
            // The start falls in a daylight-saving gap of the selected time zone.
            return string.Empty;
        }
    }

    private async Task<CalendarEventComposeResult> BuildResultAsync(List<CalendarComposeAttendeeViewModel> uniqueAttendees)
    {
        if (IsRecurring && RecurrenceEndDate.HasValue && RecurrenceEndDate.Value.Date < StartDate.Date)
        {
            throw new CalendarEventComposeValidationException(Translator.CalendarEventCompose_ValidationInvalidRecurrenceEnd);
        }

        var htmlNotes = GetHtmlNotesAsync == null ? string.Empty : await GetHtmlNotesAsync();
        var effectiveStart = GetEffectiveStartDateTime();
        var effectiveEnd = GetEffectiveEndDateTime();

        return new CalendarEventComposeResult
        {
            CalendarId = SelectedCalendar?.Id ?? Guid.Empty,
            AccountId = SelectedCalendar?.Account.Id ?? Guid.Empty,
            Title = Title.Trim(),
            Location = Location?.Trim() ?? string.Empty,
            HtmlNotes = htmlNotes,
            StartDate = effectiveStart,
            EndDate = effectiveEnd,
            IsAllDay = IsAllDay,
            TimeZoneId = GetSelectedTimeZone().Id,
            ShowAs = SelectedShowAsOption?.ShowAs ?? SelectedCalendar?.DefaultShowAs ?? CalendarItemShowAs.Busy,
            Visibility = IsPrivate ? CalendarItemVisibility.Private : CalendarItemVisibility.Public,
            IsOnlineMeeting = CanAddOnlineMeeting && IsOnlineMeeting,
            SelectedReminders = BuildSelectedReminders(),
            Attendees = BuildAttendees(uniqueAttendees),
            Attachments = CanAddAttachments
                ? Attachments.Select(attachment => attachment.ToDraftModel()).ToList()
                : [],
            Recurrence = BuildRecurrenceRule(),
            RecurrenceSummary = RecurrenceSummary
        };
    }

    private List<Reminder> BuildSelectedReminders()
    {
        if (SelectedReminderOption == null)
            return [];

        return
        [
            new Reminder
            {
                Id = Guid.NewGuid(),
                CalendarItemId = Guid.Empty,
                DurationInSeconds = SelectedReminderOption.Minutes * 60L,
                ReminderType = CalendarItemReminderType.Popup
            }
        ];
    }

    private static List<CalendarEventAttendee> BuildAttendees(IEnumerable<CalendarComposeAttendeeViewModel> attendees)
    {
        return attendees
            .Select(attendee => new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                CalendarItemId = Guid.Empty,
                Name = attendee.HasDistinctDisplayName ? attendee.DisplayName : string.Empty,
                Email = attendee.Email,
                AttendenceStatus = AttendeeStatus.NeedsAction,
                IsOrganizer = false,
                IsOptionalAttendee = attendee.IsOptional,
                ResolvedContact = attendee.ResolvedContact
            })
            .ToList();
    }

    private ReminderOption GetDefaultReminderOption()
    {
        var reminderMinutes = Math.Max(1, _preferencesService.DefaultReminderDurationInSeconds / 60);
        return ReminderOptions.FirstOrDefault(option => option.Minutes == reminderMinutes)
               ?? ReminderOptions.FirstOrDefault();
    }

    private void UpdateRecurrenceSummary()
    {
        if (!HasInitializedComposeDateRange() || SelectedRepeatOption == null)
        {
            RecurrenceSummary = string.Empty;
            return;
        }

        var (frequency, interval, days) = GetRecurrencePattern(SelectedRepeatOption.Kind);

        RecurrenceSummary = CalendarRecurrenceSummaryFormatter.BuildSummary(
            IsRecurring,
            GetEffectiveStartDateTime(),
            GetEffectiveEndDateTime(),
            IsAllDay,
            CurrentSettings,
            interval,
            frequency,
            days,
            RecurrenceEndDate);
    }

    private bool HasInitializedComposeDateRange() => StartDate != default && EndDate != default;

    private (CalendarItemRecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> Days) GetRecurrencePattern(CalendarComposeRepeatKind kind)
    {
        return kind switch
        {
            CalendarComposeRepeatKind.Daily => (CalendarItemRecurrenceFrequency.Daily, 1, Array.Empty<DayOfWeek>()),
            CalendarComposeRepeatKind.Weekdays => (CalendarItemRecurrenceFrequency.Weekly, 1, WorkWeekDays),
            CalendarComposeRepeatKind.Weekly => (CalendarItemRecurrenceFrequency.Weekly, 1, new[] { StartDate.DayOfWeek }),
            CalendarComposeRepeatKind.Monthly => (CalendarItemRecurrenceFrequency.Monthly, 1, Array.Empty<DayOfWeek>()),
            CalendarComposeRepeatKind.Yearly => (CalendarItemRecurrenceFrequency.Yearly, 1, Array.Empty<DayOfWeek>()),
            CalendarComposeRepeatKind.Custom => GetCustomRecurrencePattern(),
            _ => (CalendarItemRecurrenceFrequency.Weekly, 1, Array.Empty<DayOfWeek>())
        };
    }

    private (CalendarItemRecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> Days) GetCustomRecurrencePattern()
    {
        var frequency = SelectedRecurrenceFrequencyOption?.Frequency ?? CalendarItemRecurrenceFrequency.Weekly;
        if (frequency != CalendarItemRecurrenceFrequency.Weekly)
            return (frequency, SelectedRecurrenceInterval, Array.Empty<DayOfWeek>());

        var days = WeekdayOptions
            .Where(option => option.IsSelected)
            .Select(option => option.DayOfWeek)
            .ToArray();

        return (frequency, SelectedRecurrenceInterval, days.Length > 0 ? days : new[] { StartDate.DayOfWeek });
    }

    private string BuildRecurrenceRule()
    {
        if (!IsRecurring)
            return string.Empty;

        var (frequency, interval, days) = GetRecurrencePattern(SelectedRepeatOption.Kind);
        var parts = new List<string>
        {
            $"FREQ={frequency.ToString().ToUpperInvariant()}",
            $"INTERVAL={interval}"
        };

        if (days.Count > 0)
        {
            var ruleDays = WeekdayOptions
                .Where(option => days.Contains(option.DayOfWeek))
                .Select(option => option.RuleValue);

            parts.Add($"BYDAY={string.Join(",", ruleDays)}");
        }

        if (RecurrenceEndDate.HasValue)
        {
            parts.Add($"UNTIL={BuildUntilValue(RecurrenceEndDate.Value.Date)}");
        }

        return $"RRULE:{string.Join(";", parts)}";
    }

    private string BuildUntilValue(DateTime lastDay)
    {
        if (IsAllDay)
            return lastDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // RFC 5545 requires a UTC UNTIL when the start carries a time zone.
        var endOfLastDay = DateTime.SpecifyKind(lastDay.AddDays(1).AddSeconds(-1), DateTimeKind.Unspecified);

        DateTime utcUntil;
        try
        {
            utcUntil = TimeZoneInfo.ConvertTimeToUtc(endOfLastDay, GetSelectedTimeZone());
        }
        catch (ArgumentException)
        {
            utcUntil = TimeZoneInfo.ConvertTimeToUtc(endOfLastDay.AddHours(-1), GetSelectedTimeZone());
        }

        return utcUntil.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    private DateTime GetEffectiveStartDateTime()
        => StartDate.Date.Add(IsAllDay ? TimeSpan.Zero : StartTime);

    /// <summary>
    /// Gets the exclusive end: the next midnight for all-day events.
    /// </summary>
    private DateTime GetEffectiveEndDateTime()
        => IsAllDay
            ? EndDate.Date.AddDays(1)
            : EndDate.Date.Add(EndTime);

    private static (DateTime StartDate, DateTime EndDate) GetDefaultComposeDateRange()
    {
        var localNow = DateTime.Now;
        var roundedMinutes = localNow.Minute switch
        {
            < 30 => 30,
            30 when localNow.Second == 0 && localNow.Millisecond == 0 => 30,
            _ => 60
        };

        var startDate = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0);
        startDate = roundedMinutes == 60 ? startDate.AddHours(1) : startDate.AddMinutes(roundedMinutes);

        return (startDate, startDate.Add(DefaultEventDuration));
    }

    private void LoadTimeZoneOptions()
    {
        var localTimeZone = TimeZoneInfo.Local;
        CalendarComposeTimeZoneOption localOption = null;

        foreach (var timeZone in TimeZoneInfo.GetSystemTimeZones())
        {
            var option = new CalendarComposeTimeZoneOption(timeZone);
            TimeZoneOptions.Add(option);

            if (timeZone.Id == localTimeZone.Id)
                localOption = option;
        }

        if (localOption == null)
        {
            localOption = new CalendarComposeTimeZoneOption(localTimeZone);
            TimeZoneOptions.Insert(0, localOption);
        }

        SelectedTimeZoneOption = localOption;
    }

    private TimeZoneInfo GetSelectedTimeZone() => SelectedTimeZoneOption?.TimeZone ?? TimeZoneInfo.Local;

    private CultureInfo GetCulture() => CurrentSettings?.CultureInfo ?? CultureInfo.CurrentCulture;

    private void RefreshRepeatOptionTexts()
    {
        var culture = GetCulture();

        foreach (var option in RepeatOptions)
        {
            option.DisplayText = option.Kind switch
            {
                CalendarComposeRepeatKind.None => Translator.CalendarEventCompose_RepeatNone,
                CalendarComposeRepeatKind.Daily => Translator.CalendarEventCompose_RepeatDaily,
                CalendarComposeRepeatKind.Weekdays => Translator.CalendarEventCompose_RepeatWeekdays,
                CalendarComposeRepeatKind.Weekly => string.Format(culture, Translator.CalendarEventCompose_RepeatWeekly, culture.DateTimeFormat.GetDayName(StartDate.DayOfWeek)),
                CalendarComposeRepeatKind.Monthly => string.Format(culture, Translator.CalendarEventCompose_RepeatMonthly, StartDate.Day),
                CalendarComposeRepeatKind.Yearly => string.Format(culture, Translator.CalendarEventCompose_RepeatYearly, StartDate.ToString(culture.DateTimeFormat.MonthDayPattern, culture)),
                _ => Translator.CalendarEventCompose_RepeatCustom
            };
        }
    }

    private CalendarComposeRepeatOption GetRepeatOption(CalendarComposeRepeatKind kind)
        => RepeatOptions.First(option => option.Kind == kind);

    private CalendarComposeFrequencyOption GetFrequencyOption(CalendarItemRecurrenceFrequency frequency)
        => RecurrenceFrequencyOptions.FirstOrDefault(option => option.Frequency == frequency) ?? RecurrenceFrequencyOptions.First();

    private void SetCustomRecurrence(CalendarItemRecurrenceFrequency frequency, int interval, IReadOnlyCollection<DayOfWeek> days)
    {
        var wasApplying = _isApplyingRecurrence;
        _isApplyingRecurrence = true;

        try
        {
            SelectedRecurrenceInterval = Math.Clamp(interval, 1, 99);
            SelectedRecurrenceFrequencyOption = GetFrequencyOption(frequency);

            foreach (var weekday in WeekdayOptions)
                weekday.IsSelected = days.Contains(weekday.DayOfWeek);
        }
        finally
        {
            _isApplyingRecurrence = wasApplying;
        }
    }

    private CalendarComposeWeekdayOption CreateWeekdayOption(DayOfWeek dayOfWeek, CultureInfo culture)
    {
        // RRULE day codes are the first two letters of the English day name.
        var ruleValue = dayOfWeek.ToString()[..2].ToUpperInvariant();
        var option = new CalendarComposeWeekdayOption(dayOfWeek, ruleValue, culture.DateTimeFormat.GetShortestDayName(dayOfWeek));
        option.PropertyChanged += WeekdayOptionPropertyChanged;
        return option;
    }

    private void WeekdayOptionPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarComposeWeekdayOption.IsSelected))
        {
            UpdateRecurrenceSummary();
        }
    }

    private void SelectSingleWeekday(DayOfWeek dayOfWeek)
    {
        foreach (var option in WeekdayOptions)
        {
            option.IsSelected = option.DayOfWeek == dayOfWeek;
        }
    }

    private void ShowValidationMessage(string message)
    {
        _dialogService.InfoBarMessage(
            Translator.CalendarEventCompose_ValidationTitle,
            message,
            InfoBarMessageType.Warning);
    }

    private void AttachmentsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentCount));
    }

    private void AttendeesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAttendees));
        OnPropertyChanged(nameof(AttendeeCount));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    private bool TryAddAttachment(string fileName, string filePath, string fileExtension, long size)
    {
        if (!CanAddAttachments ||
            string.IsNullOrWhiteSpace(filePath) ||
            Attachments.Any(existing => existing.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var attachment = new CalendarComposeAttachmentViewModel(fileName, filePath, fileExtension, size);
        Attachments.Add(attachment);
        BeginAttachmentInspection(attachment);
        return true;
    }

    private void BeginAttachmentInspection(CalendarComposeAttachmentViewModel attachment)
    {
        if (_attachmentFileService == null)
            return;

        var inspection = _attachmentFileService.InspectAsync(attachment.CreateFileSource()).AsTask();
        attachment.BeginInspection(inspection);
        _ = ApplyAttachmentInspectionAsync(attachment, inspection);
    }

    private async Task ApplyAttachmentInspectionAsync(
        CalendarComposeAttachmentViewModel attachment,
        Task<ContentTypeDetectionResult> inspection)
    {
        try
        {
            var detection = await inspection.ConfigureAwait(false);
            await ExecuteUIThread(() => attachment.ContentTypeDetection = detection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

}

public enum CalendarComposeRepeatKind
{
    None,
    Daily,
    Weekdays,
    Weekly,
    Monthly,
    Yearly,
    Custom
}

public partial class CalendarComposeRepeatOption : ObservableObject
{
    public CalendarComposeRepeatKind Kind { get; }

    [ObservableProperty]
    public partial string DisplayText { get; set; } = string.Empty;

    public CalendarComposeRepeatOption(CalendarComposeRepeatKind kind)
    {
        Kind = kind;
    }
}

public sealed class CalendarComposeTimeZoneOption
{
    public TimeZoneInfo TimeZone { get; }
    public string Id => TimeZone.Id;
    public string DisplayText => TimeZone.DisplayName;

    public CalendarComposeTimeZoneOption(TimeZoneInfo timeZone)
    {
        TimeZone = timeZone;
    }
}

public partial class CalendarComposeFrequencyOption : ObservableObject
{
    public CalendarItemRecurrenceFrequency Frequency { get; }
    public string DisplayText { get; }

    public CalendarComposeFrequencyOption(CalendarItemRecurrenceFrequency frequency, string displayText)
    {
        Frequency = frequency;
        DisplayText = displayText;
    }

    public string PluralLabel(int interval)
    {
        if (interval == 1)
            return DisplayText;

        return Frequency switch
        {
            CalendarItemRecurrenceFrequency.Daily => Translator.CalendarEventCompose_FrequencyDayPlural,
            CalendarItemRecurrenceFrequency.Weekly => Translator.CalendarEventCompose_FrequencyWeekPlural,
            CalendarItemRecurrenceFrequency.Monthly => Translator.CalendarEventCompose_FrequencyMonthPlural,
            CalendarItemRecurrenceFrequency.Yearly => Translator.CalendarEventCompose_FrequencyYearPlural,
            _ => DisplayText
        };
    }
}

public partial class CalendarComposeWeekdayOption : ObservableObject
{
    public DayOfWeek DayOfWeek { get; }
    public string RuleValue { get; }
    public string Label { get; }
    public string FullDayName => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(DayOfWeek);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public CalendarComposeWeekdayOption(DayOfWeek dayOfWeek, string ruleValue, string label)
    {
        DayOfWeek = dayOfWeek;
        RuleValue = ruleValue;
        Label = label;
    }
}
