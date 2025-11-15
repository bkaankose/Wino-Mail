using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class AccountManagementPage : AccountManagementPageAbstract
{
    public AccountManagementPage()
    {
        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    private void EditMergedAccounts_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard card && card.CommandParameter is MergedAccountProviderDetailViewModel mergedAccount)
        {
            ViewModel.EditMergedAccountsCommand.Execute(mergedAccount);
        }
    }

    private void RootAccountTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard card && card.CommandParameter is AccountProviderDetailViewModel accountDetails)
        {
            ViewModel.NavigateAccountDetailsCommand.Execute(accountDetails);
        }
    }

}
