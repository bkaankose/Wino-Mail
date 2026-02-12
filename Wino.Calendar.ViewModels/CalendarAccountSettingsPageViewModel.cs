using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels;

/// <summary>
/// ViewModel for managing calendar account settings.
/// </summary>
public partial class CalendarAccountSettingsPageViewModel : CalendarBaseViewModel
{
    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;
    
    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial AccountCalendar AccountCalendar { get; set; }

    [ObservableProperty]
    public partial string AccountColorHex { get; set; } = "#0078D4";

    [ObservableProperty]
    public partial bool IsSyncEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPrimaryCalendar { get; set; }

    public ObservableCollection<ShowAsOption> ShowAsOptions { get; } = new ObservableCollection<ShowAsOption>();

    [ObservableProperty]
    public partial ShowAsOption SelectedDefaultShowAsOption { get; set; }

    public CalendarAccountSettingsPageViewModel(ICalendarService calendarService, IAccountService accountService)
    {
        _calendarService = calendarService;
        _accountService = accountService;

        // Initialize ShowAs options
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Free));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Tentative));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Busy));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.OutOfOffice));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.WorkingElsewhere));
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is AccountCalendar selectedCalendar)
        {
            Account = await _accountService.GetAccountAsync(selectedCalendar.AccountId);
            AccountCalendar = await _calendarService.GetAccountCalendarAsync(selectedCalendar.Id) ?? selectedCalendar;
        }
        else if (parameters is Guid accountId)
        {
            Account = await _accountService.GetAccountAsync(accountId);
            var calendars = await _calendarService.GetAccountCalendarsAsync(accountId);
            AccountCalendar = calendars.FirstOrDefault(c => c.IsPrimary) ?? calendars.FirstOrDefault();
        }
        else
        {
            return;
        }

        if (Account == null || AccountCalendar == null)
            return;

        // Initialize properties from AccountCalendar
        AccountColorHex = AccountCalendar.BackgroundColorHex ?? "#0078D4";
        IsSyncEnabled = AccountCalendar.IsSynchronizationEnabled;
        IsPrimaryCalendar = AccountCalendar.IsPrimary;
        SelectedDefaultShowAsOption = ShowAsOptions.FirstOrDefault(o => o.ShowAs == AccountCalendar.DefaultShowAs) ?? ShowAsOptions[2];
    }

    partial void OnAccountColorHexChanged(string value)
    {
        if (AccountCalendar != null && !string.IsNullOrEmpty(value))
        {
            AccountCalendar.BackgroundColorHex = value;
            SaveChangesAsync();
        }
    }

    partial void OnIsSyncEnabledChanged(bool value)
    {
        if (AccountCalendar != null)
        {
            AccountCalendar.IsSynchronizationEnabled = value;
            SaveChangesAsync();
        }
    }

    partial void OnIsPrimaryCalendarChanged(bool value)
    {
        if (AccountCalendar != null)
        {
            AccountCalendar.IsPrimary = value;
            SaveChangesAsync();
        }
    }

    partial void OnSelectedDefaultShowAsOptionChanged(ShowAsOption value)
    {
        if (AccountCalendar != null && value != null)
        {
            AccountCalendar.DefaultShowAs = value.ShowAs;
            SaveChangesAsync();
        }
    }

    private async void SaveChangesAsync()
    {
        if (AccountCalendar == null)
            return;

        await _calendarService.UpdateAccountCalendarAsync(AccountCalendar);
        
        // Send message to update UI
        Messenger.Send(new CalendarListUpdated(AccountCalendar));
    }
}
