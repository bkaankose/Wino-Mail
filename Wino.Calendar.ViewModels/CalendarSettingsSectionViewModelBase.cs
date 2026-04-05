using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;
using Wino.Core.ViewModels;

namespace Wino.Calendar.ViewModels;

public abstract class CalendarSettingsSectionViewModelBase : CalendarBaseViewModel
{
    protected CalendarSettingsSectionViewModelBase(
        IPreferencesService preferencesService,
        ICalendarService calendarService,
        IAccountService accountService)
    {
        PreferencesService = preferencesService;
        CalendarService = calendarService;
        AccountService = accountService;

        var languageCode = WinoTranslationDictionary.GetLanguageFileNameRelativePath(preferencesService.CurrentLanguage);
        CalendarCulture = new CultureInfo(languageCode);

        for (var index = 0; index < 7; index++)
        {
            DayNames.Add(CalendarCulture.DateTimeFormat.DayNames[index]);
        }
    }

    protected IPreferencesService PreferencesService { get; }
    protected ICalendarService CalendarService { get; }
    protected IAccountService AccountService { get; }
    protected CultureInfo CalendarCulture { get; }
    protected bool IsLoaded { get; set; }

    public ObservableCollection<string> DayNames { get; } = [];
    public ObservableCollection<string> ReminderOptions { get; } = [];
    public ObservableCollection<string> SnoozeOptions { get; } = [];
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

    protected void LoadReminderOptions()
    {
        ReminderOptions.Clear();

        var predefinedMinutes = CalendarService.GetPredefinedReminderMinutes();
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
    }

    protected int GetSelectedReminderIndex()
    {
        if (PreferencesService.DefaultReminderDurationInSeconds == 0)
            return 0;

        var minutes = (int)(PreferencesService.DefaultReminderDurationInSeconds / 60);
        var predefinedMinutes = CalendarService.GetPredefinedReminderMinutes();
        var index = Array.IndexOf(predefinedMinutes, minutes);
        return index >= 0 ? index + 1 : 0;
    }

    protected void SaveReminderIndex(int selectedDefaultReminderIndex)
    {
        if (selectedDefaultReminderIndex == 0)
        {
            PreferencesService.DefaultReminderDurationInSeconds = 0;
            return;
        }

        var predefinedMinutes = CalendarService.GetPredefinedReminderMinutes();
        var minutes = predefinedMinutes[selectedDefaultReminderIndex - 1];
        PreferencesService.DefaultReminderDurationInSeconds = minutes * 60;
    }

    protected void LoadSnoozeOptions()
    {
        SnoozeOptions.Clear();

        foreach (var snoozeMinutes in CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes())
        {
            SnoozeOptions.Add(string.Format(Translator.CalendarReminder_SnoozeMinutesOption, snoozeMinutes));
        }
    }

    protected int GetSelectedSnoozeIndex()
    {
        var supportedSnoozeMinutes = CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes().ToArray();
        var selectedIndex = Array.IndexOf(supportedSnoozeMinutes, PreferencesService.DefaultSnoozeDurationInMinutes);
        return selectedIndex >= 0 ? selectedIndex : 0;
    }

    protected void SaveSnoozeIndex(int selectedDefaultSnoozeIndex)
    {
        var supportedSnoozeMinutes = CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes();
        if (supportedSnoozeMinutes.Count == 0)
            return;

        var selectedIndex = Math.Clamp(selectedDefaultSnoozeIndex, 0, supportedSnoozeMinutes.Count - 1);
        PreferencesService.DefaultSnoozeDurationInMinutes = supportedSnoozeMinutes[selectedIndex];
    }

    protected void LoadNewEventBehaviorOptions()
    {
        NewEventBehaviorOptions.Clear();
        NewEventBehaviorOptions.Add(new CalendarNewEventBehaviorOption(NewEventButtonBehavior.AskEachTime, Translator.CalendarSettings_NewEventBehavior_AskEachTime));
        NewEventBehaviorOptions.Add(new CalendarNewEventBehaviorOption(NewEventButtonBehavior.AlwaysUseSpecificCalendar, Translator.CalendarSettings_NewEventBehavior_AlwaysUseSpecificCalendar));
    }

    protected CalendarNewEventBehaviorOption GetSelectedNewEventBehaviorOption()
        => NewEventBehaviorOptions.FirstOrDefault(option => option.Behavior == PreferencesService.NewEventButtonBehavior)
           ?? NewEventBehaviorOptions.First();

    protected async void LoadCalendarsAsync(Action applySelection)
    {
        var accounts = await AccountService.GetAccountsAsync().ConfigureAwait(false);
        var calendarsByAccount = new List<AccountCalendarViewModel>();

        foreach (var account in accounts)
        {
            var calendars = await CalendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
            calendarsByAccount.AddRange(calendars.Select(calendar => new AccountCalendarViewModel(account, calendar)));
        }

        await ExecuteUIThread(() =>
        {
            AvailableNewEventCalendars.Clear();

            foreach (var calendar in calendarsByAccount)
            {
                AvailableNewEventCalendars.Add(calendar);
            }

            applySelection();
        });
    }

    protected AccountCalendarViewModel ResolveSelectedNewEventCalendar()
    {
        var configuredCalendarId = PreferencesService.DefaultNewEventCalendarId;
        return configuredCalendarId.HasValue
            ? AvailableNewEventCalendars.FirstOrDefault(calendar => calendar.Id == configuredCalendarId.Value)
            : null;
    }

    protected AccountCalendarViewModel ResolveFallbackNewEventCalendar()
        => AvailableNewEventCalendars.FirstOrDefault(calendar => calendar.IsPrimary)
           ?? AvailableNewEventCalendars.FirstOrDefault();

    protected void SaveNewEventBehavior(CalendarNewEventBehaviorOption selectedBehaviorOption, AccountCalendarViewModel selectedCalendar)
    {
        var newEventBehavior = selectedBehaviorOption?.Behavior ?? NewEventButtonBehavior.AskEachTime;
        if (newEventBehavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar && selectedCalendar != null)
        {
            PreferencesService.NewEventButtonBehavior = NewEventButtonBehavior.AlwaysUseSpecificCalendar;
            PreferencesService.DefaultNewEventCalendarId = selectedCalendar.Id;
            return;
        }

        PreferencesService.NewEventButtonBehavior = NewEventButtonBehavior.AskEachTime;
        PreferencesService.DefaultNewEventCalendarId = null;
    }
}

public sealed class CalendarNewEventBehaviorOption
{
    public CalendarNewEventBehaviorOption(NewEventButtonBehavior behavior, string displayText)
    {
        Behavior = behavior;
        DisplayText = displayText;
    }

    public NewEventButtonBehavior Behavior { get; }
    public string DisplayText { get; }
}
