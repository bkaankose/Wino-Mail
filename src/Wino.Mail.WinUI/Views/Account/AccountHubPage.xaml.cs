using Microsoft.UI.Xaml;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Account;

public sealed partial class AccountHubPage : AccountHubPageAbstract
{
    public AccountHubPage()
    {
        InitializeComponent();
    }

    private void AppRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is WinoSettingsCard { Tag: AccountAppItemViewModel item })
            ViewModel.OpenAppCommand.Execute(item.App);
    }
}
