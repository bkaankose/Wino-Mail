using System.Security.Cryptography.X509Certificates;
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

    private async void SigningCertificateDropDownClosed(object sender, object e)
    {
        var (alias, cert) = GetAliasAndSelectedCertificateForCombobox(sender);
        if (alias is not null)
        {
            await ViewModel.SetSelectedSigningCertificate(alias, cert);
        }
    }

    private async void SmimeEncryptionChecked(object sender, object e)
    {
        var checkBox = sender as CheckBox;
        if (checkBox?.DataContext is MailAccountAlias alias)
        {
            await ViewModel.SetAliasSmimeEncryption(alias, checkBox.IsChecked ?? false);
        }
    }

    private static (MailAccountAlias alias, X509Certificate2 cert) GetAliasAndSelectedCertificateForCombobox(object sender)
    {
        var comboBox = sender as ComboBox;
        var alias = comboBox?.DataContext as MailAccountAlias;
        var selected = comboBox?.SelectedItem as X509Certificate2;
        return (alias, selected);
    }
}
