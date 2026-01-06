using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Navigation;

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
    public partial List<string> ReminderOptions { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedDefaultReminderIndex { get; set; }

    public IPreferencesService PreferencesService { get; }
    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;

    public ObservableCollection<MailAccount> Accounts { get; } = new ObservableCollection<MailAccount>();

    private readonly bool _isLoaded = false;

    public CalendarSettingsPageViewModel(IPreferencesService preferencesService, ICalendarService calendarService, IAccountService accountService)
    {
        PreferencesService = preferencesService;
        _calendarService = calendarService;
        _accountService = accountService;

        var currentLanguageLanguageCode = WinoTranslationDictionary.GetLanguageFileNameRelativePath(preferencesService.CurrentLanguage);

        var cultureInfo = new CultureInfo(currentLanguageLanguageCode);

        // Populate the day names list
        for (var i = 0; i < 7; i++)
        {
            DayNames.Add(cultureInfo.DateTimeFormat.DayNames[i]);
        }

        var cultureFirstDayName = cultureInfo.DateTimeFormat.GetDayName(preferencesService.FirstDayOfWeek);
        SelectedFirstDayOfWeekIndex = DayNames.IndexOf(cultureFirstDayName);
        Is24HourHeaders = preferencesService.Prefer24HourTimeFormat;
        WorkingHourStart = preferencesService.WorkingHourStart;
        WorkingHourEnd = preferencesService.WorkingHourEnd;
        CellHourHeight = preferencesService.HourHeight;
        WorkingDayStartIndex = DayNames.IndexOf(cultureInfo.DateTimeFormat.GetDayName(preferencesService.WorkingDayStart));
        WorkingDayEndIndex = DayNames.IndexOf(cultureInfo.DateTimeFormat.GetDayName(preferencesService.WorkingDayEnd));

        // Initialize reminder options
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

        // Set selected index based on current default reminder setting
        if (preferencesService.DefaultReminderDurationInSeconds == 0)
        {
            SelectedDefaultReminderIndex = 0; // None
        }
        else
        {
            var minutes = (int)(preferencesService.DefaultReminderDurationInSeconds / 60);
            var index = Array.IndexOf(predefinedMinutes, minutes);
            SelectedDefaultReminderIndex = index >= 0 ? index + 1 : 0;
        }

        _isLoaded = true;

        // Load accounts with calendar support
        LoadAccountsAsync();
    }

    private async void LoadAccountsAsync()
    {
        var accounts = await _accountService.GetAccountsAsync();
        
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            Accounts.Clear();
            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }
        });
    }

    [RelayCommand]
    private void NavigateToAccountSettings(MailAccount account)
    {
        if (account == null) return;
        
        Messenger.Send(new BreadcrumbNavigationRequested(
            string.Format(Translator.CalendarAccountSettings_Description, account.Name),
            WinoPage.CalendarAccountSettingsPage,
            account.Id));
    }

    partial void OnCellHourHeightChanged(double oldValue, double newValue) => SaveSettings();
    partial void OnIs24HourHeadersChanged(bool value) => SaveSettings();
    partial void OnSelectedFirstDayOfWeekIndexChanged(int value) => SaveSettings();
    partial void OnWorkingHourStartChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingHourEndChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingDayStartIndexChanged(int value) => SaveSettings();
    partial void OnWorkingDayEndIndexChanged(int value) => SaveSettings();
    partial void OnSelectedDefaultReminderIndexChanged(int value) => SaveSettings();

    public void SaveSettings()
    {
        if (!_isLoaded) return;

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
        PreferencesService.WorkingHourStart = WorkingHourStart;
        PreferencesService.WorkingHourEnd = WorkingHourEnd;
        PreferencesService.HourHeight = CellHourHeight;

        // Save default reminder setting
        if (SelectedDefaultReminderIndex == 0)
        {
            PreferencesService.DefaultReminderDurationInSeconds = 0; // None
        }
        else
        {
            var predefinedMinutes = _calendarService.GetPredefinedReminderMinutes();
            var minutes = predefinedMinutes[SelectedDefaultReminderIndex - 1];
            PreferencesService.DefaultReminderDurationInSeconds = minutes * 60;
        }

        Messenger.Send(new CalendarSettingsUpdatedMessage());
    }
}
