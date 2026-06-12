using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Accounts;

namespace Wino.Mail.ViewModels;

public partial class IdlePageViewModel : CoreBaseViewModel
{
    public const string MailEmptyStateParameter = "mail-empty-state";
    public const string CompanionUnavailableStateParameter = "companion-unavailable-state";

    private readonly IBackgroundServiceConnection _backgroundServiceConnection;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    public partial bool IsMailEmptyStateVisible { get; set; }

    [ObservableProperty]
    public partial bool IsCompanionUnavailableStateVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCompanionConnectionCommand))]
    public partial bool IsRetryingCompanionConnection { get; set; }

    public string MailEmptyStateTitle => Translator.MailEmptyState_Title;
    public string MailEmptyStateMessage => Translator.MailEmptyState_Message;
    public string AddAccountText => Translator.MailEmptyState_AddAccount;
    public string ManageAccountsText => Translator.MailEmptyState_ManageAccounts;

    public string CompanionUnavailableTitle => Translator.CompanionUnavailableState_Title;
    public string CompanionUnavailableMessage => Translator.CompanionUnavailableState_Message;
    public string RetryCompanionConnectionText => Translator.CompanionUnavailableState_Retry;

    public IdlePageViewModel(IBackgroundServiceConnection backgroundServiceConnection, INavigationService navigationService)
    {
        _backgroundServiceConnection = backgroundServiceConnection;
        _navigationService = navigationService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        var state = parameters as string;

        IsMailEmptyStateVisible = string.Equals(state, MailEmptyStateParameter, StringComparison.Ordinal);
        IsCompanionUnavailableStateVisible = string.Equals(state, CompanionUnavailableStateParameter, StringComparison.Ordinal);
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

    private bool CanRetryCompanionConnection() => !IsRetryingCompanionConnection;

    [RelayCommand(CanExecute = nameof(CanRetryCompanionConnection))]
    private async Task RetryCompanionConnectionAsync()
    {
        IsRetryingCompanionConnection = true;

        try
        {
            await _backgroundServiceConnection.EnsureConnectedAsync();
            WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(AutomaticallyNavigateFirstItem: true));
        }
        catch
        {
            IsCompanionUnavailableStateVisible = true;
        }
        finally
        {
            IsRetryingCompanionConnection = false;
        }
    }
}
