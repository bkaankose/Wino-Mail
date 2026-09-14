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
using Wino.Core.ViewModels.Data;

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

        foreach (var option in CalendarReminderOptionFactory.GetReminderOptions(CalendarService))
        {
            ReminderOptions.Add(option);
        }
    }

    protected int GetSelectedReminderIndex()
        => CalendarReminderOptionFactory.GetSelectedReminderIndex(CalendarService, PreferencesService.DefaultReminderDurationInSeconds);

    protected void SaveReminderIndex(int selectedDefaultReminderIndex)
        => PreferencesService.DefaultReminderDurationInSeconds =
            CalendarReminderOptionFactory.GetReminderDurationInSeconds(CalendarService, selectedDefaultReminderIndex);

    protected void LoadSnoozeOptions()
    {
        SnoozeOptions.Clear();

        foreach (var option in CalendarReminderOptionFactory.GetSnoozeOptions())
        {
            SnoozeOptions.Add(option);
        }
    }

    protected int GetSelectedSnoozeIndex()
        => CalendarReminderOptionFactory.GetSelectedSnoozeIndex(PreferencesService.DefaultSnoozeDurationInMinutes);

    protected void SaveSnoozeIndex(int selectedDefaultSnoozeIndex)
    {
        if (CalendarReminderOptionFactory.GetSnoozeMinutes(selectedDefaultSnoozeIndex) is { } minutes)
        {
            PreferencesService.DefaultSnoozeDurationInMinutes = minutes;
        }
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
