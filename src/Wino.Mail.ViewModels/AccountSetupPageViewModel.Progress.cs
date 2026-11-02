#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>Add account and the progress list, plus the paths out to Advanced setup.</summary>
public partial class AccountSetupPageViewModel
{
    private MailAccount? _createdAccount;
    private bool _isCreatedAccountSaved;
    private int _currentStepIndex;
    private string? _setupProtocolLog;

    public ObservableCollection<AccountSetupStepModel> Steps { get; } = [];

    [ObservableProperty]
    public partial string SenderName { get; set; } = string.Empty;

    /// <summary>The sender name sits on the IMAP sign-in step, under the password or the browser button.</summary>
    public bool IsSendAsVisible => !IsLocalOnlyMode &&
                                   (Route == AccountRoute.Imap || (Route == AccountRoute.Exchange && !IsExchangeModernAuth)) &&
                                   Capabilities.IsMailEnabled;
    public bool IsAddedVisible => Phase == AccountSetupPhase.Added;
    public bool IsSetupFailedVisible => Phase == AccountSetupPhase.Failed;
    public bool HasSetupFailureDetails => IsSetupFailedVisible && !string.IsNullOrWhiteSpace(_setupProtocolLog);

    public string StepsTitle => Phase == AccountSetupPhase.Added ? Translator.AccountSetup_AddedTitle : Translator.AccountSetup_AddingTitle;

    public string AddedMessage => IsLocalOnlyMode || !(_createdAccount?.IsMailAccessGranted ?? false)
        ? string.Format(Translator.AccountSetup_AddedMessageLocal, _createdAccount?.Name ?? AccountName)
        : string.Format(Translator.AccountSetup_AddedMessage, _createdAccount?.Address ?? EffectiveAddress);

    private string EffectiveAddress => !string.IsNullOrWhiteSpace(_confirmedAddress)
        ? _confirmedAddress
        : EmailAddress?.Trim() ?? string.Empty;

    private string EffectiveSenderName => SenderName?.Trim() ?? string.Empty;

    private bool IsAddedFromFeatures => Phase == AccountSetupPhase.Features &&
                                        FeatureStep == AccountSetupStep.Tasks &&
                                        IsAddedFromTasksStep;

    /// <summary>
    /// Everything is validated: a local account has at least one of Calendar, Contacts and To Do on, every other
    /// account is signed in and its checks passed.
    /// </summary>
    private bool CanAddAccount() => IsAddedFromFeatures
        ? Capabilities.HasAnyCapability
        : Phase == AccountSetupPhase.SignIn && IsSignInValidated;

    [RelayCommand(CanExecute = nameof(CanAddAccount))]
    private async Task AddAccountAsync()
    {
        if (!CanAddAccount())
            return;

        if (IsAddedFromFeatures && !ValidateFeatureSelection())
            return;

        // The name was typed on the address step or derived after discovery; a local account cannot get here without one.
        if (string.IsNullOrWhiteSpace(AccountName))
        {
            if (IsLocalOnlyMode)
                return;

            AccountName = EffectiveAddress;
        }

        await RunAddAccountAsync();
    }

    /// <summary>Back to where Add account was pressed: the To Do step for a local account, otherwise the validated sign-in.</summary>
    private void ReturnToAddAccount()
    {
        if (IsLocalOnlyMode || !Capabilities.RequiresRemoteService)
        {
            FeatureStep = AccountSetupStep.Tasks;
            Phase = AccountSetupPhase.Features;
            return;
        }

        Phase = AccountSetupPhase.SignIn;
    }

    private async Task RunAddAccountAsync()
    {
        AccountSetupPlan plan;

        try
        {
            plan = BuildFinalPlan();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Building the account setup plan failed.");

            if (IsAddedFromFeatures)
                FeaturesError = ex.Message;
            else
                ShowFailure(Translator.AccountSetup_Failure_Title, ex.Message, InfoBarMessageType.Error, AccountSetupRetryAction.None);

            return;
        }

        CancelOperation();
        HideFailure();

        Steps.Clear();
        _currentStepIndex = 0;
        _setupProtocolLog = null;

        foreach (var step in _setupRunner.GetSteps(plan))
            Steps.Add(new AccountSetupStepModel { Kind = step.Kind, Title = step.Title });

        Phase = AccountSetupPhase.Adding;

        var result = await _setupRunner.RunAsync(
            plan,
            new StepUpdateHandler(ApplyStepUpdate),
            (message, certificateRawData) => _dialogService.ShowServerCertificateTrustDialogAsync(message, certificateRawData));

        _createdAccount = result.Account;
        _isCreatedAccountSaved = result.IsAccountSaved;

        // A saved account owns the sign-in now; leaving the page must not discard it.
        if (result.IsAccountSaved)
            ForgetHandedOffCredentials();

        if (result.IsCompleted && result.Account != null)
        {
            Phase = AccountSetupPhase.Added;
            OnPropertyChanged(nameof(AddedMessage));
            Messenger.Send(new AccountCreatedMessage(result.Account));
            return;
        }

        if (result.IsCanceled && !result.IsAccountSaved)
        {
            ReturnToAddAccount();
            return;
        }

        _setupProtocolLog = (result.Error as ImapValidationException)?.ProtocolLog;
        Phase = AccountSetupPhase.Failed;
    }

    [RelayCommand]
    private async Task TryAgainAsync()
    {
        if (_isCreatedAccountSaved && _createdAccount != null)
        {
            Phase = AccountSetupPhase.Added;
            OnPropertyChanged(nameof(AddedMessage));
            Messenger.Send(new AccountCreatedMessage(_createdAccount));
            return;
        }

        await RunAddAccountAsync();
    }

    [RelayCommand]
    private void GoBackFromFailure()
    {
        if (_isCreatedAccountSaved && _createdAccount != null)
        {
            Messenger.Send(new AccountCreatedMessage(_createdAccount));
            Phase = AccountSetupPhase.Added;
            return;
        }

        ReturnToAddAccount();
    }

    [RelayCommand]
    private async Task ShowSetupFailureDetailsAsync()
    {
        var protocolLog = _setupProtocolLog;
        if (string.IsNullOrWhiteSpace(protocolLog))
            return;

        await _dialogService.ShowImapValidationFailedDialogAsync(Translator.AccountSetup_FailureMessage, protocolLog);
    }

    private AccountSetupPlan BuildFinalPlan()
    {
        var accountName = AccountName?.Trim() ?? string.Empty;
        var colorHex = SelectedColor?.Hex ?? string.Empty;
        var range = SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths;

        if (IsLocalOnlyMode || !Capabilities.RequiresRemoteService)
        {
            var calendarMode = Capabilities.CalendarMode == AccountCapabilityMode.Local
                ? ImapCalendarSupportMode.LocalOnly
                : ImapCalendarSupportMode.Disabled;

            return new AccountSetupPlan
            {
                Kind = AccountSetupKind.Imap,
                ProviderType = MailProviderType.IMAP4,
                SpecialImapProvider = SpecialImapProvider.None,
                AccountName = accountName,
                AccountColorHex = colorHex,
                InitialSynchronizationRange = range,
                IsMailAccessEnabled = false,
                IsCalendarAccessEnabled = Capabilities.IsCalendarEnabled,
                IsContactAccessEnabled = Capabilities.IsContactEnabled,
                IsTaskAccessEnabled = Capabilities.IsTaskEnabled,
                CalendarIntegrationSource = AccountIntegrationSource.Local,
                ContactIntegrationSource = AccountIntegrationSource.Local,
                TaskIntegrationSource = AccountIntegrationSource.Local,
                CalendarSupportMode = calendarMode,
                Imap = ImapAccountSetupData.CreateLocalOnly(accountName, calendarMode)
            };
        }

        if (IsNativeRoute)
        {
            var transientAccount = _transientAccount ?? throw new InvalidOperationException(Translator.AccountSetup_Failure_SignInTitle);
            var token = _nativeToken ?? throw new InvalidOperationException(Translator.AccountSetup_Failure_SignInTitle);
            var confirmedAddress = _confirmedAddress ?? throw new InvalidOperationException(Translator.AccountSetup_Failure_SignInTitle);

            return BuildNativePlan(new NativeSignInResult(transientAccount, token with { AccountAddress = confirmedAddress }));
        }

        if (IsExchangeRoute)
            return BuildExchangePlan(accountName, colorHex, range);

        var server = _pendingServer ?? throw new InvalidOperationException(Translator.AccountSetup_Failure_Title);
        server.DisplayName = EffectiveSenderName;

        return new AccountSetupPlan
        {
            Kind = AccountSetupKind.Imap,
            ProviderType = MailProviderType.IMAP4,
            SpecialImapProvider = RouteSpecialImapProvider,
            ProviderName = ProviderLabel,
            AccountName = accountName,
            AccountColorHex = colorHex,
            InitialSynchronizationRange = range,
            IsMailAccessEnabled = Capabilities.IsMailEnabled,
            IsCalendarAccessEnabled = Capabilities.IsCalendarEnabled,
            IsContactAccessEnabled = Capabilities.IsContactEnabled,
            IsTaskAccessEnabled = Capabilities.IsTaskEnabled,
            CalendarIntegrationSource = ToIntegrationSource(Capabilities.CalendarMode),
            ContactIntegrationSource = ToIntegrationSource(Capabilities.ContactMode),
            TaskIntegrationSource = AccountIntegrationSource.Local,
            CalendarSupportMode = server.CalendarSupportMode,
            EmailAddress = EmailAddress?.Trim(),
            DisplayName = EffectiveSenderName,
            Imap = new ImapAccountSetupData
            {
                DisplayName = EffectiveSenderName,
                EmailAddress = EmailAddress?.Trim(),
                IsMailAccessGranted = Capabilities.IsMailEnabled,
                IsCalendarAccessGranted = server.CalendarSupportMode != ImapCalendarSupportMode.Disabled,
                ShouldAppendMessagesToSentFolder = true,
                ServerInformation = server,
                OAuthSession = server.IncomingAuthenticationMethod == ImapAuthenticationMethod.OAuth2 ? _oauthSession : null
            }
        };
    }

    /// <summary>
    /// A step the list does not show keeps the previous step current, so its success or failure lands on the step the
    /// user last saw running (the progress page's rule).
    /// </summary>
    private void ApplyStepUpdate(AccountSetupStepUpdate update)
    {
        if (update.Status == AccountSetupStepStatus.InProgress)
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].Kind != update.Kind)
                    continue;

                _currentStepIndex = i;
                Steps[i].Status = AccountSetupStepStatus.InProgress;
                return;
            }

            return;
        }

        if (_currentStepIndex >= Steps.Count)
            return;

        Steps[_currentStepIndex].Status = update.Status;

        if (update.Status == AccountSetupStepStatus.Failed)
            Steps[_currentStepIndex].ErrorMessage = update.ErrorMessage ?? string.Empty;
    }

    private void ForgetHandedOffCredentials()
    {
        _transientAccount = null;
        _nativeToken = null;
        _oauthSession = null;
        _grantedScopes = Array.Empty<string>();
        _exchangeTransientAccount = null;
    }

    #region Advanced setup

    /// <summary>
    /// Opens <c>ImapCalDavSettingsPage</c> in create mode with what this page found: the servers, the typed credential and
    /// the OAuth session, which Advanced setup owns from here. It saves through the wizard context and the progress page.
    /// </summary>
    [RelayCommand]
    private async Task OpenAdvancedSetupAsync()
    {
        CancelOperation();

        if (Route == AccountRoute.Exchange || (Phase == AccountSetupPhase.NotFound && IsExchangeRediscovery))
        {
            await OpenExchangeAdvancedSetupAsync();
            return;
        }

        var address = IsLocalOnlyMode ? string.Empty : EmailAddress?.Trim() ?? string.Empty;
        var isAddressValid = MailAccountAddressValidator.IsValid(address);
        var hasFeatures = _resolution != null && Phase is not (AccountSetupPhase.Identity or AccountSetupPhase.Resolving or AccountSetupPhase.NotFound);
        var isImapResolution = Route == AccountRoute.Imap && _resolution?.Imap?.IsFound == true;

        var isMailEnabled = !hasFeatures || Capabilities.IsMailEnabled;
        var calendarMode = hasFeatures ? Capabilities.CalendarMode : AccountCapabilityMode.Provider;
        var contactMode = hasFeatures ? Capabilities.ContactMode : AccountCapabilityMode.Local;
        var taskMode = hasFeatures ? Capabilities.TaskMode : AccountCapabilityMode.Local;

        if (taskMode == AccountCapabilityMode.Provider)
            taskMode = AccountCapabilityMode.Local;

        var useOAuth = isImapResolution && _oauthSession != null && OAuthState == AccountSetupOAuthState.SignedIn;
        CustomServerInformation? server = null;

        if (isImapResolution)
        {
            try
            {
                server = _pendingServer ?? BuildImapServerInformation(useOAuth);
            }
            catch (Exception ex)
            {
                Log.Information("Advanced setup opens without the found servers ({ErrorType}).", ex.GetType().Name);
            }
        }

        var specialProvider = isImapResolution ? RouteSpecialImapProvider : SpecialImapProvider.None;
        var provider = FindProviderDetail(specialProvider);
        if (provider == null)
        {
            Log.Warning("Advanced setup has no IMAP provider entry to open with.");
            return;
        }

        var accountName = GetAdvancedAccountName(address);
        var calendarSupportMode = calendarMode switch
        {
            AccountCapabilityMode.Provider => ImapCalendarSupportMode.CalDav,
            AccountCapabilityMode.Local => ImapCalendarSupportMode.LocalOnly,
            _ => ImapCalendarSupportMode.Disabled
        };

        WizardContext.Reset();
        WizardContext.SelectedProvider = provider;
        WizardContext.AccountName = accountName;
        WizardContext.AccountColorHex = SelectedColor?.Hex ?? string.Empty;
        WizardContext.SelectedInitialSynchronizationRange = SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths;
        WizardContext.IsMailAccessEnabled = isMailEnabled;
        WizardContext.IsCalendarAccessEnabled = calendarMode != AccountCapabilityMode.Off;
        WizardContext.IsContactAccessEnabled = contactMode != AccountCapabilityMode.Off;
        WizardContext.IsTaskAccessEnabled = taskMode != AccountCapabilityMode.Off;
        WizardContext.CalendarIntegrationSource = calendarMode == AccountCapabilityMode.Provider ? AccountIntegrationSource.Dav : AccountIntegrationSource.Local;
        WizardContext.ContactIntegrationSource = contactMode == AccountCapabilityMode.Provider ? AccountIntegrationSource.Dav : AccountIntegrationSource.Local;
        WizardContext.TaskIntegrationSource = AccountIntegrationSource.Local;
        WizardContext.CalendarSupportMode = calendarSupportMode;
        WizardContext.EmailAddress = address;
        WizardContext.DisplayName = EffectiveSenderName;
        WizardContext.AppSpecificPassword = useOAuth ? string.Empty : Password;
        WizardContext.RegionId = SelectedRegion?.Id ?? string.Empty;
        WizardContext.LoginAddress = IsLoginAddressVisible ? LoginAddress?.Trim() ?? string.Empty : string.Empty;

        var prefill = new ImapSetupPrefill
        {
            EmailAddress = address,
            SenderName = EffectiveSenderName,
            Password = useOAuth ? string.Empty : Password ?? string.Empty,
            SpecialImapProvider = specialProvider,
            LoginAddress = string.IsNullOrWhiteSpace(WizardContext.LoginAddress) ? null : WizardContext.LoginAddress,
            ServerInformation = server,
            CalDavServiceUrl = calendarMode == AccountCapabilityMode.Provider ? CalDavServiceUri?.ToString() : null,
            CardDavServiceUrl = contactMode == AccountCapabilityMode.Provider ? CardDavServiceUri?.ToString() : null,
            OAuthSession = useOAuth ? _oauthSession : null,
            StartOnServers = isAddressValid && (isImapResolution || Phase is AccountSetupPhase.NotFound or AccountSetupPhase.Resolving)
        };

        // Advanced setup owns the session now and discards it if it is not saved; the rest of this attempt goes.
        if (prefill.OAuthSession != null)
            _oauthSession = null;

        await DiscardSignInAsync();
        ClearRoute();
        Phase = AccountSetupPhase.Identity;

        var creationResult = WizardContext.BuildAccountCreationDialogResult();
        var context = _hostMode == ProviderSelectionHostMode.SettingsAddAccount
            ? ImapCalDavSettingsNavigationContext.CreateForAddAccountMode(creationResult, prefill)
            : ImapCalDavSettingsNavigationContext.CreateForWizardMode(creationResult, prefill);

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.ImapCalDavSettingsPage_TitleCreate,
            WinoPage.ImapCalDavSettingsPage,
            context));
    }

    private IProviderDetail? FindProviderDetail(SpecialImapProvider specialProvider)
    {
        var providers = _providerCatalog.GetAvailableProviders();

        return providers.FirstOrDefault(provider => provider.Type == MailProviderType.IMAP4 && provider.SpecialImapProvider == specialProvider && specialProvider != SpecialImapProvider.None)
               ?? providers.FirstOrDefault(provider => provider.Type == MailProviderType.IMAP4 && provider.SpecialImapProvider == SpecialImapProvider.None);
    }

    private string GetAdvancedAccountName(string address)
    {
        var name = AccountName?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (MailAccountAddressValidator.TryGetDomain(address, out var domain) && !string.IsNullOrWhiteSpace(domain))
            return domain;

        return Translator.ProviderSelection_ModeImap;
    }

    #endregion

    private void NotifyProgressLayoutChanged()
    {
        OnPropertyChanged(nameof(IsSendAsVisible));
        OnPropertyChanged(nameof(IsAddedVisible));
        OnPropertyChanged(nameof(IsSetupFailedVisible));
        OnPropertyChanged(nameof(HasSetupFailureDetails));
        OnPropertyChanged(nameof(StepsTitle));
    }

    /// <summary>Applies step updates on the thread the runner continues on, which is the caller's.</summary>
    private sealed class StepUpdateHandler(Action<AccountSetupStepUpdate> handler) : IProgress<AccountSetupStepUpdate>
    {
        public void Report(AccountSetupStepUpdate value) => handler(value);
    }
}
