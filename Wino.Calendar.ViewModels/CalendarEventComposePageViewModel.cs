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
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels;

public partial class CalendarEventComposePageViewModel : CalendarBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly INavigationService _navigationService;
    private readonly IMailDialogService _dialogService;
    private readonly IContactService _contactService;
    private readonly IPreferencesService _preferencesService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly CalendarEventComposeResultValidator _composeResultValidator = new();

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

    [ObservableProperty]
    public partial TimeSpan EndTime { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset AllDayEndDate { get; set; }

    [ObservableProperty]
    public partial bool IsRecurring { get; set; }

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
    public partial bool IsDarkWebviewRenderer { get; set; }

    [ObservableProperty]
    public partial CalendarEventComposeResult LastCreatedResult { get; set; }

    public CalendarSettings CurrentSettings { get; }
    public string TimePickerClockIdentifier => CurrentSettings.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour ? "24HourClock" : "12HourClock";
    public bool HasAttachments => Attachments.Count > 0;
    public bool IsSelectedCalendarCalDav => SelectedCalendar?.Account?.ProviderType == MailProviderType.IMAP4 &&
                                            SelectedCalendar.Account.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav;
    public bool CanAddAttachments => !IsSelectedCalendarCalDav;
    public string AttachmentsDisabledTooltipText => IsSelectedCalendarCalDav
        ? Translator.CalendarEventCompose_AttachmentsNotSupportedForCalDav
        : string.Empty;
    public string SelectedCalendarDisplayText => SelectedCalendar?.Name ?? Translator.CalendarEventCompose_SelectCalendar;
    public string SelectedCalendarAccountText => SelectedCalendar?.Account?.Address ?? string.Empty;
    public bool IsDailyRecurrenceSelected => SelectedRecurrenceFrequencyOption?.Frequency == CalendarItemRecurrenceFrequency.Daily;

    public CalendarEventComposePageViewModel(IAccountService accountService,
                                             ICalendarService calendarService,
                                             INavigationService navigationService,
                                             IMailDialogService dialogService,
                                             IContactService contactService,
                                             IPreferencesService preferencesService,
                                             IUnderlyingThemeService underlyingThemeService,
                                             IWinoRequestDelegator winoRequestDelegator)
    {
        _accountService = accountService;
        _calendarService = calendarService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _contactService = contactService;
        _preferencesService = preferencesService;
        _underlyingThemeService = underlyingThemeService;
        _winoRequestDelegator = winoRequestDelegator;

        CurrentSettings = _preferencesService.GetCurrentCalendarSettings();
        IsDarkWebviewRenderer = _underlyingThemeService.IsUnderlyingThemeDark();

        Attachments.CollectionChanged += AttachmentsCollectionChanged;

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
        SelectedRecurrenceFrequencyOption = RecurrenceFrequencyOptions.FirstOrDefault();

        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Monday, "MO", Translator.CalendarEventCompose_Weekday_Monday));
        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Tuesday, "TU", Translator.CalendarEventCompose_Weekday_Tuesday));
        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Wednesday, "WE", Translator.CalendarEventCompose_Weekday_Wednesday));
        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Thursday, "TH", Translator.CalendarEventCompose_Weekday_Thursday));
        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Friday, "FR", Translator.CalendarEventCompose_Weekday_Friday));
        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Saturday, "SA", Translator.CalendarEventCompose_Weekday_Saturday));
        WeekdayOptions.Add(CreateWeekdayOption(DayOfWeek.Sunday, "SU", Translator.CalendarEventCompose_Weekday_Sunday));

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

        OnPropertyChanged(nameof(IsSelectedCalendarCalDav));
        OnPropertyChanged(nameof(CanAddAttachments));
        OnPropertyChanged(nameof(AttachmentsDisabledTooltipText));
        OnPropertyChanged(nameof(SelectedCalendarDisplayText));
        OnPropertyChanged(nameof(SelectedCalendarAccountText));
    }

    partial void OnIsAllDayChanged(bool value)
    {
        if (value)
        {
            if (AllDayEndDate.Date <= StartDate.Date)
            {
                AllDayEndDate = StartDate.AddDays(1);
            }
        }

        UpdateRecurrenceSummary();
    }

    partial void OnStartDateChanged(DateTimeOffset value)
    {
        if (IsAllDay && AllDayEndDate.Date <= value.Date)
        {
            AllDayEndDate = value.AddDays(1);
        }

        if (IsRecurring && WeekdayOptions.All(option => !option.IsSelected))
        {
            SelectSingleWeekday(value.DayOfWeek);
        }

        UpdateRecurrenceSummary();
    }

    partial void OnStartTimeChanged(TimeSpan value) => UpdateRecurrenceSummary();
    partial void OnEndTimeChanged(TimeSpan value) => UpdateRecurrenceSummary();
    partial void OnAllDayEndDateChanged(DateTimeOffset value) => UpdateRecurrenceSummary();
    partial void OnIsRecurringChanged(bool value)
    {
        if (value && WeekdayOptions.All(option => !option.IsSelected))
        {
            SelectSingleWeekday(StartDate.DayOfWeek);
        }

        UpdateRecurrenceSummary();
    }
    partial void OnSelectedRecurrenceIntervalChanged(int value) => UpdateRecurrenceSummary();
    partial void OnSelectedRecurrenceFrequencyOptionChanged(CalendarComposeFrequencyOption value)
    {
        OnPropertyChanged(nameof(IsDailyRecurrenceSelected));
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
    private void Cancel()
    {
        _navigationService.GoBack();
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var uniqueAttendees = Attendees
            .GroupBy(attendee => attendee.Email, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var createdResult = await BuildResultAsync(uniqueAttendees);

        try
        {
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

        return await _contactService.GetAddressInformationAsync(queryText).ConfigureAwait(false);
    }

    public async Task<CalendarComposeAttendeeViewModel> GetAttendeeAsync(string tokenText)
    {
        if (!EmailValidator.Validate(tokenText))
            return null;

        var existing = Attendees.Any(attendee => attendee.Email.Equals(tokenText, StringComparison.OrdinalIgnoreCase));
        if (existing)
            return null;

        var info = await _contactService.GetAddressInformationByAddressAsync(tokenText).ConfigureAwait(false);
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

    private void ApplyDateRange(DateTime startDate, DateTime endDate, bool isAllDay)
    {
        IsAllDay = isAllDay;
        StartDate = new DateTimeOffset(startDate.Date);
        StartTime = startDate.TimeOfDay;
        EndTime = endDate.TimeOfDay;
        AllDayEndDate = new DateTimeOffset((isAllDay ? endDate.Date : startDate.Date.AddDays(1)));
    }

    private async Task<CalendarEventComposeResult> BuildResultAsync(List<CalendarComposeAttendeeViewModel> uniqueAttendees)
    {
        if (RecurrenceEndDate.HasValue && RecurrenceEndDate.Value.Date < StartDate.Date)
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
            TimeZoneId = TimeZoneInfo.Local.Id,
            ShowAs = SelectedShowAsOption?.ShowAs ?? SelectedCalendar?.DefaultShowAs ?? CalendarItemShowAs.Busy,
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
        if (!HasInitializedComposeDateRange())
        {
            RecurrenceSummary = string.Empty;
            return;
        }

        var effectiveStart = GetEffectiveStartDateTime();
        var effectiveEnd = GetEffectiveEndDateTime();
        var selectedDays = IsDailyRecurrenceSelected
            ? WeekdayOptions
                .Where(option => option.IsSelected)
                .Select(option => option.DayOfWeek)
                .ToList()
            : [];

        RecurrenceSummary = CalendarRecurrenceSummaryFormatter.BuildSummary(
            IsRecurring,
            effectiveStart,
            effectiveEnd,
            IsAllDay,
            CurrentSettings,
            SelectedRecurrenceInterval,
            SelectedRecurrenceFrequencyOption?.Frequency ?? CalendarItemRecurrenceFrequency.Weekly,
            selectedDays,
            RecurrenceEndDate);
    }

    private bool HasInitializedComposeDateRange()
    {
        if (StartDate == default)
        {
            return false;
        }

        return !IsAllDay || AllDayEndDate != default;
    }

    private string BuildRecurrenceRule()
    {
        if (!IsRecurring || SelectedRecurrenceFrequencyOption == null)
            return string.Empty;

        var parts = new List<string>
        {
            $"FREQ={SelectedRecurrenceFrequencyOption.Frequency.ToString().ToUpperInvariant()}",
            $"INTERVAL={SelectedRecurrenceInterval}"
        };

        var selectedDays = IsDailyRecurrenceSelected
            ? WeekdayOptions
                .Where(option => option.IsSelected)
                .Select(option => option.RuleValue)
                .ToList()
            : [];

        if (selectedDays.Count > 0)
        {
            parts.Add($"BYDAY={string.Join(",", selectedDays)}");
        }

        if (RecurrenceEndDate.HasValue)
        {
            var untilValue = IsAllDay
                ? RecurrenceEndDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : RecurrenceEndDate.Value.Date.AddDays(1).AddSeconds(-1).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

            parts.Add($"UNTIL={untilValue}");
        }

        return $"RRULE:{string.Join(";", parts)}";
    }

    private DateTime GetEffectiveStartDateTime()
        => StartDate.Date.Add(IsAllDay ? TimeSpan.Zero : StartTime);

    private DateTime GetEffectiveEndDateTime()
        => IsAllDay
            ? AllDayEndDate.Date
            : StartDate.Date.Add(EndTime);

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

        return (startDate, startDate.AddMinutes(30));
    }

    private CalendarComposeWeekdayOption CreateWeekdayOption(DayOfWeek dayOfWeek, string ruleValue, string label)
    {
        var option = new CalendarComposeWeekdayOption(dayOfWeek, ruleValue, label);
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
    }

    private bool TryAddAttachment(string fileName, string filePath, string fileExtension, long size)
    {
        if (!CanAddAttachments ||
            string.IsNullOrWhiteSpace(filePath) ||
            Attachments.Any(existing => existing.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Attachments.Add(new CalendarComposeAttachmentViewModel(fileName, filePath, fileExtension, size));
        return true;
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
    public string FullDayName => DayOfWeek switch
    {
        DayOfWeek.Monday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[1],
        DayOfWeek.Tuesday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[2],
        DayOfWeek.Wednesday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[3],
        DayOfWeek.Thursday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[4],
        DayOfWeek.Friday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[5],
        DayOfWeek.Saturday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[6],
        DayOfWeek.Sunday => CultureInfo.CurrentCulture.DateTimeFormat.DayNames[0],
        _ => string.Empty
    };

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public CalendarComposeWeekdayOption(DayOfWeek dayOfWeek, string ruleValue, string label)
    {
        DayOfWeek = dayOfWeek;
        RuleValue = ruleValue;
        Label = label;
    }
}


