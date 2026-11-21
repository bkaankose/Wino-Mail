#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>The account's primary calendar and, per calendar, sync, color, Show as and default reminder.</summary>
public partial class AccountCalendarSettingsPageViewModel : AccountAppPageViewModelBase
{
    private readonly ICalendarService _calendarService;
    private readonly IPreferencesService _preferencesService;
    private bool _isLoadingContent;

    public AccountCalendarSettingsPageViewModel(
        IMessenger messenger,
        IAccountService accountService,
        IMailDialogService dialogService,
        IAccountCapabilityService capabilityService,
        AccountAppChangeCoordinator appChanges,
        ICalendarService calendarService,
        IPreferencesService preferencesService,
        INewThemeService themeService)
        : base(messenger, WinoApplicationMode.Calendar, accountService, dialogService, capabilityService, appChanges)
    {
        _calendarService = calendarService;
        _preferencesService = preferencesService;
        AvailableColors = themeService.GetAvailableAccountColors().Select(hex => new AppColorViewModel(hex)).ToList();

        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.Free, Translator.CalendarShowAs_Free));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.Tentative, Translator.CalendarShowAs_Tentative));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.Busy, Translator.CalendarShowAs_Busy));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.OutOfOffice, Translator.CalendarShowAs_OutOfOffice));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.WorkingElsewhere, Translator.CalendarShowAs_WorkingElsewhere));
    }

    public List<AppColorViewModel> AvailableColors { get; }

    public ObservableCollection<AccountCalendarShowAsOption> ShowAsOptions { get; } = [];

    public ObservableCollection<AccountCalendar> AccountCalendars { get; } = [];

    public ObservableCollection<AccountCalendarSettingsItemViewModel> CalendarItems { get; } = [];

    [ObservableProperty]
    public partial AccountCalendar? SelectedPrimaryCalendar { get; set; }

    public bool HasCalendars => CalendarItems.Count > 0;

    protected override async Task LoadContentAsync(MailAccount account)
    {
        var calendars = IsCalendarOn(account)
            ? await _calendarService.GetAccountCalendarsAsync(account.Id)
            : [];
        var isSynchronizationConfigurable = account.HasRemoteCalendarSource();

        await ExecuteUIThread(() =>
        {
            _isLoadingContent = true;

            try
            {
                AccountCalendars.Clear();
                CalendarItems.Clear();

                foreach (var calendar in calendars)
                {
                    AccountCalendars.Add(calendar);
                    CalendarItems.Add(new AccountCalendarSettingsItemViewModel(
                        calendar,
                        ShowAsOptions,
                        AvailableColors,
                        isSynchronizationConfigurable,
                        CalendarDefaultReminderOption.CreateOptions(_preferencesService.DefaultReminderDurationInSeconds, calendar.DefaultReminderInSeconds)));
                }

                SelectedPrimaryCalendar = AccountCalendars.FirstOrDefault(calendar => calendar.IsPrimary) ?? AccountCalendars.FirstOrDefault();
            }
            finally
            {
                _isLoadingContent = false;
            }

            OnPropertyChanged(nameof(HasCalendars));
        });
    }

    private static bool IsCalendarOn(MailAccount account) => account.IsCalendarAccessEnabled || account.IsCalendarAccessGranted;

    partial void OnSelectedPrimaryCalendarChanged(AccountCalendar? value)
    {
        if (_isLoadingContent || value == null || Account == null)
            return;

        _ = SetPrimaryCalendarAsync(Account.Id, value);
    }

    private async Task SetPrimaryCalendarAsync(Guid accountId, AccountCalendar primary)
    {
        try
        {
            foreach (var calendar in AccountCalendars)
                calendar.IsPrimary = calendar.Id == primary.Id;

            await _calendarService.SetPrimaryCalendarAsync(accountId, primary.Id);
        }
        catch (Exception)
        {
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    public async Task UpdateCalendarSynchronizationAsync(AccountCalendarSettingsItemViewModel item, bool isEnabled)
    {
        var calendar = item.Calendar;
        if (calendar.IsSynchronizationEnabled == isEnabled)
            return;

        item.IsSynchronizationEnabled = isEnabled;
        calendar.IsSynchronizationEnabled = isEnabled;

        try
        {
            await _calendarService.UpdateAccountCalendarAsync(calendar);
        }
        catch (Exception)
        {
            calendar.IsSynchronizationEnabled = !isEnabled;
            item.IsSynchronizationEnabled = !isEnabled;
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    public async Task UpdateCalendarDefaultShowAsAsync(AccountCalendarSettingsItemViewModel item, AccountCalendarShowAsOption option)
    {
        var calendar = item.Calendar;
        if (option == null || calendar.DefaultShowAs == option.ShowAs)
            return;

        var previous = calendar.DefaultShowAs;
        item.SelectedShowAsOption = option;
        calendar.DefaultShowAs = option.ShowAs;

        try
        {
            await _calendarService.UpdateAccountCalendarAsync(calendar);
        }
        catch (Exception)
        {
            calendar.DefaultShowAs = previous;
            item.SelectedShowAsOption = ShowAsOptions.FirstOrDefault(showAs => showAs.ShowAs == previous) ?? option;
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    /// <summary>Stores the reminder new events in the calendar get. Kept in Wino only.</summary>
    public async Task UpdateCalendarDefaultReminderAsync(AccountCalendarSettingsItemViewModel item, CalendarDefaultReminderOption option)
    {
        var calendar = item.Calendar;
        if (option == null || calendar.DefaultReminderInSeconds == option.Seconds)
            return;

        var previous = calendar.DefaultReminderInSeconds;
        item.SelectedDefaultReminderOption = option;
        calendar.DefaultReminderInSeconds = option.Seconds;

        try
        {
            await _calendarService.UpdateAccountCalendarAsync(calendar);
        }
        catch (Exception)
        {
            calendar.DefaultReminderInSeconds = previous;
            item.SelectedDefaultReminderOption = CalendarDefaultReminderOption.FindSelected(item.DefaultReminderOptions, previous);
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }

    public async Task UpdateCalendarColorAsync(AccountCalendarSettingsItemViewModel item, AppColorViewModel color)
    {
        if (color == null || item.Calendar.BackgroundColorHex == color.Hex)
            return;

        var previousColor = item.SelectedColor;
        var previousHex = item.Calendar.BackgroundColorHex;
        var previousOverride = item.Calendar.IsBackgroundColorUserOverridden;
        item.SetBackgroundColor(color);
        item.Calendar.IsBackgroundColorUserOverridden = true;

        try
        {
            await _calendarService.UpdateAccountCalendarAsync(item.Calendar);
        }
        catch (Exception)
        {
            item.SetBackgroundColor(previousColor ?? new AppColorViewModel(previousHex));
            item.Calendar.BackgroundColorHex = previousHex;
            item.Calendar.IsBackgroundColorUserOverridden = previousOverride;
            DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.MacOSMail_OperationFailed, InfoBarMessageType.Error);
        }
    }
}
