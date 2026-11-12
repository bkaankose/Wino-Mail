using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;
using WinRT;

namespace Wino.Views.Account;

public sealed partial class AccountGroupDetailsPage : AccountGroupDetailsPageAbstract
{
    public AccountGroupDetailsPage()
    {
        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void AddMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountGroupEntryViewModel entry })
            ViewModel.AddMemberCommand.Execute(entry);
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void RemoveMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountGroupEntryViewModel entry })
            ViewModel.RemoveMemberCommand.Execute(entry);
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void MoveMemberUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountGroupEntryViewModel entry })
            ViewModel.MoveMemberUpCommand.Execute(entry);
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void MoveMemberDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountGroupEntryViewModel entry })
            ViewModel.MoveMemberDownCommand.Execute(entry);
    }
}
