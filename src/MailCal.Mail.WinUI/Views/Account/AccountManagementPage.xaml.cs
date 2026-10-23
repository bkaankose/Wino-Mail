using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using MailCal.Core.ViewModels.Data;
using MailCal.Mail.ViewModels.Data;
using MailCal.Views.Abstract;

namespace MailCal.Views;

public sealed partial class AccountManagementPage : AccountManagementPageAbstract
{
    public AccountManagementPage()
    {
        InitializeComponent();
    }

    protected override void ReleasePageResources()
        => ReleaseTemplateSelector("AccountProviderViewModelTemplateSelector");

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
