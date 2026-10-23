using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Core.ViewModels;
using MailCal.Messaging.Client.Calendar;

namespace MailCal.Calendar.ViewModels;

/// <summary>
/// ViewModel for managing calendar account settings.
/// </summary>
public partial class CalendarAccountSettingsPageViewModel : CalendarBaseViewModel
{
    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;
    private readonly ICalendarCapabilityService _calendarCapabilityService;
    private int _capabilityRequestVersion;
    private CalendarCapabilities _accountCapabilities = CalendarCapabilities.None;
    public Task PendingPersistence { get; private set; } = Task.CompletedTask;

    /// <summary>What the account's calendars can do; the strictest limits across its calendars.</summary>
    public CalendarCapabilities AccountCapabilities
    {
        get => _accountCapabilities;
        private set
        {
            if (!SetProperty(ref _accountCapabilities, value ?? CalendarCapabilities.None))
                return;

            OnPropertyChanged(nameof(HasCapabilities));
            OnPropertyChanged(nameof(IsCalendarReadOnly));
            OnPropertyChanged(nameof(SupportsAttachments));
            OnPropertyChanged(nameof(SupportsAttendees));
            OnPropertyChanged(nameof(SendsInvitations));
            OnPropertyChanged(nameof(MaxAttendees));
            OnPropertyChanged(nameof(AttendeeLimitText));
            OnPropertyChanged(nameof(QuotaAvailableBytes));
            OnPropertyChanged(nameof(QuotaAvailableText));
            OnPropertyChanged(nameof(HasQuotaWarning));
            OnPropertyChanged(nameof(QuotaWarningText));
            OnPropertyChanged(nameof(IsServerReported));
            OnPropertyChanged(nameof(CapabilitySummaryText));
        }
    }

    public bool HasCapabilities => AccountCapabilities.Source != CalendarCapabilitySource.None;
    public bool IsCalendarReadOnly => AccountCapabilities.IsReadOnly;
    public bool SupportsAttachments => AccountCapabilities.SupportsAttachments;
    public bool SupportsAttendees => AccountCapabilities.SupportsAttendees;
    public bool SendsInvitations => AccountCapabilities.SendsInvitations;
    public int? MaxAttendees => AccountCapabilities.MaxAttendees is int max && max > 0 ? max : null;
    public string AttendeeLimitText => CalendarCapabilityPresenter.GetAttendeeLimitText(AccountCapabilities);
    public long? QuotaAvailableBytes => AccountCapabilities.QuotaAvailableBytes;
    public string QuotaAvailableText => AccountCapabilities.QuotaAvailableBytes is long available
        ? CalendarCapabilityPresenter.FormatStorageSize(available)
        : string.Empty;
    public bool HasQuotaWarning => CalendarCapabilityPresenter.HasLowQuota(AccountCapabilities);
    public string QuotaWarningText => CalendarCapabilityPresenter.GetQuotaWarningText(AccountCapabilities);

    /// <summary>False until the CalDAV server was discovered; fixed provider sets are never server-reported.</summary>
    public bool IsServerReported => AccountCapabilities.IsServerReported;

    /// <summary>One translated paragraph: read-only, attachments, invitations, attendee limit and storage.</summary>
    public string CapabilitySummaryText => CalendarCapabilityPresenter.BuildSummary(AccountCapabilities);

    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial AccountCalendar AccountCalendar { get; set; }

    [ObservableProperty]
    public partial string AccountColorHex { get; set; } = "#0078D4";

    [ObservableProperty]
    public partial bool IsSyncEnabled { get; set; }

    public ObservableCollection<ShowAsOption> ShowAsOptions { get; } = new ObservableCollection<ShowAsOption>();

    [ObservableProperty]
    public partial ShowAsOption SelectedDefaultShowAsOption { get; set; }

    public CalendarAccountSettingsPageViewModel(ICalendarService calendarService,
                                                IAccountService accountService,
                                                ICalendarCapabilityService calendarCapabilityService = null)
    {
        _calendarService = calendarService;
        _accountService = accountService;
        _calendarCapabilityService = calendarCapabilityService;

        // Initialize ShowAs options
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Free));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Tentative));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Busy));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.OutOfOffice));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.WorkingElsewhere));
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        => await InitializeNavigationAsync(mode, parameters);

    public async Task InitializeNavigationAsync(NavigationMode mode, object parameters)
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
        SelectedDefaultShowAsOption = ShowAsOptions.FirstOrDefault(o => o.ShowAs == AccountCalendar.DefaultShowAs) ?? ShowAsOptions[2];

        await LoadAccountCapabilitiesAsync(Account);
        await PendingPersistence;
    }

    private async Task LoadAccountCapabilitiesAsync(MailAccount account)
    {
        var requestVersion = ++_capabilityRequestVersion;
        var capabilities = CalendarCapabilityPresenter.GetFallback(account, null);

        if (_calendarCapabilityService != null)
        {
            try
            {
                var reported = await _calendarCapabilityService.GetAccountCapabilitiesAsync(account.Id).ConfigureAwait(false);
                if (reported != null && reported.Source != CalendarCapabilitySource.None)
                    capabilities = reported;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not load the calendar capabilities of account {AccountId}", account.Id);
            }
        }

        await ExecuteUIThread(() =>
        {
            // Another navigation started a newer load.
            if (requestVersion == _capabilityRequestVersion)
                AccountCapabilities = capabilities;
        }).ConfigureAwait(false);
    }

    partial void OnAccountColorHexChanged(string value)
    {
        if (AccountCalendar != null && !string.IsNullOrEmpty(value))
        {
            AccountCalendar.BackgroundColorHex = value;
            AccountCalendar.IsBackgroundColorUserOverridden = true;
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

    partial void OnSelectedDefaultShowAsOptionChanged(ShowAsOption value)
    {
        if (AccountCalendar != null && value != null)
        {
            AccountCalendar.DefaultShowAs = value.ShowAs;
            SaveChangesAsync();
        }
    }

    public Task RetryPersistenceAsync()
    {
        SaveChangesAsync();
        return PendingPersistence;
    }

    private void SaveChangesAsync()
    {
        if (AccountCalendar == null)
            return;
        PendingPersistence = PersistAsync(PendingPersistence, AccountCalendar);
    }

    private async Task PersistAsync(Task previous, AccountCalendar calendar)
    {
        // A later accepted edit retries the latest state after a failed earlier write.
        try { await previous; } catch { }
        await _calendarService.UpdateAccountCalendarAsync(calendar);

        // Send message to update UI
        Messenger.Send(new CalendarListUpdated(calendar));
    }
}
