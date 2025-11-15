using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Mail;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class AliasManagementPage : AliasManagementPageAbstract
{
    public AliasManagementPage()
    {
        InitializeComponent();
    }

    private void SetAliasPrimary_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is RadioButton button && button.CommandParameter is MailAccountAlias alias)
        {
            ViewModel.SetAliasPrimaryCommand.Execute(alias);
        }
    }

    private void DeleteAlias_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is MailAccountAlias alias)
        {
            ViewModel.DeleteAliasCommand.Execute(alias);
        }
    }
}
