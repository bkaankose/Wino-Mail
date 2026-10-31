using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class AccountSetupProgressPageViewModel : MailBaseViewModel
{
    private readonly IAccountSetupRunner _accountSetupRunner;
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
    private int _currentStepIndex;

    public AccountSetupProgressPageViewModel(
        IAccountSetupRunner accountSetupRunner,
        IMailDialogService dialogService,
        WelcomeWizardContext wizardContext)
    {
        _accountSetupRunner = accountSetupRunner;
        _dialogService = dialogService;
        WizardContext = wizardContext;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
        => await InitializeAsync(mode, parameters);

    /// <summary>Awaitable entry for hosts that own account-setup navigation and shutdown.</summary>
    public Task InitializeAsync(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        // Only run on fresh navigation, not on back-navigation
        if (mode == NavigationMode.Back) return Task.CompletedTask;

        return RunSetupAsync();
    }

    private AccountSetupPlan BuildPlan()
    {
        var kind = WizardContext.IsOAuthProvider
            ? AccountSetupKind.Native
            : WizardContext.IsSpecialImapProviderWithAppPassword
                ? AccountSetupKind.SpecialImap
                : AccountSetupKind.Imap;

        return new AccountSetupPlan
        {
            Kind = kind,
            ProviderType = WizardContext.SelectedProvider.Type,
            SpecialImapProvider = WizardContext.SelectedProvider.SpecialImapProvider,
            ProviderName = WizardContext.SelectedProvider.Name,
            AccountName = WizardContext.AccountName,
            AccountColorHex = WizardContext.AccountColorHex,
            InitialSynchronizationRange = WizardContext.SelectedInitialSynchronizationRange,
            IsMailAccessEnabled = WizardContext.IsMailAccessEnabled,
            IsCalendarAccessEnabled = WizardContext.IsCalendarAccessEnabled,
            IsContactAccessEnabled = WizardContext.IsContactAccessEnabled,
            IsTaskAccessEnabled = WizardContext.IsTaskAccessEnabled,
            CalendarIntegrationSource = WizardContext.CalendarIntegrationSource,
            ContactIntegrationSource = WizardContext.ContactIntegrationSource,
            TaskIntegrationSource = WizardContext.TaskIntegrationSource,
            CalendarSupportMode = WizardContext.CalendarSupportMode,
            EmailAddress = WizardContext.EmailAddress,
            DisplayName = WizardContext.DisplayName,
            SpecialImap = kind == AccountSetupKind.SpecialImap ? WizardContext.BuildAccountCreationDialogResult() : null,
            Imap = WizardContext.ImapCalDavSetupResult?.ToAccountSetupData()
        };
    }

    private void BuildSteps(AccountSetupPlan plan)
    {
        Steps.Clear();
        _currentStepIndex = 0;

        foreach (var step in _accountSetupRunner.GetSteps(plan))
        {
            Steps.Add(new AccountSetupStepModel { Kind = step.Kind, Title = step.Title });
        }
    }

    /// <summary>
    /// A step the list does not show keeps the previous step current, so its success or failure lands on the step
    /// the user last saw running.
    /// </summary>
    private void ApplyStepUpdate(AccountSetupStepUpdate update)
    {
        if (update.Status == AccountSetupStepStatus.InProgress)
        {
            for (int i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].Kind == update.Kind)
                {
                    _currentStepIndex = i;
                    Steps[i].Status = AccountSetupStepStatus.InProgress;
                    return;
                }
            }

            return;
        }

        if (_currentStepIndex >= Steps.Count)
            return;

        Steps[_currentStepIndex].Status = update.Status;

        if (update.Status == AccountSetupStepStatus.Failed)
            Steps[_currentStepIndex].ErrorMessage = update.ErrorMessage;
    }

    private async Task RunSetupAsync()
    {
        IsSetupComplete = false;
        IsSetupFailed = false;
        FailureMessage = null;
        _dbWritten = false;
        _createdAccount = null;

        var plan = BuildPlan();
        BuildSteps(plan);

        var result = await _accountSetupRunner.RunAsync(
            plan,
            new StepUpdateHandler(ApplyStepUpdate),
            (message, certificateRawData) => _dialogService.ShowServerCertificateTrustDialogAsync(message, certificateRawData));

        _createdAccount = result.Account;
        _dbWritten = result.IsAccountSaved;

        if (result.IsCompleted)
        {
            IsSetupComplete = true;

            // Notify listeners — this triggers ShellWindow creation from App.xaml.cs
            Messenger.Send(new AccountCreatedMessage(_createdAccount));
            return;
        }

        if (result.IsCanceled)
        {
            // User canceled authentication — go back silently, no error UI
            Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
            return;
        }

        IsSetupFailed = true;
        FailureMessage = Translator.AccountSetup_FailureMessage;

        if (result.Error is ImapValidationException { ProtocolLog.Length: > 0 } validationException)
        {
            await _dialogService.ShowImapValidationFailedDialogAsync(
                validationException.Message,
                validationException.ProtocolLog);
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
    }

    [RelayCommand]
    private async Task TryAgainAsync()
    {
        if (_dbWritten && _createdAccount != null)
        {
            IsSetupFailed = false;
            FailureMessage = null;
            IsSetupComplete = true;

            Messenger.Send(new AccountCreatedMessage(_createdAccount));
            return;
        }

        await RunSetupAsync();
    }

    /// <summary>Applies step updates synchronously, on the thread the runner continues on (the caller's).</summary>
    private sealed class StepUpdateHandler(Action<AccountSetupStepUpdate> handler) : IProgress<AccountSetupStepUpdate>
    {
        public void Report(AccountSetupStepUpdate value) => handler(value);
    }
}
