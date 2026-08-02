using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Diagnostics;
using Wino.Core.Misc;
using Wino.Core.Services;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels;

public partial class AccountDetailsPageViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ICalendarService _calendarService;
    private readonly IStatePersistanceService _statePersistanceService;
    private readonly INewThemeService _themeService;
    private readonly IImapTestService _imapTestService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IFileService _fileService;
    private readonly IPreferencesService _preferencesService;
    private readonly IWinoLogger _winoLogger;
    private bool isLoaded = false;

    [ObservableProperty]
    public partial MailAccount Account { get; set; }
    public ObservableCollection<IMailItemFolder> CurrentFolders { get; set; } = [];
    public ObservableCollection<AccountCalendar> AccountCalendars { get; set; } = [];
    public ObservableCollection<AccountCalendarSettingsItemViewModel> AccountCalendarSettingsItems { get; } = [];
    public ObservableCollection<AccountCalendarShowAsOption> ShowAsOptions { get; } = [];

    [ObservableProperty]
    public partial AccountCalendar SelectedPrimaryCalendar { get; set; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; } = 0; // Default to Mail tab

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string SenderName { get; set; }

    [ObservableProperty]
    public partial AppColorViewModel SelectedColor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImapServer))]
    public partial CustomServerInformation ServerInformation { get; set; }

    [ObservableProperty]
    public partial List<AppColorViewModel> AvailableColors { get; set; }

    [ObservableProperty]
    public partial int SelectedIncomingServerConnectionSecurityIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedIncomingServerAuthenticationMethodIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedOutgoingServerConnectionSecurityIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedOutgoingServerAuthenticationMethodIndex { get; set; }

    // Mail-related properties
    [ObservableProperty]
    public partial bool IsFocusedInboxEnabled { get; set; }

    [ObservableProperty]
    public partial bool AreNotificationsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsSignatureEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAppendMessageSettingVisible { get; set; }

    [ObservableProperty]
    public partial bool IsAppendMessageSettinEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsTaskbarBadgeEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsJumpListEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsProtocolLogEnabled { get; set; }

    [ObservableProperty]
    public partial AccountCapabilityOption SelectedCapabilityOption { get; set; }

    public bool IsFocusedInboxSupportedForAccount => Account != null && Account.Preferences.IsFocusedInboxEnabled != null;
    public bool IsImapServer => ServerInformation != null;
    public bool HasMailAccess => Account?.IsMailAccessGranted == true;
    public bool HasCalendarAccess => Account?.IsCalendarAccessGranted == true;
    public bool IsOAuthCapabilityEditable => Account?.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail;
    public string ProviderIconPath => Account?.SpecialImapProvider != SpecialImapProvider.None
        ? $"ms-appx:///Assets/Providers/{Account.SpecialImapProvider}.png"
        : $"ms-appx:///Assets/Providers/{Account?.ProviderType}.png";
    public string Address => Account?.Address ?? string.Empty;
    public bool IsInitialSynchronizationSummaryVisible => Account?.CreatedAt.HasValue == true && Account.InitialSynchronizationRange != InitialSynchronizationRange.Everything;
    public string InitialSynchronizationSummary => Account?.CreatedAt is not DateTime createdAtUtc
        ? string.Empty
        : Account.InitialSynchronizationRange.ToCutoffDateUtc(createdAtUtc) is not DateTime cutoffDateUtc
            ? string.Empty
            : string.Format(
            Translator.AccountDetailsPage_InitialSynchronization_Description,
            cutoffDateUtc.ToLocalTime().ToString("D", CultureInfo.CurrentUICulture));

    public ObservableCollection<ImapAuthenticationMethodModel> AvailableAuthenticationMethods { get; } =
    [
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.Auto, Translator.ImapAuthenticationMethod_Auto),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.None, Translator.ImapAuthenticationMethod_None),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.NormalPassword, Translator.ImapAuthenticationMethod_Plain),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.Ntlm, Translator.ImapAuthenticationMethod_Ntlm),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.CramMd5, Translator.ImapAuthenticationMethod_CramMD5),
        new ImapAuthenticationMethodModel(Core.Domain.Enums.ImapAuthenticationMethod.DigestMd5, Translator.ImapAuthenticationMethod_DigestMD5)
    ];

    public List<ImapConnectionSecurityModel> AvailableConnectionSecurities { get; set; } =
    [
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.Auto, Translator.ImapConnectionSecurity_Auto),
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.SslTls, Translator.ImapConnectionSecurity_SslTls),
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.StartTls, Translator.ImapConnectionSecurity_StartTls),
        new ImapConnectionSecurityModel(Core.Domain.Enums.ImapConnectionSecurity.None, Translator.ImapConnectionSecurity_None)
    ];

    public List<AccountCapabilityOption> CapabilityOptions { get; } =
    [
        new(true, false, Translator.AccountCapability_MailOnly),
        new(false, true, Translator.AccountCapability_CalendarOnly),
        new(true, true, Translator.AccountCapability_MailAndCalendar)
    ];


    public AccountDetailsPageViewModel(IMailDialogService dialogService,
        IAccountService accountService,
        IFolderService folderService,
        ICalendarService calendarService,
        IStatePersistanceService statePersistanceService,
        INewThemeService themeService,
        IImapTestService imapTestService,
        INotificationBuilder notificationBuilder,
        IApplicationConfiguration applicationConfiguration,
        IFileService fileService,
        IPreferencesService preferencesService,
        IWinoLogger winoLogger)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _folderService = folderService;
        _calendarService = calendarService;
        _statePersistanceService = statePersistanceService;
        _themeService = themeService;
        _imapTestService = imapTestService;
        _notificationBuilder = notificationBuilder;
        _applicationConfiguration = applicationConfiguration;
        _fileService = fileService;
        _preferencesService = preferencesService;
        _winoLogger = winoLogger;

        var colorHexList = _themeService.GetAvailableAccountColors();
        AvailableColors = colorHexList.Select(a => new AppColorViewModel(a)).ToList();

        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.Free, Translator.CalendarShowAs_Free));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.Tentative, Translator.CalendarShowAs_Tentative));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.Busy, Translator.CalendarShowAs_Busy));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.OutOfOffice, Translator.CalendarShowAs_OutOfOffice));
        ShowAsOptions.Add(new AccountCalendarShowAsOption(CalendarItemShowAs.WorkingElsewhere, Translator.CalendarShowAs_WorkingElsewhere));
    }

    [RelayCommand]
    private Task SetupSpecialFolders()
        => _dialogService.HandleSystemFolderConfigurationDialogAsync(Account.Id, _folderService);

    [RelayCommand]
    private void EditSignature()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsSignature_Title, WinoPage.SignatureManagementPage, Account.Id));

    [RelayCommand]
    private void ManageMailFilters()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.MailFilters_Title, WinoPage.MailFiltersPage, Account.Id));

    [RelayCommand]
    private void EditAliases()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.SettingsManageAliases_Title, WinoPage.AliasManagementPage, Account.Id));

    [RelayCommand]
    private void EditCategories()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.MailCategoryManagementPage_Title, WinoPage.MailCategoryManagementPage, Account.Id));

    [RelayCommand]
    private void CustomizeFolderList()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.FolderCustomization_Title, WinoPage.FolderCustomizationPage, Account.Id));

    [RelayCommand]
    private void EditImapCalDavSettings()
        => Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.ImapCalDavSettingsPage_TitleEdit,
            WinoPage.ImapCalDavSettingsPage,
            ImapCalDavSettingsNavigationContext.CreateForEditMode(Account.Id)));

    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        await UpdateAccountAsync();

        _dialogService.InfoBarMessage(Translator.EditAccountDetailsPage_SaveSuccess_Title, Translator.EditAccountDetailsPage_SaveSuccess_Message, InfoBarMessageType.Success);
    }

    [RelayCommand]
    private async Task ExportProtocolLogsAsync()
    {
        if (Account == null)
            return;

        var logsFolder = WinoProtocolLogger.GetAccountLogFolder(
            _applicationConfiguration.ApplicationDataFolderPath,
            Account.Id);

        if (!System.IO.Directory.Exists(logsFolder))
        {
            _dialogService.InfoBarMessage(
                Translator.Info_LogsNotFoundTitle,
                Translator.ProtocolLog_NoLogsMessage,
                InfoBarMessageType.Error);
            return;
        }

        var selectedFolderPath = await _dialogService.PickWindowsFolderAsync();
        if (string.IsNullOrEmpty(selectedFolderPath))
            return;

        try
        {
            var archiveFileName = $"Wino-Protocol-{Account.Id:N}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            var archivePath = await _fileService.CreateLogsArchiveAsync(
                logsFolder,
                selectedFolderPath,
                archiveFileName);

            if (string.IsNullOrEmpty(archivePath))
            {
                _dialogService.InfoBarMessage(
                    Translator.Info_LogsNotFoundTitle,
                    Translator.ProtocolLog_NoLogsMessage,
                    InfoBarMessageType.Error);
                return;
            }

            _dialogService.InfoBarMessage(
                Translator.ProtocolLog_ArchiveSavedTitle,
                string.Format(Translator.ProtocolLog_ArchiveSavedMessage, archiveFileName),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task UploadProtocolLogsAsync()
    {
        if (Account == null)
            return;

        var logsFolder = WinoProtocolLogger.GetAccountLogFolder(
            _applicationConfiguration.ApplicationDataFolderPath,
            Account.Id);

        if (!System.IO.Directory.Exists(logsFolder))
        {
            _dialogService.InfoBarMessage(
                Translator.Info_LogsNotFoundTitle,
                Translator.ProtocolLog_NoLogsMessage,
                InfoBarMessageType.Error);
            return;
        }

        var archiveFileName = $"Wino-Protocol-{Account.Id:N}.zip";
        var archivePath = await _fileService.CreateLogsArchiveAsync(
            logsFolder,
            _applicationConfiguration.ApplicationTempFolderPath,
            archiveFileName,
            sanitizeSensitiveData: true);

        if (string.IsNullOrEmpty(archivePath))
        {
            _dialogService.InfoBarMessage(
                Translator.Info_LogsNotFoundTitle,
                Translator.ProtocolLog_NoLogsMessage,
                InfoBarMessageType.Error);
            return;
        }

        try
        {
            await _winoLogger.UploadDiagnosticLogsAsync(archivePath, _preferencesService.DiagnosticId);
            _dialogService.InfoBarMessage(
                Translator.Info_LogsUploadedTitle,
                string.Format(Translator.Info_LogsUploadedMessage, archiveFileName),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to upload IMAP protocol logs to Sentry.");
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                Translator.Info_LogsUploadFailedMessage,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (Account == null)
            return;

        var confirmation = await _dialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_DeleteAccountConfirmationTitle,
                                                                            string.Format(Translator.DialogMessage_DeleteAccountConfirmationMessage, Account.Name),
                                                                            Translator.Buttons_Delete);

        if (!confirmation)
            return;

        await SynchronizationManager.Instance.DestroySynchronizerAsync(Account.Id);
        await _accountService.DeleteAccountAsync(Account);

        _dialogService.InfoBarMessage(Translator.Info_AccountDeletedTitle, string.Format(Translator.Info_AccountDeletedMessage, Account.Name), InfoBarMessageType.Success);

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }

    [RelayCommand]
    private async Task ValidateImapSettingsAsync()
    {
        try
        {
            var candidate = BuildCorrectedConnectionCandidate();
            await ValidateConnectionCandidateAsync(candidate);
            _dialogService.InfoBarMessage(Translator.IMAPSetupDialog_ValidationSuccess_Title, Translator.IMAPSetupDialog_ValidationSuccess_Message, InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            if (ex is ImapValidationException { ProtocolLog.Length: > 0 } validationException)
            {
                await _dialogService.ShowImapValidationFailedDialogAsync(
                    validationException.Message,
                    validationException.ProtocolLog);
            }
            else
            {
                _dialogService.InfoBarMessage(Translator.IMAPSetupDialog_ValidationFailed_Title, ex.Message, InfoBarMessageType.Error);
            }
        }
    }

    [RelayCommand]
    private async Task UpdateCustomServerInformationAsync()
    {
        try
        {
            var candidate = BuildCorrectedConnectionCandidate();
            await ValidateConnectionCandidateAsync(candidate);

            Account.ServerInformation = candidate;
            Account.AttentionReason = AccountAttentionReason.None;
            await _accountService.UpdateImapConnectionSettingsAsync(Account, candidate);
            await SynchronizationManager.Instance.DestroySynchronizerAsync(Account.Id);
            ServerInformation = candidate;

            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.FullFolders
            }));

            _dialogService.InfoBarMessage(Translator.IMAPSetupDialog_SaveImapSuccess_Title, Translator.IMAPSetupDialog_SaveImapSuccess_Message, InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.IMAPSetupDialog_ValidationFailed_Title, ex.Message, InfoBarMessageType.Error);
        }
    }

    private CustomServerInformation BuildCorrectedConnectionCandidate()
    {
        if (ServerInformation == null)
            throw new InvalidOperationException(Translator.Exception_NullAssignedAccount);

        var incomingAuth = AvailableAuthenticationMethods[SelectedIncomingServerAuthenticationMethodIndex].ImapAuthenticationMethod;
        var outgoingAuth = AvailableAuthenticationMethods[SelectedOutgoingServerAuthenticationMethodIndex].ImapAuthenticationMethod;
        if (incomingAuth == ImapAuthenticationMethod.EncryptedPassword || outgoingAuth == ImapAuthenticationMethod.EncryptedPassword)
            throw new InvalidOperationException(Translator.IMAPSetupDialog_EncryptedPasswordPromotionBlocked);

        return new CustomServerInformation
        {
            Id = ServerInformation.Id,
            AccountId = Account.Id,
            Address = ServerInformation.Address,
            IncomingServer = ServerInformation.IncomingServer,
            IncomingServerPort = ServerInformation.IncomingServerPort,
            IncomingServerUsername = ServerInformation.IncomingServerUsername,
            IncomingServerPassword = ServerInformation.IncomingServerPassword,
            IncomingServerType = ServerInformation.IncomingServerType,
            IncomingAuthenticationMethod = incomingAuth,
            IncomingServerSocketOption = AvailableConnectionSecurities[SelectedIncomingServerConnectionSecurityIndex].ImapConnectionSecurity,
            OutgoingServer = ServerInformation.OutgoingServer,
            OutgoingServerPort = ServerInformation.OutgoingServerPort,
            OutgoingServerUsername = ServerInformation.OutgoingServerUsername,
            OutgoingServerPassword = ServerInformation.OutgoingServerPassword,
            OutgoingAuthenticationMethod = outgoingAuth,
            OutgoingServerSocketOption = AvailableConnectionSecurities[SelectedOutgoingServerConnectionSecurityIndex].ImapConnectionSecurity,
            ProxyServer = ServerInformation.ProxyServer,
            ProxyServerPort = ServerInformation.ProxyServerPort,
            MaxConcurrentClients = ServerInformation.MaxConcurrentClients,
            CalendarSupportMode = ServerInformation.CalendarSupportMode,
            CalDavServiceUrl = ServerInformation.CalDavServiceUrl,
            CalDavUsername = ServerInformation.CalDavUsername,
            CalDavPassword = ServerInformation.CalDavPassword,
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected
        };
    }

    private async Task ValidateConnectionCandidateAsync(CustomServerInformation candidate)
    {
        while (true)
        {
            var result = await SynchronizationManager.Instance.TestImapConnectivityAsync(candidate);
            if (!result.IsCertificateUIRequired)
            {
                if (!result.IsSuccess)
                    throw new ImapValidationException(result.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage, result.ProtocolLog);
                return;
            }

            var failure = result.CertificateFailure;
            if (failure?.CanTrust != true)
                throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateCannotBeTrusted);

            var message = $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateProtocol}: {failure.Protocol}\n" +
                $"{Translator.IMAPSetupDialog_CertificateEndpoint}: {failure.Host}:{failure.Port}\n" +
                $"{Translator.IMAPSetupDialog_CertificateSubject}: {failure.Subject}\n" +
                $"{Translator.IMAPSetupDialog_CertificateSans}: {failure.SubjectAlternativeNames}\n" +
                $"{Translator.IMAPSetupDialog_CertificateIssuer}: {failure.Issuer}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidFrom}: {failure.ValidFromUtc:u}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidTo}: {failure.ValidToUtc:u}\n" +
                $"{Translator.IMAPSetupDialog_CertificateFingerprint}: {failure.CertificateSha256}\n\n" +
                Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1;
            if (!await _dialogService.ShowServerCertificateTrustDialogAsync(message, failure.CertificateRawData))
                throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateDenied);

            candidate.PendingCertificateTrusts.RemoveAll(item => item.Protocol == failure.Protocol &&
                string.Equals(item.Host, failure.Host, StringComparison.OrdinalIgnoreCase) && item.Port == failure.Port);
            candidate.PendingCertificateTrusts.Add(failure.CreateTrust(Account.Id));
        }
    }

    public Task FolderSyncToggledAsync(IMailItemFolder folderStructure, bool isEnabled)
        => _folderService.ChangeFolderSynchronizationStateAsync(folderStructure.Id, isEnabled);

    public async Task FolderShowUnreadToggled(IMailItemFolder folderStructure, bool isEnabled)
    {
        await _folderService.ChangeFolderShowUnreadCountStateAsync(folderStructure.Id, isEnabled);
        await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
    }

    public async Task FolderJumpListToggledAsync(IMailItemFolder folderStructure, bool isEnabled)
    {
        await _folderService.ChangeFolderJumpListStateAsync(folderStructure.Id, isEnabled);
        folderStructure.IsJumpListEnabled = isEnabled;
        await _notificationBuilder.UpdateJumpListOptionsAsync();
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is Guid accountId)
        {
            Account = await _accountService.GetAccountAsync(accountId);
            AccountName = Account.Name;
            SenderName = Account.SenderName;
            ServerInformation = Account.ServerInformation;
            SelectedCapabilityOption = ResolveCapabilityOption(Account.IsMailAccessGranted, Account.IsCalendarAccessGranted);

            IsFocusedInboxEnabled = Account.Preferences.IsFocusedInboxEnabled.GetValueOrDefault();
            AreNotificationsEnabled = Account.Preferences.IsNotificationsEnabled;
            IsSignatureEnabled = Account.Preferences.IsSignatureEnabled;

            IsAppendMessageSettingVisible = Account.ProviderType == MailProviderType.IMAP4;
            IsAppendMessageSettinEnabled = Account.Preferences.ShouldAppendMessagesToSentFolder;
            IsTaskbarBadgeEnabled = Account.Preferences.IsTaskbarBadgeEnabled;
            IsJumpListEnabled = Account.Preferences.IsJumpListEnabled;
            IsProtocolLogEnabled = Account.IsProtocolLogEnabled;

            if (!string.IsNullOrEmpty(Account.AccountColorHex))
            {
                SelectedColor = AvailableColors.FirstOrDefault(a => a.Hex == Account.AccountColorHex);
            }
            else
            {
                SelectedColor = null;
            }

            if (ServerInformation != null)
            {
                if (ServerInformation.ConnectionPolicyVersion == ImapConnectionPolicyVersion.Legacy &&
                    (ServerInformation.IncomingAuthenticationMethod == ImapAuthenticationMethod.EncryptedPassword ||
                     ServerInformation.OutgoingAuthenticationMethod == ImapAuthenticationMethod.EncryptedPassword))
                {
                    AvailableAuthenticationMethods.Insert(3, new ImapAuthenticationMethodModel(
                        ImapAuthenticationMethod.EncryptedPassword,
                        Translator.ImapAuthenticationMethod_EncryptedPassword));
                }

                SelectedIncomingServerAuthenticationMethodIndex = AvailableAuthenticationMethods
                    .Select((item, index) => (item, index))
                    .FirstOrDefault(pair => pair.item.ImapAuthenticationMethod == ServerInformation.IncomingAuthenticationMethod).index;
                SelectedIncomingServerConnectionSecurityIndex = AvailableConnectionSecurities.FindIndex(a => a.ImapConnectionSecurity == ServerInformation.IncomingServerSocketOption);
                SelectedOutgoingServerAuthenticationMethodIndex = AvailableAuthenticationMethods
                    .Select((item, index) => (item, index))
                    .FirstOrDefault(pair => pair.item.ImapAuthenticationMethod == ServerInformation.OutgoingAuthenticationMethod).index;
                SelectedOutgoingServerConnectionSecurityIndex = AvailableConnectionSecurities.FindIndex(a => a.ImapConnectionSecurity == ServerInformation.OutgoingServerSocketOption);
            }

            SelectedTabIndex = _statePersistanceService.ApplicationMode == WinoApplicationMode.Calendar && HasCalendarAccess
                ? 2
                : HasMailAccess
                    ? 1
                    : 0;

            var folderStructures = (await _folderService.GetFolderStructureForAccountAsync(Account.Id, true)).Folders;

            await ExecuteUIThread(() =>
            {
                CurrentFolders.Clear();

                foreach (var folder in folderStructures)
                {
                    CurrentFolders.Add(folder);
                }
            });

            // Load calendar list
            await LoadAccountCalendarsAsync();

            isLoaded = true;
        }
    }

    private async Task UpdateAccountAsync()
    {
        var protocolLogSettingChanged = Account.IsProtocolLogEnabled != IsProtocolLogEnabled;

        Account.Name = AccountName;
        Account.SenderName = SenderName;
        Account.AccountColorHex = SelectedColor?.Hex ?? string.Empty;
        Account.IsProtocolLogEnabled = IsProtocolLogEnabled;

        await _accountService.UpdateAccountAsync(Account);

        if (protocolLogSettingChanged)
        {
            // The MailKit protocol logger is selected when the account synchronizer and its pool
            // are constructed. Recreate it so the opt-in change takes effect immediately.
            await SynchronizationManager.Instance.DestroySynchronizerAsync(Account.Id);
        }
    }

    private async Task LoadAccountCalendarsAsync()
    {
        var calendars = await _calendarService.GetAccountCalendarsAsync(Account.Id);

        await ExecuteUIThread(() =>
        {
            AccountCalendars.Clear();
            AccountCalendarSettingsItems.Clear();

            foreach (var calendar in calendars)
            {
                AccountCalendars.Add(calendar);
                AccountCalendarSettingsItems.Add(new AccountCalendarSettingsItemViewModel(calendar, ShowAsOptions, AvailableColors));
            }
        });

        SelectedPrimaryCalendar = AccountCalendars.FirstOrDefault(calendar => calendar.IsPrimary) ?? AccountCalendars.FirstOrDefault();
    }

    public AccountCalendarShowAsOption GetShowAsOption(CalendarItemShowAs showAs)
        => ShowAsOptions.FirstOrDefault(option => option.ShowAs == showAs) ?? ShowAsOptions.First();

    public async Task UpdateCalendarSynchronizationAsync(AccountCalendar calendar, bool isEnabled)
    {
        if (calendar == null || calendar.IsSynchronizationEnabled == isEnabled)
            return;

        calendar.IsSynchronizationEnabled = isEnabled;
        await _calendarService.UpdateAccountCalendarAsync(calendar);
    }

    public async Task UpdateCalendarDefaultShowAsAsync(AccountCalendar calendar, AccountCalendarShowAsOption option)
    {
        if (calendar == null || option == null || calendar.DefaultShowAs == option.ShowAs)
            return;

        calendar.DefaultShowAs = option.ShowAs;
        await _calendarService.UpdateAccountCalendarAsync(calendar);
    }

    public async Task UpdateCalendarColorAsync(AccountCalendarSettingsItemViewModel calendarItem, AppColorViewModel color)
    {
        if (calendarItem?.Calendar == null || color == null || calendarItem.Calendar.BackgroundColorHex == color.Hex)
            return;

        calendarItem.SetBackgroundColor(color);
        calendarItem.Calendar.IsBackgroundColorUserOverridden = true;
        await _calendarService.UpdateAccountCalendarAsync(calendarItem.Calendar);
    }

    [RelayCommand]
    private void ResetColor()
        => SelectedColor = null;

    partial void OnSelectedColorChanged(AppColorViewModel oldValue, AppColorViewModel newValue)
    {
        if (Account != null)
        {
            _ = UpdateAccountAsync();
        }
    }

    partial void OnAccountChanged(MailAccount value)
    {
        SelectedCapabilityOption = ResolveCapabilityOption(value?.IsMailAccessGranted == true, value?.IsCalendarAccessGranted == true);
        OnPropertyChanged(nameof(IsFocusedInboxSupportedForAccount));
        OnPropertyChanged(nameof(ProviderIconPath));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(IsInitialSynchronizationSummaryVisible));
        OnPropertyChanged(nameof(InitialSynchronizationSummary));
        OnPropertyChanged(nameof(HasMailAccess));
        OnPropertyChanged(nameof(HasCalendarAccess));
        OnPropertyChanged(nameof(IsOAuthCapabilityEditable));
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (!isLoaded) return;

        switch (e.PropertyName)
        {
            case nameof(IsFocusedInboxEnabled) when IsFocusedInboxSupportedForAccount:
                Account.Preferences.IsFocusedInboxEnabled = IsFocusedInboxEnabled;
                await _accountService.UpdateAccountAsync(Account);
                await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
                break;
            case nameof(AreNotificationsEnabled):
                Account.Preferences.IsNotificationsEnabled = AreNotificationsEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(IsAppendMessageSettinEnabled):
                Account.Preferences.ShouldAppendMessagesToSentFolder = IsAppendMessageSettinEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(IsSignatureEnabled):
                Account.Preferences.IsSignatureEnabled = IsSignatureEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(IsTaskbarBadgeEnabled):
                Account.Preferences.IsTaskbarBadgeEnabled = IsTaskbarBadgeEnabled;
                await _accountService.UpdateAccountAsync(Account);
                await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
                break;
            case nameof(IsJumpListEnabled):
                Account.Preferences.IsJumpListEnabled = IsJumpListEnabled;
                await _accountService.UpdateAccountAsync(Account);
                break;
            case nameof(SelectedCapabilityOption) when IsOAuthCapabilityEditable && SelectedCapabilityOption != null:
                if (Account.IsMailAccessGranted == SelectedCapabilityOption.IsMailAccessGranted &&
                    Account.IsCalendarAccessGranted == SelectedCapabilityOption.IsCalendarAccessGranted)
                    break;

                try
                {
                    await UpdateOAuthCapabilityAsync(SelectedCapabilityOption);
                    await _notificationBuilder.UpdateTaskbarIconBadgeAsync();
                }
                catch (Exception ex)
                {
                    await ExecuteUIThread(() => SelectedCapabilityOption = ResolveCapabilityOption(Account.IsMailAccessGranted, Account.IsCalendarAccessGranted));
                    _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
                }
                break;
            case nameof(SelectedPrimaryCalendar) when SelectedPrimaryCalendar != null:
                foreach (var calendar in AccountCalendars)
                {
                    calendar.IsPrimary = calendar.Id == SelectedPrimaryCalendar.Id;
                }

                await _calendarService.SetPrimaryCalendarAsync(Account.Id, SelectedPrimaryCalendar.Id);
                break;
        }
    }

    private AccountCapabilityOption ResolveCapabilityOption(bool isMailAccessGranted, bool isCalendarAccessGranted)
        => CapabilityOptions.First(option =>
            option.IsMailAccessGranted == isMailAccessGranted &&
            option.IsCalendarAccessGranted == isCalendarAccessGranted);

    private async Task UpdateOAuthCapabilityAsync(AccountCapabilityOption selectedOption)
    {
        var previousMailAccess = Account.IsMailAccessGranted;
        var previousCalendarAccess = Account.IsCalendarAccessGranted;
        var requiresReauthorization = IsOAuthCapabilityEditable &&
                                      (selectedOption.IsMailAccessGranted != previousMailAccess ||
                                       selectedOption.IsCalendarAccessGranted != previousCalendarAccess);

        try
        {
            if (requiresReauthorization)
            {
                Account.IsMailAccessGranted = selectedOption.IsMailAccessGranted;
                Account.IsCalendarAccessGranted = selectedOption.IsCalendarAccessGranted;

                await SynchronizationManager.Instance.HandleAuthorizationAsync(
                    Account.ProviderType,
                    Account,
                    Account.ProviderType == MailProviderType.Gmail,
                    forceInteractive: true);
            }
        }
        catch
        {
            Account.IsMailAccessGranted = previousMailAccess;
            Account.IsCalendarAccessGranted = previousCalendarAccess;
            throw;
        }

        Account.IsMailAccessGranted = selectedOption.IsMailAccessGranted;
        Account.IsCalendarAccessGranted = selectedOption.IsCalendarAccessGranted;

        await _accountService.UpdateAccountAsync(Account);

        if (selectedOption.IsMailAccessGranted && !previousMailAccess)
        {
            await SynchronizationManager.Instance.SynchronizeProfileAsync(Account.Id);
            await SynchronizationManager.Instance.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.FullFolders
            });

            if (Account.ProviderType == MailProviderType.Outlook)
            {
                await SynchronizationManager.Instance.SynchronizeMailAsync(new MailSynchronizationOptions
                {
                    AccountId = Account.Id,
                    Type = MailSynchronizationType.Categories
                });
            }

            if (!string.IsNullOrWhiteSpace(Account.Address))
            {
                var aliases = await _accountService.GetAccountAliasesAsync(Account.Id);
                var hasRootAlias = aliases.Any(alias => alias.IsRootAlias);

                if (!hasRootAlias)
                {
                    await _accountService.CreateRootAliasAsync(Account.Id, Account.Address);
                }
            }

            await SynchronizationManager.Instance.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.Alias
            });
        }

        if (selectedOption.IsCalendarAccessGranted && !previousCalendarAccess)
        {
            await SynchronizationManager.Instance.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = CalendarSynchronizationType.CalendarMetadata
            });
        }

        var refreshedAccount = await _accountService.GetAccountAsync(Account.Id);

        await ExecuteUIThread(() =>
        {
            Account = refreshedAccount;
            AccountName = refreshedAccount.Name;
            SenderName = refreshedAccount.SenderName;
            EnsureSelectedTabForCapabilities();
        });
    }

    private void EnsureSelectedTabForCapabilities()
    {
        if (SelectedTabIndex == 1 && !HasMailAccess)
        {
            SelectedTabIndex = HasCalendarAccess ? 2 : 0;
        }
        else if (SelectedTabIndex == 2 && !HasCalendarAccess)
        {
            SelectedTabIndex = HasMailAccess ? 1 : 0;
        }
    }
}

public sealed class AccountCalendarShowAsOption
{
    public CalendarItemShowAs ShowAs { get; }
    public string DisplayText { get; }

    public AccountCalendarShowAsOption(CalendarItemShowAs showAs, string displayText)
    {
        ShowAs = showAs;
        DisplayText = displayText;
    }
}

public sealed class AccountCapabilityOption
{
    public bool IsMailAccessGranted { get; }
    public bool IsCalendarAccessGranted { get; }
    public string DisplayText { get; }

    public AccountCapabilityOption(bool isMailAccessGranted, bool isCalendarAccessGranted, string displayText)
    {
        IsMailAccessGranted = isMailAccessGranted;
        IsCalendarAccessGranted = isCalendarAccessGranted;
        DisplayText = displayText;
    }
}

public partial class AccountCalendarSettingsItemViewModel : ObservableObject
{
    public AccountCalendar Calendar { get; }
    public ObservableCollection<AccountCalendarShowAsOption> ShowAsOptions { get; }
    public List<AppColorViewModel> AvailableColors { get; }

    public string Name => Calendar.Name;
    public string TimeZone => Calendar.TimeZone;
    public string BackgroundColorHex => Calendar.BackgroundColorHex;

    [ObservableProperty]
    public partial bool IsSynchronizationEnabled { get; set; }

    [ObservableProperty]
    public partial AccountCalendarShowAsOption SelectedShowAsOption { get; set; }

    [ObservableProperty]
    public partial AppColorViewModel SelectedColor { get; set; }

    public AccountCalendarSettingsItemViewModel(AccountCalendar calendar, ObservableCollection<AccountCalendarShowAsOption> showAsOptions, List<AppColorViewModel> availableColors)
    {
        Calendar = calendar;
        ShowAsOptions = showAsOptions;
        AvailableColors = availableColors;
        IsSynchronizationEnabled = calendar.IsSynchronizationEnabled;
        SelectedShowAsOption = showAsOptions.FirstOrDefault(option => option.ShowAs == calendar.DefaultShowAs) ?? showAsOptions.FirstOrDefault();
        SelectedColor = availableColors.FirstOrDefault(color => string.Equals(color.Hex, calendar.BackgroundColorHex, StringComparison.OrdinalIgnoreCase))
            ?? new AppColorViewModel(calendar.BackgroundColorHex ?? ColorHelpers.GenerateFlatColorHex());
    }

    public void SetBackgroundColor(AppColorViewModel color)
    {
        SelectedColor = color;
        Calendar.BackgroundColorHex = color.Hex;
        OnPropertyChanged(nameof(BackgroundColorHex));
    }
}
