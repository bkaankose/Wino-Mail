#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The review step: one row per app with a switch and a drop-down for where its data lives.
/// "On this device" is always offered. "Synced" is offered when the route can serve the app. Entering another server
/// is offered on the IMAP route only, where Calendar and To Do share one CalDAV server.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private const string ProviderSourceId = "provider";
    private const string LocalSourceId = "local";
    private const string EnterSourceId = "enter";

    private bool _isSyncingRows;

    public List<AccountSetupAppRow> AppRows { get; private set; } = [];

    public List<AccountSetupResultRow> ResultRows { get; private set; } = [];

    /// <summary>Add account is checking a server the user entered, or one that needs its own sign-in.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool IsReviewBusy { get; set; }

    public bool IsReviewNameVisible => Step == AccountSetupStep.Review;

    /// <summary>The IMAP route reaches the review already signed in; the other routes sign in from it.</summary>
    public bool IsReviewSignedInVisible => Step == AccountSetupStep.Review && IsImapRoute && IsSignInValidated;

    /// <summary>The DAV sign-in sits under the rows once a synced app cannot use the mail sign-in.</summary>
    public bool IsReviewDavSignInVisible => Step == AccountSetupStep.Review &&
                                            IsImapRoute &&
                                            (UsesCalDav || IsContactRemote) &&
                                            (IsDavPasswordRequired || (HasManualDavEntry && !UseMailSignInForDav));

    /// <summary>A server the user entered can reuse the mail password; a browser sign-in has no password to reuse.</summary>
    public bool IsUseMailSignInForDavVisible => Step == AccountSetupStep.Review &&
                                                IsImapRoute &&
                                                HasManualDavEntry &&
                                                _oauthSession == null &&
                                                !IsDavPasswordRequired;

    /// <summary>What a POP3 account means, said once before the account is added.</summary>
    public bool IsPop3NoticeVisible => Step == AccountSetupStep.Review && IsPop3Route;

    public bool IsServerDetailsVisible => Step == AccountSetupStep.Review && IsImapRoute && _pendingServer != null;

    public string ServerDetailsIncoming => FormatServer(_pendingServer?.IncomingServer, _pendingServer?.IncomingServerPort);
    public string ServerDetailsOutgoing => FormatServer(_pendingServer?.OutgoingServer, _pendingServer?.OutgoingServerPort);
    public string ServerDetailsCalDav => CalDavServiceUri?.ToString() ?? Translator.AccountSetup_Review_NotUsed;
    public string ServerDetailsCardDav => CardDavServiceUri?.ToString() ?? Translator.AccountSetup_Review_NotUsed;

    /// <summary>One line above the footer: what Add account or the sign-in button will do with these choices.</summary>
    public string ReviewCaption
    {
        get
        {
            if (Step != AccountSetupStep.Review || HasFeaturesError)
                return string.Empty;

            if (!Capabilities.HasAnyCapability)
            {
                return IsLocalOnlyMode
                    ? Translator.AccountSetup_LocalCapabilityRequired
                    : Translator.ProviderSelection_CapabilityValidationMessage;
            }

            if (IsLocalOnlyMode || !Capabilities.RequiresRemoteService)
                return Translator.AccountSetup_Review_CaptionLocal;

            return string.Empty;
        }
    }

    public bool HasReviewCaption => !string.IsNullOrEmpty(ReviewCaption);

    /// <summary>Gmail, Outlook and Exchange sign in from the review footer; the wait shows as one status line.</summary>
    public bool IsReviewStatusVisible => Step == AccountSetupStep.Review &&
                                         (IsReviewBusy || ConnectionStatusKind is AccountSetupConnectionStatus.Waiting or AccountSetupConnectionStatus.Checking);

    public string ReviewStatusText => IsReviewBusy ? Translator.AccountSetup_Status_Checking : ConnectionStatusText;

    private bool HasManualDavEntry => IsCalDavManualEntryVisible || IsCardDavManualEntryVisible;

    private static string FormatServer(string? host, string? port)
        => string.IsNullOrWhiteSpace(host)
            ? Translator.AccountSetup_Review_NotUsed
            : string.IsNullOrWhiteSpace(port) ? host : $"{host} : {port}";

    /// <summary>Builds the rows from <see cref="Capabilities"/>; called each time the review step is entered.</summary>
    private void BuildAppRows()
    {
        foreach (var row in AppRows)
            row.PropertyChanged -= OnAppRowPropertyChanged;

        var rows = new List<AccountSetupAppRow>();

        _isSyncingRows = true;
        try
        {
            if (Capabilities.IsMailEnabled)
                rows.Add(CreateMailRow());

            rows.Add(CreateModeRow(
                AccountSetupAppKind.Calendar,
                Translator.ProviderSelection_UseForCalendar,
                WinoIconGlyph.Calendar,
                Capabilities.CalendarMode,
                Capabilities.IsCalendarProviderModeAvailable,
                GetCalendarSyncedText(),
                _calDavAvailability,
                IsCalDavManualEntryVisible && IsCalendarRemote,
                ManualCalDavUrl,
                Translator.ProviderSelection_SourceCalDav));

            rows.Add(CreateModeRow(
                AccountSetupAppKind.Contacts,
                Translator.ProviderSelection_UseForContacts,
                WinoIconGlyph.People,
                Capabilities.ContactMode,
                Capabilities.IsContactProviderModeAvailable,
                GetContactSyncedText(),
                _cardDavAvailability,
                IsCardDavManualEntryVisible && IsContactRemote,
                ManualCardDavUrl,
                Translator.ProviderSelection_SourceCardDav));

            rows.Add(CreateModeRow(
                AccountSetupAppKind.Tasks,
                Translator.ProviderSelection_UseForTasks,
                WinoIconGlyph.TaskList,
                Capabilities.TaskMode,
                Capabilities.IsTaskProviderModeAvailable,
                GetTaskSyncedText(),
                _calDavAvailability,
                IsCalDavManualEntryVisible && IsTaskDavRemote,
                ManualCalDavUrl,
                Translator.ProviderSelection_SourceCalDav));
        }
        finally
        {
            _isSyncingRows = false;
        }

        foreach (var row in rows)
            row.PropertyChanged += OnAppRowPropertyChanged;

        AppRows = rows;
        OnPropertyChanged(nameof(AppRows));
        NotifyReviewStateChanged();
    }

    private AccountSetupAppRow CreateMailRow()
    {
        var options = new List<AccountSetupSourceOption>
        {
            new(nameof(InitialSynchronizationRange.ThreeMonths), Translator.AccountSetup_Range_3Months),
            new(nameof(InitialSynchronizationRange.SixMonths), Translator.AccountSetup_Range_6Months),
            new(nameof(InitialSynchronizationRange.NineMonths), Translator.AccountSetup_Range_9Months),
            new(nameof(InitialSynchronizationRange.OneYear), Translator.AccountSetup_Range_Year),
            new(nameof(InitialSynchronizationRange.Everything), Translator.AccountSetup_Range_Everything)
        };

        var range = (SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths).ToString();
        var row = new AccountSetupAppRow(AccountSetupAppKind.Mail, Translator.ProviderSelection_UseForMail, WinoIconGlyph.Mail, isSwitchVisible: false, options)
        {
            SelectedOption = options.FirstOrDefault(option => option.Id == range) ?? options[1]
        };

        row.Note = GetMailNote();
        return row;
    }

    private AccountSetupAppRow CreateModeRow(
        AccountSetupAppKind kind,
        string title,
        WinoIconGlyph glyph,
        AccountCapabilityMode mode,
        bool isProviderAvailable,
        string syncedText,
        AccountSetupDavAvailability davAvailability,
        bool isManualEntry,
        string manualUrl,
        string protocolName)
    {
        var options = new List<AccountSetupSourceOption>();

        if (!IsLocalOnlyMode && isProviderAvailable)
            options.Add(new AccountSetupSourceOption(ProviderSourceId, syncedText));

        options.Add(new AccountSetupSourceOption(LocalSourceId, Translator.AccountSetup_Choice_OnThisDevice));

        if (IsDavRoute)
        {
            options.Add(new AccountSetupSourceOption(EnterSourceId, string.Format(
                isProviderAvailable ? Translator.AccountSetup_Review_AnotherServer : Translator.AccountSetup_Review_EnterServer,
                protocolName)));
        }

        var selectedId = mode == AccountCapabilityMode.Provider
            ? isManualEntry ? EnterSourceId : ProviderSourceId
            : LocalSourceId;

        var row = new AccountSetupAppRow(kind, title, glyph, isSwitchVisible: true, options)
        {
            IsOn = mode != AccountCapabilityMode.Off,
            SelectedOption = options.FirstOrDefault(option => option.Id == selectedId)
                             ?? options.First(option => option.Id == LocalSourceId),
            ServerUrl = manualUrl,
            IsServerEntryVisible = isManualEntry
        };

        if (IsDavRoute)
        {
            row.StatusText = davAvailability switch
            {
                AccountSetupDavAvailability.Available => string.Empty,
                AccountSetupDavAvailability.NeedsSignIn => Translator.AccountSetup_Review_NeedsSignIn,
                AccountSetupDavAvailability.Failed => Translator.AccountSetup_Review_NotAvailable,
                _ => Translator.AccountSetup_Review_NotFound
            };
        }
        else if (!IsLocalOnlyMode && !IsPop3Route)
        {
            row.StatusText = isProviderAvailable ? string.Empty : Translator.AccountSetup_Review_NotOffered;
        }

        if (options.Count == 1)
            row.StatusText = Translator.AccountSetup_Choice_OnThisDevice;

        return row;
    }

    private string GetMailNote()
        => SelectedInitialSynchronizationRange?.IsEverything == true ? Translator.AccountSetup_Range_EverythingCaption_Device : string.Empty;

    private string GetCalendarSyncedText()
        => IsExchangeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, ExchangeProviderName)
            : IsNativeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, NativeProviderName)
            : string.Format(Translator.AccountSetup_Choice_DavAt, FoundCalDavHost);

    private string GetContactSyncedText()
        => IsExchangeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, ExchangeProviderName)
            : IsNativeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, NativeProviderName)
            : string.Format(Translator.AccountSetup_Choice_CardDavAt, FoundCardDavHost);

    private string GetTaskSyncedText() => Route switch
    {
        AccountRoute.Gmail => string.Format(Translator.AccountSetup_Choice_SyncedWith, Translator.AccountSetup_Provider_GoogleTasks),
        AccountRoute.Outlook => string.Format(Translator.AccountSetup_Choice_SyncedWith, Translator.AccountSetup_Provider_MicrosoftToDo),
        AccountRoute.Exchange => string.Format(Translator.AccountSetup_Choice_SyncedWith, ExchangeProviderName),
        _ => string.Format(Translator.AccountSetup_Choice_DavAt, FoundCalDavHost)
    };

    private string FoundCalDavHost => (_foundCalDavUri ?? _resolution?.Support.Calendar.ServiceUri)?.Host ?? ImapHostName;
    private string FoundCardDavHost => (_foundCardDavUri ?? _resolution?.Support.Contacts.ServiceUri)?.Host ?? ImapHostName;

    private AccountSetupAppRow? FindRow(AccountSetupAppKind kind) => AppRows.FirstOrDefault(row => row.Kind == kind);

    private void OnAppRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSyncingRows || sender is not AccountSetupAppRow row)
            return;

        switch (e.PropertyName)
        {
            case nameof(AccountSetupAppRow.SelectedOption):
                // A drop-down that loses its selection while the page rebinds keeps the mode it had.
                if (row.SelectedOption == null)
                    return;

                ApplyRow(row);
                break;
            case nameof(AccountSetupAppRow.IsOn):
                ApplyRow(row);
                break;
            case nameof(AccountSetupAppRow.ServerUrl):
                ApplyServerUrl(row);
                break;
        }
    }

    private void ApplyRow(AccountSetupAppRow row)
    {
        var id = row.SelectedOption?.Id ?? LocalSourceId;

        _isSyncingRows = true;
        try
        {
            switch (row.Kind)
            {
                case AccountSetupAppKind.Mail:
                    var range = InitialSynchronizationRanges.FirstOrDefault(option => option.Range.ToString() == id);
                    if (range != null)
                        SelectedInitialSynchronizationRange = range;

                    row.Note = GetMailNote();
                    break;
                case AccountSetupAppKind.Calendar:
                    Capabilities.CalendarMode = ToMode(row, id);
                    AlignCalDavRows(row, AccountSetupAppKind.Tasks);
                    break;
                case AccountSetupAppKind.Tasks:
                    Capabilities.TaskMode = ToMode(row, id);
                    AlignCalDavRows(row, AccountSetupAppKind.Calendar);
                    break;
                case AccountSetupAppKind.Contacts:
                    Capabilities.ContactMode = ToMode(row, id);
                    break;
            }

            RefreshManualEntryState();
            RefreshDavSignInRequirement();
        }
        finally
        {
            _isSyncingRows = false;
        }

        ClearRowErrors();
        FeaturesError = string.Empty;
        NotifyStepContentChanged();
    }

    private static AccountCapabilityMode ToMode(AccountSetupAppRow row, string id)
        => !row.IsOn
            ? AccountCapabilityMode.Off
            : id == LocalSourceId ? AccountCapabilityMode.Local : AccountCapabilityMode.Provider;

    /// <summary>
    /// Calendar and To Do share one CalDAV server. When one of them moves between the found server and an entered one,
    /// the other follows if it is synced too, so the two rows never name different servers.
    /// </summary>
    private void AlignCalDavRows(AccountSetupAppRow changed, AccountSetupAppKind otherKind)
    {
        if (!IsImapRoute || !changed.IsOn)
            return;

        var changedId = changed.SelectedOption?.Id;
        if (changedId is not (ProviderSourceId or EnterSourceId))
            return;

        var other = FindRow(otherKind);
        var otherId = other?.SelectedOption?.Id;
        if (other == null || !other.IsOn || otherId == changedId || otherId is not (ProviderSourceId or EnterSourceId))
            return;

        var match = other.Options.FirstOrDefault(option => option.Id == changedId);
        if (match != null)
            other.SelectedOption = match;
    }

    private void RefreshManualEntryState()
    {
        var calendar = FindRow(AccountSetupAppKind.Calendar);
        var tasks = FindRow(AccountSetupAppKind.Tasks);
        var contacts = FindRow(AccountSetupAppKind.Contacts);

        var isCalendarEntered = IsEntered(calendar);
        var isTasksEntered = IsEntered(tasks);
        var isContactsEntered = IsEntered(contacts);

        IsCalDavManualEntryVisible = isCalendarEntered || isTasksEntered;
        IsCardDavManualEntryVisible = isContactsEntered;

        if (calendar != null)
            calendar.IsServerEntryVisible = isCalendarEntered;

        // One address field for the shared CalDAV server: on the Calendar row when it is entered there, otherwise on To Do.
        if (tasks != null)
            tasks.IsServerEntryVisible = isTasksEntered && !isCalendarEntered;

        if (contacts != null)
            contacts.IsServerEntryVisible = isContactsEntered;

        if (tasks != null)
            tasks.Note = isTasksEntered && isCalendarEntered ? Translator.AccountSetup_Review_SameCalDavServer : string.Empty;
    }

    private static bool IsEntered(AccountSetupAppRow? row) => row is { IsOn: true } && row.SelectedOption?.Id == EnterSourceId;

    private void ApplyServerUrl(AccountSetupAppRow row)
    {
        _isSyncingRows = true;
        try
        {
            if (row.Kind == AccountSetupAppKind.Contacts)
            {
                ManualCardDavUrl = row.ServerUrl;
            }
            else
            {
                ManualCalDavUrl = row.ServerUrl;

                var other = FindRow(row.Kind == AccountSetupAppKind.Calendar ? AccountSetupAppKind.Tasks : AccountSetupAppKind.Calendar);
                if (other != null && other.ServerUrl != row.ServerUrl)
                    other.ServerUrl = row.ServerUrl;
            }
        }
        finally
        {
            _isSyncingRows = false;
        }

        if (row.HasError)
            row.Error = string.Empty;

        OnPropertyChanged(nameof(ServerDetailsCalDav));
        OnPropertyChanged(nameof(ServerDetailsCardDav));
    }

    private void SetRowError(AccountSetupAppKind kind, string message)
    {
        var row = FindRow(kind);
        if (row is { IsOn: true } && row.SelectedOption?.Id != LocalSourceId)
            row.Error = message;
    }

    private void ClearRowErrors()
    {
        foreach (var row in AppRows)
        {
            if (row.HasError)
                row.Error = string.Empty;
        }
    }

    /// <summary>What was set up, under "Account added": each app that is on and where its data lives.</summary>
    private void BuildResultRows()
    {
        var rows = new List<AccountSetupResultRow>();

        if (Capabilities.IsMailEnabled)
            rows.Add(new AccountSetupResultRow(WinoIconGlyph.Mail, Translator.ProviderSelection_UseForMail, Translator.AccountSetup_Result_MailDownloading));

        AddResultRow(rows, WinoIconGlyph.Calendar, Translator.ProviderSelection_UseForCalendar, Capabilities.CalendarMode);
        AddResultRow(rows, WinoIconGlyph.People, Translator.ProviderSelection_UseForContacts, Capabilities.ContactMode);
        AddResultRow(rows, WinoIconGlyph.TaskList, Translator.ProviderSelection_UseForTasks, Capabilities.TaskMode);

        ResultRows = rows;
        OnPropertyChanged(nameof(ResultRows));
    }

    private static void AddResultRow(List<AccountSetupResultRow> rows, WinoIconGlyph glyph, string title, AccountCapabilityMode mode)
    {
        if (mode == AccountCapabilityMode.Off)
            return;

        rows.Add(new AccountSetupResultRow(glyph, title, mode == AccountCapabilityMode.Local
            ? Translator.AccountSetup_Choice_OnThisDevice
            : Translator.AccountSetup_Result_Syncing));
    }

    private void NotifyReviewStateChanged()
    {
        OnPropertyChanged(nameof(IsReviewNameVisible));
        OnPropertyChanged(nameof(IsReviewSignedInVisible));
        OnPropertyChanged(nameof(IsPop3NoticeVisible));
        OnPropertyChanged(nameof(IsReviewDavSignInVisible));
        OnPropertyChanged(nameof(IsUseMailSignInForDavVisible));
        OnPropertyChanged(nameof(IsServerDetailsVisible));
        OnPropertyChanged(nameof(ServerDetailsIncoming));
        OnPropertyChanged(nameof(ServerDetailsOutgoing));
        OnPropertyChanged(nameof(ServerDetailsCalDav));
        OnPropertyChanged(nameof(ServerDetailsCardDav));
        OnPropertyChanged(nameof(ReviewCaption));
        OnPropertyChanged(nameof(HasReviewCaption));
        OnPropertyChanged(nameof(IsReviewStatusVisible));
        OnPropertyChanged(nameof(ReviewStatusText));
    }

    partial void OnIsReviewBusyChanged(bool value) => NotifyReviewStateChanged();

    partial void OnFeaturesErrorChanged(string value)
    {
        OnPropertyChanged(nameof(ReviewCaption));
        OnPropertyChanged(nameof(HasReviewCaption));
    }
}
