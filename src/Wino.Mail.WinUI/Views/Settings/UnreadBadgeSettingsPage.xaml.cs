using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class UnreadBadgeSettingsPage : UnreadBadgeSettingsPageAbstract
{
    public UnreadBadgeSettingsPage()
    {
        InitializeComponent();
    }

    // A reflection {Binding} from the row template to the page view model has no metadata in
    // Native AOT builds, so the row button reaches the command from code-behind instead.
    private void ConfigureAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: TaskbarBadgeAccountViewModel account })
            ViewModel.ConfigureAccountCommand.Execute(account);
    }
}
