using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Interfaces;
using MailCal.Mail.Controls.ContextFlyout;
using MailCal.Mail.WinUI;
using MailCal.Mail.ViewModels.Data;
using MailCal.Views.Abstract;
using WinRT;

namespace MailCal.Views.Settings;

public sealed partial class AliasManagementPage : AliasManagementPageAbstract
{
    public AliasManagementPage()
    {
        InitializeComponent();
    }

    private void SetAliasPrimary_Click(object sender, System.EventArgs e)
    {
        if (GetAlias(sender) is MailAccountAlias alias)
        {
            ViewModel.SetAliasPrimaryCommand.Execute(alias);
        }
    }

    private void DeleteAlias_Click(object sender, System.EventArgs e)
    {
        if (GetAlias(sender) is MailAccountAlias alias)
        {
            ViewModel.DeleteAliasCommand.Execute(alias);
        }
    }

    private async void CopyAliasAddress_Click(object sender, System.EventArgs e)
    {
        if (GetAlias(sender) is not MailAccountAlias alias) return;

        var clipboardService = MailCalApplication.Current.Services.GetRequiredService<IClipboardService>();
        (await clipboardService.CopyTextAsync(alias.AliasAddress)).ThrowIfNotSucceeded();
    }

    private static MailAccountAlias GetAlias(object sender)
        => (sender as MailCalContextFlyoutItem)?.CommandParameter as MailAccountAlias;

    private async void SigningCertificateDropDownClosed(object sender, object e)
    {
        var (alias, cert) = GetAliasAndSelectedCertificateForCombobox(sender);
        if (alias is not null)
        {
            await ViewModel.SetSelectedSigningCertificate(alias, cert);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(ComboBox))]
    private static (MailAccountAlias? alias, X509Certificate2? cert) GetAliasAndSelectedCertificateForCombobox(object sender)
    {
        var comboBox = sender as ComboBox;
        var alias = (comboBox?.Tag as AliasManagementItem)?.Alias;
        var selected = comboBox?.SelectedItem as X509Certificate2;

        return (alias, selected);
    }

    [DynamicWindowsRuntimeCast(typeof(ToggleSwitch))]
    private async void SmimeToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Toggled also fires while the row is being realized. Only a value that differs from the
        // stored one is a user action; anything else would write on every render and reload the list.
        if (sender is not ToggleSwitch toggle || toggle.Tag is not AliasManagementItem item) return;

        if (toggle.IsOn == item.IsSmimeEncryptionEnabled) return;

        await ViewModel.SetAliasSmimeEncryption(item.Alias, toggle.IsOn);
    }
}
