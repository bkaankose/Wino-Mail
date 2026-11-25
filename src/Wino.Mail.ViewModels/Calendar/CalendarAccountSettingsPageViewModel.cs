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
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.ViewModels.Calendar;

/// <summary>
/// ViewModel for managing calendar account settings.
/// </summary>
public partial class CalendarAccountSettingsPageViewModel : CalendarBaseViewModel
{
    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;
    private readonly ICalendarCapabilityService _calendarCapabilityService;
    private readonly IPreferencesService _preferencesService;
    private bool _isLoadingDefaultReminder;
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
    [NotifyPropertyChangedFor(nameof(IsSyncConfigurable))]
    public partial MailAccount Account { get; set; }

    public bool IsSyncConfigurable => Account?.HasRemoteCalendarSource() == true;

    [ObservableProperty]
    public partial AccountCalendar AccountCalendar { get; set; }

    [ObservableProperty]
    public partial string AccountColorHex { get; set; } = "#0078D4";

    [ObservableProperty]
    public partial bool IsSyncEnabled { get; set; }

    public ObservableCollection<ShowAsOption> ShowAsOptions { get; } = new ObservableCollection<ShowAsOption>();

    [ObservableProperty]
    public partial ShowAsOption SelectedDefaultShowAsOption { get; set; }

    public ObservableCollection<CalendarDefaultReminderOption> DefaultReminderOptions { get; } = [];

    [ObservableProperty]
    public partial CalendarDefaultReminderOption SelectedDefaultReminderOption { get; set; }

    public CalendarAccountSettingsPageViewModel(IMessenger messenger, ICalendarService calendarService,
                                                IAccountService accountService,
                                                ICalendarCapabilityService calendarCapabilityService = null,
                                                IPreferencesService preferencesService = null) : base(messenger)
    {
        _calendarService = calendarService;
        _accountService = accountService;
        _calendarCapabilityService = calendarCapabilityService;
        _preferencesService = preferencesService;

        // Initialize ShowAs options
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Free));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Tentative));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.Busy));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.OutOfOffice));
        ShowAsOptions.Add(new ShowAsOption(CalendarItemShowAs.WorkingElsewhere));
    }

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
        => await InitializeNavigationAsync(mode, parameters);

    public async Task InitializeNavigationAsync(NavigationMode mode, object? parameters)
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
        LoadDefaultReminderOptions(AccountCalendar.DefaultReminderInSeconds);

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

    private void LoadDefaultReminderOptions(long? storedValue)
    {
        _isLoadingDefaultReminder = true;
        DefaultReminderOptions.Clear();

        foreach (var option in CalendarDefaultReminderOption.CreateOptions(_preferencesService?.DefaultReminderDurationInSeconds ?? 0, storedValue))
            DefaultReminderOptions.Add(option);

        SelectedDefaultReminderOption = CalendarDefaultReminderOption.FindSelected(DefaultReminderOptions, storedValue);
        _isLoadingDefaultReminder = false;
    }

    partial void OnSelectedDefaultReminderOptionChanged(CalendarDefaultReminderOption value)
    {
        if (_isLoadingDefaultReminder || AccountCalendar == null || value == null || AccountCalendar.DefaultReminderInSeconds == value.Seconds)
            return;

        AccountCalendar.DefaultReminderInSeconds = value.Seconds;
        SaveChangesAsync();
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

/// <summary>One choice for a calendar's default reminder; <see cref="Seconds"/> null uses the app's default.</summary>
public sealed class CalendarDefaultReminderOption(long? seconds, string displayText)
{
    private static readonly long[] Choices = [0, 5 * 60, 10 * 60, 15 * 60, 30 * 60, 3600, 2 * 3600, 86400, 2 * 86400, 7 * 86400];

    public long? Seconds { get; } = seconds;
    public string DisplayText { get; } = displayText;
    public override string ToString() => DisplayText;

    /// <summary>The app default, none, and the fixed choices, plus <paramref name="storedValue"/> when it is not one of them.</summary>
    public static List<CalendarDefaultReminderOption> CreateOptions(long appDefaultReminderInSeconds, long? storedValue)
    {
        var options = new List<CalendarDefaultReminderOption>
        {
            new(null, string.Format(
                Translator.CalendarReminder_UseAppDefault,
                appDefaultReminderInSeconds > 0 ? CalendarReminderFormatter.Format(appDefaultReminderInSeconds) : Translator.CalendarReminder_None)),
            new(AccountCalendar.NoDefaultReminder, Translator.CalendarReminder_None)
        };

        var values = Choices.ToList();
        if (storedValue is { } stored && stored != AccountCalendar.NoDefaultReminder && !values.Contains(stored))
            values.Add(stored);

        options.AddRange(values.OrderBy(value => value).Select(value => new CalendarDefaultReminderOption(value, CalendarReminderFormatter.Format(value))));
        return options;
    }

    public static CalendarDefaultReminderOption FindSelected(IEnumerable<CalendarDefaultReminderOption> options, long? storedValue)
        => options.FirstOrDefault(option => option.Seconds == storedValue) ?? options.First();
}
