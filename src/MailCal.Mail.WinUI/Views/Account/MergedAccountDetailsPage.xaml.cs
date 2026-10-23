using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using MailCal.Mail.ViewModels.Data;
using MailCal.Views.Abstract;


namespace MailCal.Views.Account;

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
