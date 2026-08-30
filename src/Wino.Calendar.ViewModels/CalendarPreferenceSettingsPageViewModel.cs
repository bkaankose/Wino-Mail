using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels;

public partial class CalendarPreferenceSettingsPageViewModel : CalendarSettingsSectionViewModelBase
{
    [ObservableProperty]
    public partial CalendarNewEventBehaviorOption SelectedNewEventBehaviorOption { get; set; }

    [ObservableProperty]
    public partial AccountCalendarViewModel SelectedNewEventCalendar { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowCalendarStartupAccount))]
    public partial bool IsCalendarAccountsGrouped { get; set; }

    [ObservableProperty]
    public partial MailAccount SelectedCalendarStartupAccount { get; set; }

    public ObservableCollection<MailAccount> CalendarStartupAccounts { get; } = [];

    public bool ShouldShowSpecificNewEventCalendar
        => SelectedNewEventBehaviorOption?.Behavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar;

    public bool ShouldShowCalendarStartupAccount => !IsCalendarAccountsGrouped;

    public Task InitializationTask { get; }

    public CalendarPreferenceSettingsPageViewModel(
        IPreferencesService preferencesService,
        ICalendarService calendarService,
        IAccountService accountService)
        : base(preferencesService, calendarService, accountService)
    {
        LoadNewEventBehaviorOptions();
        SelectedNewEventBehaviorOption = GetSelectedNewEventBehaviorOption();
        IsCalendarAccountsGrouped = preferencesService.IsCalendarAccountsGrouped;

        IsLoaded = true;
        InitializationTask = LoadCalendarOptionsAsync();
    }

    partial void OnSelectedNewEventBehaviorOptionChanged(CalendarNewEventBehaviorOption value)
    {
        if (!IsLoaded)
            return;

        OnPropertyChanged(nameof(ShouldShowSpecificNewEventCalendar));
        SaveNewEventBehavior(SelectedNewEventBehaviorOption, SelectedNewEventCalendar);
    }

    partial void OnSelectedNewEventCalendarChanged(AccountCalendarViewModel value)
    {
        if (!IsLoaded)
            return;

        SaveNewEventBehavior(SelectedNewEventBehaviorOption, value);
    }

    partial void OnIsCalendarAccountsGroupedChanged(bool value)
    {
        if (!IsLoaded)
            return;

        PreferencesService.IsCalendarAccountsGrouped = value;
    }

    partial void OnSelectedCalendarStartupAccountChanged(MailAccount value)
    {
        if (!IsLoaded)
            return;

        PreferencesService.CalendarStartupAccountId = value?.Id;
    }

    private async Task LoadCalendarOptionsAsync()
    {
        var accounts = await AccountService.GetAccountsAsync().ConfigureAwait(false);
        var orderedAccounts = accounts
            .OrderBy(account => account.Order)
            .ThenBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(account => account.Id)
            .ToList();
        var calendarOptions = new List<AccountCalendarViewModel>();
        var startupAccounts = new List<MailAccount>();

        foreach (var account in orderedAccounts)
        {
            var calendars = await CalendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false);
            calendarOptions.AddRange(calendars.Select(calendar => new AccountCalendarViewModel(account, calendar)));

            if (GroupedAccountCalendarViewModel.SupportsCalendar(account) && calendars.Count > 0)
                startupAccounts.Add(account);
        }

        await ExecuteUIThread(() =>
        {
            AvailableNewEventCalendars.Clear();
            foreach (var calendar in calendarOptions)
            {
                AvailableNewEventCalendars.Add(calendar);
            }

            CalendarStartupAccounts.Clear();
            foreach (var account in startupAccounts)
            {
                CalendarStartupAccounts.Add(account);
            }

            ApplyStoredNewEventCalendarPreference();
            ApplyStoredCalendarStartupAccountPreference();
        });
    }

    private void ApplyStoredNewEventCalendarPreference()
    {
        var configuredCalendar = ResolveSelectedNewEventCalendar();
        if (PreferencesService.NewEventButtonBehavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar && configuredCalendar == null)
        {
            SelectedNewEventBehaviorOption = NewEventBehaviorOptions.First(option => option.Behavior == NewEventButtonBehavior.AskEachTime);
            SelectedNewEventCalendar = null;
            return;
        }

        SelectedNewEventCalendar = configuredCalendar ?? ResolveFallbackNewEventCalendar();
    }

    private void ApplyStoredCalendarStartupAccountPreference()
    {
        var configuredAccountId = PreferencesService.CalendarStartupAccountId;
        var configuredAccount = configuredAccountId.HasValue
            ? CalendarStartupAccounts.FirstOrDefault(account => account.Id == configuredAccountId.Value)
            : null;
        SelectedCalendarStartupAccount = configuredAccount ?? CalendarStartupAccounts.FirstOrDefault();

        var repairedAccountId = SelectedCalendarStartupAccount?.Id;
        if (PreferencesService.CalendarStartupAccountId != repairedAccountId)
            PreferencesService.CalendarStartupAccountId = repairedAccountId;
    }
}
