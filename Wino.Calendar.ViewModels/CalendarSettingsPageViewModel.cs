using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels;

public partial class CalendarSettingsPageViewModel : CalendarBaseViewModel
{
    [ObservableProperty]
    public partial double CellHourHeight { get; set; }

    [ObservableProperty]
    public partial int SelectedFirstDayOfWeekIndex { get; set; }

    [ObservableProperty]
    public partial bool Is24HourHeaders { get; set; }

    [ObservableProperty]
    public partial bool IsWorkingHoursEnabled { get; set; }

    [ObservableProperty]
    public partial TimeSpan WorkingHourStart { get; set; }

    [ObservableProperty]
    public partial TimeSpan WorkingHourEnd { get; set; }

    [ObservableProperty]
    public partial List<string> DayNames { get; set; } = [];

    [ObservableProperty]
    public partial int WorkingDayStartIndex { get; set; }

    [ObservableProperty]
    public partial int WorkingDayEndIndex { get; set; }

    [ObservableProperty]
    public partial string TimedDayHeaderDateFormat { get; set; } = "ddd dd";

    [ObservableProperty]
    public partial int SelectedTimedDayHeaderFormatPresetIndex { get; set; } = -1;

    [ObservableProperty]
    public partial List<string> ReminderOptions { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedDefaultReminderIndex { get; set; }

    [ObservableProperty]
    public partial List<string> SnoozeOptions { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedDefaultSnoozeIndex { get; set; }

    public ObservableCollection<MailAccount> Accounts { get; } = [];
    public ObservableCollection<CalendarNewEventBehaviorOption> NewEventBehaviorOptions { get; } = [];
    public ObservableCollection<AccountCalendarViewModel> AvailableNewEventCalendars { get; } = [];
    public ObservableCollection<string> TimedDayHeaderFormatPresets { get; } =
    [
        "ddd dd",
        "dddd dd",
        "ddd d MMM",
        "dd MMM ddd",
        "M/d ddd"
    ];

    [ObservableProperty]
    public partial CalendarNewEventBehaviorOption SelectedNewEventBehaviorOption { get; set; }

    [ObservableProperty]
    public partial AccountCalendarViewModel SelectedNewEventCalendar { get; set; }

    public bool ShouldShowSpecificNewEventCalendar => SelectedNewEventBehaviorOption?.Behavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar;

    public IPreferencesService PreferencesService { get; }
    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;
    private readonly CultureInfo _calendarCulture;
    private readonly bool _isLoaded = false;

    public CalendarSettingsPageViewModel(IPreferencesService preferencesService, ICalendarService calendarService, IAccountService accountService)
    {
        PreferencesService = preferencesService;
        _calendarService = calendarService;
        _accountService = accountService;

        var currentLanguageLanguageCode = WinoTranslationDictionary.GetLanguageFileNameRelativePath(preferencesService.CurrentLanguage);
        var cultureInfo = new CultureInfo(currentLanguageLanguageCode);
        _calendarCulture = cultureInfo;

        for (var i = 0; i < 7; i++)
        {
            DayNames.Add(cultureInfo.DateTimeFormat.DayNames[i]);
        }

        var cultureFirstDayName = cultureInfo.DateTimeFormat.GetDayName(preferencesService.FirstDayOfWeek);
        SelectedFirstDayOfWeekIndex = DayNames.IndexOf(cultureFirstDayName);
        Is24HourHeaders = preferencesService.Prefer24HourTimeFormat;
        IsWorkingHoursEnabled = preferencesService.IsWorkingHoursEnabled;
        WorkingHourStart = preferencesService.WorkingHourStart;
        WorkingHourEnd = preferencesService.WorkingHourEnd;
        CellHourHeight = preferencesService.HourHeight;
        WorkingDayStartIndex = DayNames.IndexOf(cultureInfo.DateTimeFormat.GetDayName(preferencesService.WorkingDayStart));
        WorkingDayEndIndex = DayNames.IndexOf(cultureInfo.DateTimeFormat.GetDayName(preferencesService.WorkingDayEnd));
        TimedDayHeaderDateFormat = preferencesService.CalendarTimedDayHeaderDateFormat;
        SelectedTimedDayHeaderFormatPresetIndex = TimedDayHeaderFormatPresets.IndexOf(TimedDayHeaderDateFormat);

        var predefinedMinutes = _calendarService.GetPredefinedReminderMinutes();
        ReminderOptions.Add("None");
        foreach (var minutes in predefinedMinutes)
        {
            var displayText = minutes switch
            {
                >= 60 => $"{minutes / 60} Hour{(minutes / 60 > 1 ? "s" : "")}",
                _ => $"{minutes} Minute{(minutes > 1 ? "s" : "")}"
            };
            ReminderOptions.Add(displayText);
        }

        if (preferencesService.DefaultReminderDurationInSeconds == 0)
        {
            SelectedDefaultReminderIndex = 0;
        }
        else
        {
            var minutes = (int)(preferencesService.DefaultReminderDurationInSeconds / 60);
            var index = Array.IndexOf(predefinedMinutes, minutes);
            SelectedDefaultReminderIndex = index >= 0 ? index + 1 : 0;
        }

        var supportedSnoozeMinutes = CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes().ToArray();
        foreach (var snoozeMinutes in supportedSnoozeMinutes)
        {
            SnoozeOptions.Add(string.Format(Translator.CalendarReminder_SnoozeMinutesOption, snoozeMinutes));
        }

        var selectedSnoozeIndex = Array.IndexOf(supportedSnoozeMinutes, preferencesService.DefaultSnoozeDurationInMinutes);
        SelectedDefaultSnoozeIndex = selectedSnoozeIndex >= 0 ? selectedSnoozeIndex : 0;

        NewEventBehaviorOptions.Add(new CalendarNewEventBehaviorOption(NewEventButtonBehavior.AskEachTime, Translator.CalendarSettings_NewEventBehavior_AskEachTime));
        NewEventBehaviorOptions.Add(new CalendarNewEventBehaviorOption(NewEventButtonBehavior.AlwaysUseSpecificCalendar, Translator.CalendarSettings_NewEventBehavior_AlwaysUseSpecificCalendar));
        SelectedNewEventBehaviorOption = NewEventBehaviorOptions.FirstOrDefault(option => option.Behavior == preferencesService.NewEventButtonBehavior)
                                         ?? NewEventBehaviorOptions.First();

        _isLoaded = true;

        LoadAccountsAsync();
    }

    private async void LoadAccountsAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var calendarsByAccount = new List<(MailAccount Account, List<AccountCalendarViewModel> Calendars)>();

        foreach (var account in accounts)
        {
            var calendars = await _calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
            calendarsByAccount.Add((account, calendars.Select(calendar => new AccountCalendarViewModel(account, calendar)).ToList()));
        }

        await Dispatcher.ExecuteOnUIThread(() =>
        {
            Accounts.Clear();
            AvailableNewEventCalendars.Clear();

            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }

            foreach (var accountCalendars in calendarsByAccount)
            {
                foreach (var calendar in accountCalendars.Calendars)
                {
                    AvailableNewEventCalendars.Add(calendar);
                }
            }

            ApplyStoredNewEventCalendarPreference();
        });
    }

    partial void OnCellHourHeightChanged(double oldValue, double newValue) => SaveSettings();
    partial void OnIs24HourHeadersChanged(bool value)
    {
        OnPropertyChanged(nameof(TimedHourLabelPreview));
        SaveSettings();
    }
    partial void OnSelectedFirstDayOfWeekIndexChanged(int value) => SaveSettings();
    partial void OnIsWorkingHoursEnabledChanged(bool value) => SaveSettings();
    partial void OnWorkingHourStartChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingHourEndChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingDayStartIndexChanged(int value) => SaveSettings();
    partial void OnWorkingDayEndIndexChanged(int value) => SaveSettings();
    partial void OnTimedDayHeaderDateFormatChanged(string value)
    {
        OnPropertyChanged(nameof(TimedDayHeaderFormatPreview));
        OnPropertyChanged(nameof(TimedHourLabelPreview));

        var normalizedFormat = string.IsNullOrWhiteSpace(value) ? "ddd dd" : value.Trim();
        var matchingPresetIndex = TimedDayHeaderFormatPresets
            .Select((format, index) => new { format, index })
            .Where(item => string.Equals(item.format, normalizedFormat, StringComparison.Ordinal))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

        if (SelectedTimedDayHeaderFormatPresetIndex != matchingPresetIndex)
        {
            SelectedTimedDayHeaderFormatPresetIndex = matchingPresetIndex;
        }

        SaveSettings();
    }
    partial void OnSelectedTimedDayHeaderFormatPresetIndexChanged(int value)
    {
        if (value < 0 || value >= TimedDayHeaderFormatPresets.Count)
        {
            return;
        }

        var selectedPreset = TimedDayHeaderFormatPresets[value];

        if (string.Equals(TimedDayHeaderDateFormat, selectedPreset, StringComparison.Ordinal))
        {
            return;
        }

        TimedDayHeaderDateFormat = selectedPreset;
    }
    partial void OnSelectedDefaultReminderIndexChanged(int value) => SaveSettings();
    partial void OnSelectedDefaultSnoozeIndexChanged(int value) => SaveSettings();
    partial void OnSelectedNewEventBehaviorOptionChanged(CalendarNewEventBehaviorOption value)
    {
        OnPropertyChanged(nameof(ShouldShowSpecificNewEventCalendar));
        SaveSettings();
    }
    partial void OnSelectedNewEventCalendarChanged(AccountCalendarViewModel value) => SaveSettings();

    public string TimedDayHeaderFormatPreview
    {
        get
        {
            var format = string.IsNullOrWhiteSpace(TimedDayHeaderDateFormat) ? "ddd dd" : TimedDayHeaderDateFormat.Trim();
            var previewDates = new[]
            {
                new DateTime(2026, 3, 23),
                new DateTime(2026, 3, 24),
                new DateTime(2026, 3, 25)
            };

            try
            {
                return string.Join(" · ", previewDates.Select(date => date.ToString(format, _calendarCulture)));
            }
            catch (FormatException)
            {
                return string.Join(" · ", previewDates.Select(date => date.ToString("ddd dd", _calendarCulture)));
            }
        }
    }

    public string TimedHourLabelPreview
    {
        get
        {
            var previewHours = new[] { 0, 9, 14, 24 };
            return string.Join("  ·  ", previewHours.Select(CurrentSettingsPreviewLabel));
        }
    }

    private string CurrentSettingsPreviewLabel(int hour)
    {
        if (Is24HourHeaders)
        {
            return hour.ToString(_calendarCulture);
        }

        var displayHour = hour % 24;
        return DateTime.Today.AddHours(displayHour).ToString("h tt", _calendarCulture);
    }

    public void SaveSettings()
    {
        if (!_isLoaded)
            return;

        PreferencesService.FirstDayOfWeek = SelectedFirstDayOfWeekIndex switch
        {
            0 => DayOfWeek.Sunday,
            1 => DayOfWeek.Monday,
            2 => DayOfWeek.Tuesday,
            3 => DayOfWeek.Wednesday,
            4 => DayOfWeek.Thursday,
            5 => DayOfWeek.Friday,
            6 => DayOfWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException()
        };

        PreferencesService.WorkingDayStart = WorkingDayStartIndex switch
        {
            0 => DayOfWeek.Sunday,
            1 => DayOfWeek.Monday,
            2 => DayOfWeek.Tuesday,
            3 => DayOfWeek.Wednesday,
            4 => DayOfWeek.Thursday,
            5 => DayOfWeek.Friday,
            6 => DayOfWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException()
        };

        PreferencesService.WorkingDayEnd = WorkingDayEndIndex switch
        {
            0 => DayOfWeek.Sunday,
            1 => DayOfWeek.Monday,
            2 => DayOfWeek.Tuesday,
            3 => DayOfWeek.Wednesday,
            4 => DayOfWeek.Thursday,
            5 => DayOfWeek.Friday,
            6 => DayOfWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException()
        };

        PreferencesService.Prefer24HourTimeFormat = Is24HourHeaders;
        PreferencesService.IsWorkingHoursEnabled = IsWorkingHoursEnabled;
        PreferencesService.WorkingHourStart = WorkingHourStart;
        PreferencesService.WorkingHourEnd = WorkingHourEnd;
        PreferencesService.HourHeight = CellHourHeight;
        PreferencesService.CalendarTimedDayHeaderDateFormat = TimedDayHeaderDateFormat;

        if (SelectedDefaultReminderIndex == 0)
        {
            PreferencesService.DefaultReminderDurationInSeconds = 0;
        }
        else
        {
            var predefinedMinutes = _calendarService.GetPredefinedReminderMinutes();
            var minutes = predefinedMinutes[SelectedDefaultReminderIndex - 1];
            PreferencesService.DefaultReminderDurationInSeconds = minutes * 60;
        }

        var supportedSnoozeMinutes = CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes();
        if (supportedSnoozeMinutes.Count > 0)
        {
            var selectedIndex = Math.Clamp(SelectedDefaultSnoozeIndex, 0, supportedSnoozeMinutes.Count - 1);
            PreferencesService.DefaultSnoozeDurationInMinutes = supportedSnoozeMinutes[selectedIndex];
        }

        var newEventBehavior = SelectedNewEventBehaviorOption?.Behavior ?? NewEventButtonBehavior.AskEachTime;
        if (newEventBehavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar && SelectedNewEventCalendar != null)
        {
            PreferencesService.NewEventButtonBehavior = NewEventButtonBehavior.AlwaysUseSpecificCalendar;
            PreferencesService.DefaultNewEventCalendarId = SelectedNewEventCalendar.Id;
        }
        else
        {
            PreferencesService.NewEventButtonBehavior = NewEventButtonBehavior.AskEachTime;
            PreferencesService.DefaultNewEventCalendarId = null;
        }
    }

    private void ApplyStoredNewEventCalendarPreference()
    {
        var configuredCalendarId = PreferencesService.DefaultNewEventCalendarId;
        var configuredCalendar = configuredCalendarId.HasValue
            ? AvailableNewEventCalendars.FirstOrDefault(calendar => calendar.Id == configuredCalendarId.Value)
            : null;

        if (PreferencesService.NewEventButtonBehavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar && configuredCalendar == null)
        {
            SelectedNewEventBehaviorOption = NewEventBehaviorOptions.First(option => option.Behavior == NewEventButtonBehavior.AskEachTime);
            SelectedNewEventCalendar = null;
            return;
        }

        SelectedNewEventCalendar = configuredCalendar
                                   ?? AvailableNewEventCalendars.FirstOrDefault(calendar => calendar.IsPrimary)
                                   ?? AvailableNewEventCalendars.FirstOrDefault();
    }
}

public sealed class CalendarNewEventBehaviorOption
{
    public NewEventButtonBehavior Behavior { get; }
    public string DisplayText { get; }

    public CalendarNewEventBehaviorOption(NewEventButtonBehavior behavior, string displayText)
    {
        Behavior = behavior;
        DisplayText = displayText;
    }
}
