using System;
using System.Collections.ObjectModel;
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
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class AccountSetupProgressPageViewModel : MailBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly ICalDavClient _calDavClient;
    private readonly IMailDialogService _dialogService;

    public WelcomeWizardContext WizardContext { get; }

    public ObservableCollection<AccountSetupStepModel> Steps { get; } = [];

    [ObservableProperty]
    public partial bool IsSetupComplete { get; set; }

    [ObservableProperty]
    public partial bool IsSetupFailed { get; set; }

    [ObservableProperty]
    public partial string FailureMessage { get; set; }

    private MailAccount _createdAccount;
    private bool _dbWritten;

    public AccountSetupProgressPageViewModel(
        IAccountService accountService,
        ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
        ICalDavClient calDavClient,
        IMailDialogService dialogService,
        WelcomeWizardContext wizardContext)
    {
        _accountService = accountService;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _calDavClient = calDavClient;
        _dialogService = dialogService;
        WizardContext = wizardContext;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        // Only run on fresh navigation, not on back-navigation
        if (mode == NavigationMode.Back) return;

        await RunSetupAsync();
    }

    private void BuildSteps()
    {
        Steps.Clear();
        var shouldSetupMail = WizardContext.IsMailAccessEnabled;
        var shouldSetupCalendar = WizardContext.IsCalendarAccessEnabled;

        if (WizardContext.IsOAuthProvider)
        {
            Steps.Add(new AccountSetupStepModel
            {
                Title = string.Format(Translator.AccountSetup_Step_Authenticating, WizardContext.SelectedProvider.Name)
            });
            Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SavingAccount });
            if (shouldSetupMail)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_FetchingProfile });
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SyncingFolders });

                if (WizardContext.SelectedProvider.Type == MailProviderType.Outlook)
                {
                    Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SyncingCategories });
                }

                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SyncingAliases });
            }

            if (shouldSetupCalendar)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_FetchingCalendarMetadata });
            }

            Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_Finalizing });
        }
        else if (WizardContext.IsSpecialImapProvider)
        {
            if (shouldSetupMail)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_TestingMailAuth });
            }

            if (shouldSetupCalendar && WizardContext.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_DiscoveringCalDav });
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_TestingCalendarAuth });
            }

            Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SavingAccount });
            if (shouldSetupMail)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SyncingFolders });
            }

            if (shouldSetupCalendar && WizardContext.CalendarSupportMode != ImapCalendarSupportMode.Disabled)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_FetchingCalendarMetadata });
            }

            Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_Finalizing });
        }
        else // Generic IMAP
        {
            Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SavingAccount });
            if (shouldSetupMail)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_SyncingFolders });
            }

            var setupResult = WizardContext.ImapCalDavSetupResult;
            if (setupResult?.IsCalendarAccessGranted == true &&
                setupResult.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_DiscoveringCalDav });
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_TestingCalendarAuth });
            }

            if (setupResult?.IsCalendarAccessGranted == true)
            {
                Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_FetchingCalendarMetadata });
            }

            Steps.Add(new AccountSetupStepModel { Title = Translator.AccountSetup_Step_Finalizing });
        }
    }

    private int _currentStepIndex;

    private void SetStepInProgress(string title)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Title == title)
            {
                _currentStepIndex = i;
                Steps[i].Status = AccountSetupStepStatus.InProgress;
                return;
            }
        }
    }

    private void SetCurrentStepSucceeded()
    {
        if (_currentStepIndex < Steps.Count)
            Steps[_currentStepIndex].Status = AccountSetupStepStatus.Succeeded;
    }

    private void SetCurrentStepFailed(string errorMessage)
    {
        if (_currentStepIndex < Steps.Count)
        {
            Steps[_currentStepIndex].Status = AccountSetupStepStatus.Failed;
            Steps[_currentStepIndex].ErrorMessage = errorMessage;
        }
    }

    private async Task RunSetupAsync()
    {
        IsSetupComplete = false;
        IsSetupFailed = false;
        FailureMessage = null;
        _dbWritten = false;
        _createdAccount = null;

        BuildSteps();

        try
        {
            CustomServerInformation customServerInformation = null;
            var accountCreatedAt = DateTime.UtcNow;

            // Build account in memory
            _createdAccount = new MailAccount
            {
                Id = Guid.NewGuid(),
                ProviderType = WizardContext.SelectedProvider.Type,
                Name = WizardContext.AccountName,
                SpecialImapProvider = WizardContext.SelectedProvider.SpecialImapProvider,
                AccountColorHex = WizardContext.AccountColorHex,
                CreatedAt = accountCreatedAt,
                InitialSynchronizationRange = WizardContext.SelectedInitialSynchronizationRange,
                IsMailAccessGranted = WizardContext.IsMailAccessEnabled,
                IsCalendarAccessGranted = WizardContext.IsCalendarAccessEnabled
            };

            if (WizardContext.IsOAuthProvider)
            {
                // Step: Authenticating
                SetStepInProgress(string.Format(Translator.AccountSetup_Step_Authenticating, WizardContext.SelectedProvider.Name));

                var authTokenInfo = await SynchronizationManager.Instance.HandleAuthorizationAsync(
                    WizardContext.SelectedProvider.Type,
                    _createdAccount,
                    _createdAccount.ProviderType == MailProviderType.Gmail);

                _createdAccount.Address = authTokenInfo.AccountAddress;
                SetCurrentStepSucceeded();

                // Step: Save to DB
                SetStepInProgress(Translator.AccountSetup_Step_SavingAccount);
                await _accountService.CreateAccountAsync(_createdAccount, null);
                _dbWritten = true;
                SetCurrentStepSucceeded();

                if (_createdAccount.IsMailAccessGranted)
                {
                    // Step: Profile
                    SetStepInProgress(Translator.AccountSetup_Step_FetchingProfile);
                    var profileResult = await SynchronizationManager.Instance.SynchronizeProfileAsync(_createdAccount.Id);
                    if (profileResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeProfileInformation);

                    if (profileResult.ProfileInformation != null)
                    {
                        _createdAccount.SenderName = profileResult.ProfileInformation.SenderName;
                        _createdAccount.Base64ProfilePictureData = profileResult.ProfileInformation.Base64ProfilePictureData;

                        if (!string.IsNullOrEmpty(profileResult.ProfileInformation.AccountAddress))
                        {
                            if (await _accountService.AccountAddressExistsAsync(profileResult.ProfileInformation.AccountAddress, _createdAccount.Id))
                                throw new InvalidOperationException(Translator.DialogMessage_AccountAddressExistsMessage);

                            _createdAccount.Address = profileResult.ProfileInformation.AccountAddress;
                        }

                        await _accountService.UpdateProfileInformationAsync(_createdAccount.Id, profileResult.ProfileInformation);
                    }
                    SetCurrentStepSucceeded();

                    // Step: Folders
                    SetStepInProgress(Translator.AccountSetup_Step_SyncingFolders);
                    var folderResult = await SynchronizationManager.Instance.SynchronizeFoldersAsync(_createdAccount.Id);
                    if (folderResult == null || folderResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeFolders);
                    SetCurrentStepSucceeded();

                    // Step: Categories
                    if (_createdAccount.IsCategorySyncSupported)
                    {
                        SetStepInProgress(Translator.AccountSetup_Step_SyncingCategories);
                        var categoryResult = await SynchronizationManager.Instance.SynchronizeCategoriesAsync(_createdAccount.Id);
                        if (categoryResult.CompletedState != SynchronizationCompletedState.Success)
                            throw new Exception(Translator.Exception_FailedToSynchronizeCategories);
                        SetCurrentStepSucceeded();
                    }
                }

                // Step: Calendar metadata
                if (_createdAccount.IsCalendarAccessGranted)
                {
                    SetStepInProgress(Translator.AccountSetup_Step_FetchingCalendarMetadata);
                    var calResult = await SynchronizationManager.Instance.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
                    {
                        AccountId = _createdAccount.Id,
                        Type = CalendarSynchronizationType.CalendarMetadata
                    });
                    if (calResult == null || calResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeCalendarMetadata);
                    SetCurrentStepSucceeded();
                }

                // Step: Aliases
                if (_createdAccount.IsMailAccessGranted)
                {
                    SetStepInProgress(Translator.AccountSetup_Step_SyncingAliases);
                    if (_createdAccount.IsAliasSyncSupported)
                    {
                        var aliasResult = await SynchronizationManager.Instance.SynchronizeAliasesAsync(_createdAccount.Id);
                        if (aliasResult.CompletedState != SynchronizationCompletedState.Success)
                            throw new Exception(Translator.Exception_FailedToSynchronizeAliases);
                    }
                    else
                    {
                        await _accountService.CreateRootAliasAsync(_createdAccount.Id, _createdAccount.Address);
                    }
                    SetCurrentStepSucceeded();
                }
            }
            else if (WizardContext.IsSpecialImapProvider)
            {
                var dialogResult = WizardContext.BuildAccountCreationDialogResult();

                customServerInformation = _specialImapProviderConfigResolver.GetServerInformation(_createdAccount, dialogResult);
                if (customServerInformation == null) throw new Exception("Failed to resolve server information.");

                customServerInformation.Id = Guid.NewGuid();
                customServerInformation.AccountId = _createdAccount.Id;

                _createdAccount.Address = WizardContext.EmailAddress;
                _createdAccount.SenderName = WizardContext.DisplayName;
                _createdAccount.IsMailAccessGranted = dialogResult.IsMailAccessGranted;
                _createdAccount.IsCalendarAccessGranted = customServerInformation.CalendarSupportMode != ImapCalendarSupportMode.Disabled;
                _createdAccount.ServerInformation = customServerInformation;

                if (_createdAccount.IsMailAccessGranted)
                {
                    // Step: Test IMAP
                    SetStepInProgress(Translator.AccountSetup_Step_TestingMailAuth);
                    await ValidateImapConnectivityAsync(customServerInformation);
                    SetCurrentStepSucceeded();
                }

                // Step: CalDAV discovery and testing (if applicable)
                if (customServerInformation.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
                {
                    SetStepInProgress(Translator.AccountSetup_Step_DiscoveringCalDav);
                    SetCurrentStepSucceeded();

                    SetStepInProgress(Translator.AccountSetup_Step_TestingCalendarAuth);
                    await ValidateCalDavConnectivityAsync(customServerInformation);
                    SetCurrentStepSucceeded();
                }

                // Step: Save to DB
                SetStepInProgress(Translator.AccountSetup_Step_SavingAccount);
                await _accountService.CreateAccountAsync(_createdAccount, customServerInformation);
                _dbWritten = true;
                SetCurrentStepSucceeded();

                if (_createdAccount.IsMailAccessGranted)
                {
                    // Step: Folders
                    SetStepInProgress(Translator.AccountSetup_Step_SyncingFolders);
                    var folderResult = await SynchronizationManager.Instance.SynchronizeFoldersAsync(_createdAccount.Id);
                    if (folderResult == null || folderResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeFolders);
                    SetCurrentStepSucceeded();
                }

                // Step: Calendar metadata (if not disabled)
                if (_createdAccount.IsCalendarAccessGranted)
                {
                    SetStepInProgress(Translator.AccountSetup_Step_FetchingCalendarMetadata);
                    var calResult = await SynchronizationManager.Instance.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
                    {
                        AccountId = _createdAccount.Id,
                        Type = CalendarSynchronizationType.CalendarMetadata
                    });
                    if (calResult == null || calResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeCalendarMetadata);
                    SetCurrentStepSucceeded();
                }

                if (_createdAccount.IsMailAccessGranted)
                {
                    await _accountService.CreateRootAliasAsync(_createdAccount.Id, _createdAccount.Address);
                }
            }
            else // Generic IMAP
            {
                var setupResult = WizardContext.ImapCalDavSetupResult
                    ?? throw new Exception("IMAP setup was not completed.");

                customServerInformation = setupResult.ServerInformation
                    ?? throw new Exception("Server information is missing.");

                customServerInformation.Id = Guid.NewGuid();
                customServerInformation.AccountId = _createdAccount.Id;

                _createdAccount.Address = setupResult.EmailAddress;
                _createdAccount.SenderName = setupResult.DisplayName;
                _createdAccount.IsMailAccessGranted = setupResult.IsMailAccessGranted;
                _createdAccount.IsCalendarAccessGranted = setupResult.IsCalendarAccessGranted;
                _createdAccount.ServerInformation = customServerInformation;

                // Step: Save to DB
                SetStepInProgress(Translator.AccountSetup_Step_SavingAccount);
                await _accountService.CreateAccountAsync(_createdAccount, customServerInformation);
                _dbWritten = true;
                SetCurrentStepSucceeded();

                if (_createdAccount.IsMailAccessGranted)
                {
                    // Step: Folders
                    SetStepInProgress(Translator.AccountSetup_Step_SyncingFolders);
                    var folderResult = await SynchronizationManager.Instance.SynchronizeFoldersAsync(_createdAccount.Id);
                    if (folderResult == null || folderResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeFolders);
                    SetCurrentStepSucceeded();
                }

                // Step: CalDAV (if applicable)
                if (setupResult.IsCalendarAccessGranted &&
                    customServerInformation.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
                {
                    SetStepInProgress(Translator.AccountSetup_Step_DiscoveringCalDav);
                    SetCurrentStepSucceeded();

                    SetStepInProgress(Translator.AccountSetup_Step_TestingCalendarAuth);
                    await ValidateCalDavConnectivityAsync(customServerInformation);
                    SetCurrentStepSucceeded();
                }

                // Step: Calendar metadata
                if (setupResult.IsCalendarAccessGranted)
                {
                    SetStepInProgress(Translator.AccountSetup_Step_FetchingCalendarMetadata);
                    var calResult = await SynchronizationManager.Instance.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
                    {
                        AccountId = _createdAccount.Id,
                        Type = CalendarSynchronizationType.CalendarMetadata
                    });
                    if (calResult == null || calResult.CompletedState != SynchronizationCompletedState.Success)
                        throw new Exception(Translator.Exception_FailedToSynchronizeCalendarMetadata);
                    SetCurrentStepSucceeded();
                }

                if (_createdAccount.IsMailAccessGranted)
                {
                    await _accountService.CreateRootAliasAsync(_createdAccount.Id, _createdAccount.Address);
                }
            }

            // Step: Finalizing
            SetStepInProgress(Translator.AccountSetup_Step_Finalizing);
            SetCurrentStepSucceeded();

            IsSetupComplete = true;

            // Notify listeners — this triggers ShellWindow creation from App.xaml.cs
            Messenger.Send(new AccountCreatedMessage(_createdAccount));
        }
        catch (AccountSetupCanceledException)
        {
            // User canceled authentication — go back silently, no error UI
            Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
        }
        catch (Exception ex) when (ex.Message.Contains(nameof(AccountSetupCanceledException)))
        {
            // Wrapped cancellation — same silent behavior
            Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Account setup failed.");

            SetCurrentStepFailed(ex.Message);
            IsSetupFailed = true;
            FailureMessage = Translator.AccountSetup_FailureMessage;

            // Rollback if DB write happened
            if (_dbWritten && _createdAccount != null)
            {
                try
                {
                    await _accountService.DeleteAccountAsync(_createdAccount);
                }
                catch (Exception deleteEx)
                {
                    Log.Error(deleteEx, "Failed to rollback account creation.");
                }

                _dbWritten = false;
            }
        }
    }

    private async Task ValidateImapConnectivityAsync(CustomServerInformation serverInformation)
    {
        var connectivityResult = await SynchronizationManager.Instance
            .TestImapConnectivityAsync(serverInformation, allowSSLHandshake: false);

        if (connectivityResult.IsCertificateUIRequired)
        {
            var certificateMessage =
                $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateIssuer}: {connectivityResult.CertificateIssuer}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidFrom}: {connectivityResult.CertificateValidFromDateString}\n" +
                $"{Translator.IMAPSetupDialog_CertificateValidTo}: {connectivityResult.CertificateExpirationDateString}\n\n" +
                $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1}";

            var allowCertificate = await _dialogService.ShowConfirmationDialogAsync(
                certificateMessage,
                Translator.GeneralTitle_Warning,
                Translator.Buttons_Allow);

            if (!allowCertificate)
                throw new InvalidOperationException(Translator.IMAPSetupDialog_CertificateDenied);

            connectivityResult = await SynchronizationManager.Instance
                .TestImapConnectivityAsync(serverInformation, allowSSLHandshake: true);
        }

        if (!connectivityResult.IsSuccess)
            throw new InvalidOperationException(connectivityResult.FailedReason ?? Translator.IMAPSetupDialog_ConnectionFailedMessage);
    }

    private async Task ValidateCalDavConnectivityAsync(CustomServerInformation serverInformation)
    {
        if (string.IsNullOrWhiteSpace(serverInformation.CalDavServiceUrl))
            throw new InvalidOperationException(Translator.ImapCalDavSettingsPage_CalDavUrlRequired);

        var settings = new CalDavConnectionSettings
        {
            ServiceUri = new Uri(serverInformation.CalDavServiceUrl, UriKind.Absolute),
            Username = serverInformation.CalDavUsername,
            Password = serverInformation.CalDavPassword
        };

        await _calDavClient.DiscoverCalendarsAsync(settings);
    }

    [RelayCommand]
    private void GoBack()
    {
        Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
    }

    [RelayCommand]
    private async Task TryAgainAsync()
    {
        await RunSetupAsync();
    }
}
