using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// The blocked state a mode page shows while the mode cannot be used: no account, the mode
/// turned off, a sign-in pending, or the first sync still running. The owning page view model
/// activates it while the page is on screen and gates its create commands on <see cref="IsReady"/>.
/// </summary>
public partial class ModeReadinessViewModel : ObservableObject
{
    private readonly WinoApplicationMode _mode;
    private readonly IAppModeReadinessService _readinessService;
    private readonly INavigationService _navigationService;
    private readonly IMailShellClient _mailShell;
    private readonly Func<Action, Task> _executeOnUIThread;
    private int _isRefreshing;
    private int _isRefreshPending;
    private bool _isActive;

    /// <summary>Raised on the UI thread after <see cref="IsReady"/> changes.</summary>
    public event EventHandler ReadinessChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryActionVisible))]
    [NotifyPropertyChangedFor(nameof(IsSecondaryActionVisible))]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    public partial AppModeReadiness Readiness { get; set; }

    /// <summary>
    /// True until an evaluation says otherwise, so a page never flashes a blocked state
    /// while the first check runs. Commands still re-check with <see cref="EnsureReadyAsync"/>.
    /// </summary>
    public bool IsReady => Readiness?.IsReady ?? true;
    public bool IsBlocked => !IsReady;
    public AppModeReadinessState State => Readiness?.State ?? AppModeReadinessState.Ready;

    public string Title => State switch
    {
        AppModeReadinessState.NoAccounts => Translator.AppModeReadiness_NoAccountsTitle,
        AppModeReadinessState.FeatureDisabled => GetText(
            Translator.MailEmptyState_Title,
            Translator.AppModeReadiness_Calendar_DisabledTitle,
            Translator.AppModeReadiness_Contacts_DisabledTitle,
            Translator.AppModeReadiness_ToDo_DisabledTitle),
        AppModeReadinessState.SignInRequired => string.Format(Translator.MailAccountIdle_SignInTitle, AttentionAccountName),
        AppModeReadinessState.WaitingForSynchronization => GetText(
            Translator.AppModeReadiness_Mail_WaitingTitle,
            Translator.AppModeReadiness_Calendar_WaitingTitle,
            Translator.AppModeReadiness_Contacts_WaitingTitle,
            Translator.AppModeReadiness_ToDo_WaitingTitle),
        _ => string.Empty
    };

    public string Message => State switch
    {
        AppModeReadinessState.NoAccounts => GetText(
            Translator.DialogMessage_NoAccountsForCreateMailMessage,
            Translator.AppModeReadiness_Calendar_NoAccountsMessage,
            Translator.AppModeReadiness_Contacts_NoAccountsMessage,
            Translator.AppModeReadiness_ToDo_NoAccountsMessage),
        AppModeReadinessState.FeatureDisabled => GetText(
            Translator.MailEmptyState_Message,
            Translator.AppModeReadiness_Calendar_DisabledMessage,
            Translator.AppModeReadiness_Contacts_DisabledMessage,
            Translator.AppModeReadiness_ToDo_DisabledMessage),
        AppModeReadinessState.SignInRequired => GetText(
            Translator.AppModeReadiness_Mail_SignInMessage,
            Translator.AppModeReadiness_Calendar_SignInMessage,
            Translator.AppModeReadiness_Contacts_SignInMessage,
            Translator.AppModeReadiness_ToDo_SignInMessage),
        AppModeReadinessState.WaitingForSynchronization => GetText(
            Translator.AppModeReadiness_Mail_WaitingMessage,
            Translator.AppModeReadiness_Calendar_WaitingMessage,
            Translator.AppModeReadiness_Contacts_WaitingMessage,
            Translator.AppModeReadiness_ToDo_WaitingMessage),
        _ => string.Empty
    };

    public string PrimaryActionText => State switch
    {
        AppModeReadinessState.NoAccounts => Translator.MailEmptyState_AddAccount,
        AppModeReadinessState.FeatureDisabled => Translator.MailEmptyState_ManageAccounts,
        AppModeReadinessState.SignInRequired => Translator.MailAccountIdle_SignIn,
        _ => string.Empty
    };

    public string SecondaryActionText => Translator.MailEmptyState_ManageAccounts;

    public bool IsPrimaryActionVisible => State is AppModeReadinessState.NoAccounts
        or AppModeReadinessState.FeatureDisabled
        or AppModeReadinessState.SignInRequired;

    /// <summary>Manage accounts sits beside Sign in and alone under the waiting state.</summary>
    public bool IsSecondaryActionVisible => State is AppModeReadinessState.SignInRequired
        or AppModeReadinessState.WaitingForSynchronization;

    public bool IsProgressVisible => State == AppModeReadinessState.WaitingForSynchronization;

    private string AttentionAccountName
    {
        get
        {
            var account = Readiness?.AttentionAccount;
            if (account == null)
                return string.Empty;

            return string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name;
        }
    }

    public ModeReadinessViewModel(
        WinoApplicationMode mode,
        IAppModeReadinessService readinessService,
        INavigationService navigationService,
        IMailShellClient mailShell,
        Func<Action, Task> executeOnUIThread)
    {
        _mode = mode;
        _readinessService = readinessService;
        _navigationService = navigationService;
        _mailShell = mailShell;
        _executeOnUIThread = executeOnUIThread ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    /// <summary>Starts following account and sync changes and evaluates the mode now.</summary>
    public Task ActivateAsync()
    {
        if (_readinessService == null)
            return Task.CompletedTask;

        if (!_isActive)
        {
            _isActive = true;
            _readinessService.ReadinessInvalidated += ReadinessInvalidated;
        }

        return RefreshAsync();
    }

    public void Deactivate()
    {
        if (!_isActive)
            return;

        _isActive = false;
        _readinessService.ReadinessInvalidated -= ReadinessInvalidated;
    }

    /// <summary>
    /// Re-evaluates before a create action runs, so a command invoked from a shortcut or the
    /// pane between two notifications cannot slip through.
    /// </summary>
    public async Task<bool> EnsureReadyAsync()
    {
        if (_readinessService == null)
            return true;

        var readiness = await _readinessService.GetReadinessAsync(_mode).ConfigureAwait(false);
        await _executeOnUIThread(() => ApplyReadiness(readiness)).ConfigureAwait(false);

        return readiness.IsReady;
    }

    public async Task RefreshAsync()
    {
        if (_readinessService == null)
            return;

        // Coalesce bursts of sync messages into one evaluation plus at most one follow-up.
        Volatile.Write(ref _isRefreshPending, 1);

        while (true)
        {
            if (Interlocked.Exchange(ref _isRefreshing, 1) != 0)
                return;

            try
            {
                while (Interlocked.Exchange(ref _isRefreshPending, 0) != 0)
                {
                    var readiness = await _readinessService.GetReadinessAsync(_mode).ConfigureAwait(false);
                    await _executeOnUIThread(() => ApplyReadiness(readiness)).ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(ref _isRefreshing, 0);
            }

            // A request that arrived after the last pass but before the flag cleared.
            if (Volatile.Read(ref _isRefreshPending) == 0)
                return;
        }
    }

    private void ApplyReadiness(AppModeReadiness readiness)
    {
        var wasReady = IsReady;
        Readiness = readiness;

        if (wasReady != IsReady)
            ReadinessChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReadinessInvalidated(object sender, EventArgs e)
    {
        if (_isActive)
            _ = RefreshAsync();
    }

    private bool CanExecutePrimaryAction() => IsPrimaryActionVisible;

    [RelayCommand(CanExecute = nameof(CanExecutePrimaryAction))]
    private async Task PrimaryActionAsync()
    {
        switch (State)
        {
            case AppModeReadinessState.NoAccounts:
                _navigationService?.Navigate(
                    WinoPage.SettingsPage,
                    ProviderSelectionNavigationContext.CreateForSettingsAddAccount(),
                    NavigationReferenceFrame.ShellFrame);
                break;

            // Expired credentials and a mode waiting for provider consent both go through Fix account.
            case AppModeReadinessState.SignInRequired when Readiness?.AttentionAccount is { } account &&
                                                          (account.AttentionReason != AccountAttentionReason.None ||
                                                           account.CanBeFixedBySigningIn()) &&
                                                          _mailShell != null:
                await _mailShell.HandleAccountAttentionAsync(account);
                await RefreshAsync();
                break;

            default:
                // Turning a mode on, and any issue a sign-in cannot fix, live in account settings.
                ManageAccounts();
                break;
        }
    }

    [RelayCommand]
    private void ManageAccounts()
        => _navigationService?.Navigate(
            WinoPage.SettingsPage,
            WinoPage.ManageAccountsPage,
            NavigationReferenceFrame.ShellFrame);

    private string GetText(string mail, string calendar, string contacts, string tasks) => _mode switch
    {
        WinoApplicationMode.Calendar => calendar,
        WinoApplicationMode.Contacts => contacts,
        WinoApplicationMode.Tasks => tasks,
        _ => mail
    };
}
