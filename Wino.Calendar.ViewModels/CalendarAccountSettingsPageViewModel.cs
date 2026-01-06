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

        if (parameters is not Guid accountId)
            return;

        // Load account
        Account = await _accountService.GetAccountAsync(accountId);
        
        if (Account == null)
            return;

        // Load first primary calendar for this account
        var calendars = await _calendarService.GetAccountCalendarsAsync(accountId);
        AccountCalendar = calendars.FirstOrDefault(c => c.IsPrimary) ?? calendars.FirstOrDefault();

        if (AccountCalendar == null)
            return;

        // Initialize properties from AccountCalendar
        AccountColorHex = AccountCalendar.BackgroundColorHex ?? "#0078D4";
        IsSyncEnabled = AccountCalendar.IsExtended;
        IsPrimaryCalendar = AccountCalendar.IsPrimary;

        // TODO: Default ShowAs is not stored in AccountCalendar yet, defaulting to Busy
        SelectedDefaultShowAsOption = ShowAsOptions[2]; // Busy
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
            AccountCalendar.IsExtended = value;
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
        // TODO: Default ShowAs should be stored in AccountCalendar or account preferences
        // For now, this is just a placeholder as the property doesn't exist yet
        if (value != null)
        {
            // Future: Store value.ShowAs somewhere
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
