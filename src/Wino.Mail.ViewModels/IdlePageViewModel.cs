using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The blank page. In the mail page area it also stands in for mail content that cannot be
/// shown yet, and then it owns the mail account list so the pane is never left empty.
/// </summary>
public partial class IdlePageViewModel : CoreBaseViewModel, IShellMenuOwner
{
    public const string MailEmptyStateParameter = "mail-empty-state";

    private readonly INavigationService _navigationService;
    private readonly IAccountService _accountService;
    private readonly IMailShellClient _mailShell;
    private Guid? _idleAccountId;

    [ObservableProperty]
    public partial bool IsMailEmptyStateVisible { get; set; }

    [ObservableProperty]
    public partial bool IsAccountStateVisible { get; set; }

    [ObservableProperty]
    public partial string AccountStateTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountStateMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsSignInVisible { get; set; }

    public string MailEmptyStateTitle => Translator.MailEmptyState_Title;
    public string MailEmptyStateMessage => Translator.MailEmptyState_Message;
    public string AddAccountText => Translator.MailEmptyState_AddAccount;
    public string ManageAccountsText => Translator.MailEmptyState_ManageAccounts;
    public string SignInText => Translator.MailAccountIdle_SignIn;

    /// <summary>
    /// Only the mail states own the pane. Everywhere else this page is a plain placeholder
    /// and leaves the current menu alone.
    /// </summary>
    public IShellMenuProvider ShellMenuProvider => IsMailEmptyStateVisible || IsAccountStateVisible ? _mailShell : null;

    public IdlePageViewModel(IMailDialogService dialogService,
                             INavigationService navigationService,
                             IAccountService accountService = null,
                             IMailShellClient mailShell = null)
    {
        _navigationService = navigationService;
        _accountService = accountService;
        _mailShell = mailShell;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        IsMailEmptyStateVisible = string.Equals(parameters as string, MailEmptyStateParameter, StringComparison.Ordinal);

        if (parameters is MailAccountIdleState state)
        {
            _idleAccountId = state.AccountId;
            IsAccountStateVisible = true;
            IsSignInVisible = state.NeedsSignIn;
            AccountStateTitle = state.NeedsSignIn
                ? string.Format(Translator.MailAccountIdle_SignInTitle, state.AccountName)
                : Translator.MailAccountIdle_LoadingTitle;
            AccountStateMessage = state.NeedsSignIn
                ? Translator.MailAccountIdle_SignInMessage
                : Translator.MailAccountIdle_LoadingMessage;
        }
        else
        {
            _idleAccountId = null;
            IsAccountStateVisible = false;
            IsSignInVisible = false;
        }
    }

    [RelayCommand]
    private void AddAccount()
        => _navigationService.Navigate(
            WinoPage.SettingsPage,
            ProviderSelectionNavigationContext.CreateForSettingsAddAccount(),
            NavigationReferenceFrame.ShellFrame);

    [RelayCommand]
    private void ManageAccounts()
        => _navigationService.Navigate(
            WinoPage.SettingsPage,
            WinoPage.ManageAccountsPage,
            NavigationReferenceFrame.ShellFrame);

    [RelayCommand(CanExecute = nameof(IsSignInVisible))]
    private async Task SignInAsync()
    {
        if (_idleAccountId is not { } accountId || _accountService == null || _mailShell == null)
            return;

        var account = await _accountService.GetAccountAsync(accountId);
        if (account != null)
        {
            await _mailShell.HandleAccountAttentionAsync(account);
        }
    }
}
