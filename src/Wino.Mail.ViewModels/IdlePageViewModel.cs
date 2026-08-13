using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;

namespace Wino.Mail.ViewModels;

public partial class IdlePageViewModel : CoreBaseViewModel
{
    public const string MailEmptyStateParameter = "mail-empty-state";

    private readonly INavigationService _navigationService;

    [ObservableProperty]
    public partial bool IsMailEmptyStateVisible { get; set; }

    public string MailEmptyStateTitle => Translator.MailEmptyState_Title;
    public string MailEmptyStateMessage => Translator.MailEmptyState_Message;
    public string AddAccountText => Translator.MailEmptyState_AddAccount;
    public string ManageAccountsText => Translator.MailEmptyState_ManageAccounts;

    public IdlePageViewModel(IMailDialogService dialogService, INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        IsMailEmptyStateVisible = string.Equals(parameters as string, MailEmptyStateParameter, StringComparison.Ordinal);
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
}
