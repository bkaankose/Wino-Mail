using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;


namespace Wino.Views.Account;

public sealed partial class MergedAccountDetailsPage : MergedAccountDetailsPageAbstract
{
    public MergedAccountDetailsPage()
    {
        InitializeComponent();
    }

    private void UnlinkAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard card && card.CommandParameter is AccountProviderDetailViewModel account)
        {
            ViewModel.UnlinkAccountCommand.Execute(account);
        }
    }

    private void LinkAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard card && card.CommandParameter is AccountProviderDetailViewModel account)
        {
            ViewModel.LinkAccountCommand.Execute(account);
        }
    }
}
