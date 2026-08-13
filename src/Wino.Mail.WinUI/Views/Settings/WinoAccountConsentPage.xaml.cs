using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class WinoAccountConsentPage : WinoAccountConsentPageAbstract
{
    private ConsentDialogTarget _dialogTarget;
    private WinoConsentMailboxItemViewModel? _dialogMailbox;

    public WinoAccountConsentPage()
    {
        InitializeComponent();
    }

    private async void TransportConsentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || ViewModel.IsTransportBusy)
            return;

        if (checkBox.IsChecked == true)
        {
            await ShowPolicyDialogAsync(ConsentDialogTarget.Transport, null);
        }
        else
        {
            ConsentRevokeDialog.Title = Translator.WinoAccount_TransportConsentDisableTitle;
            ConsentRevokeMessage.Text = Translator.WinoAccount_TransportConsentDisableMessage;
            ConsentRevokeDialog.XamlRoot = XamlRoot;
            if (await ConsentRevokeDialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.SetTransportConsentAsync(false);
        }
        checkBox.IsChecked = ViewModel.IsTransportConsentGranted;
    }

    private async void ProcessConsentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: WinoConsentMailboxItemViewModel item } checkBox || item.IsBusy)
            return;

        if (checkBox.IsChecked == true)
        {
            await ShowPolicyDialogAsync(ConsentDialogTarget.Process, item);
        }
        else
        {
            ConsentRevokeDialog.Title = Translator.WinoAccount_ProcessConsentDisableTitle;
            ConsentRevokeMessage.Text = string.Format(Translator.WinoAccount_ProcessConsentDisableMessage, item.Address);
            ConsentRevokeDialog.XamlRoot = XamlRoot;
            if (await ConsentRevokeDialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.SetProcessConsentAsync(item, false);
        }
        checkBox.IsChecked = item.IsProcessConsentGranted;
    }

    private async void EnableAllProcessConsentButton_Click(object sender, RoutedEventArgs e)
        => await ShowPolicyDialogAsync(ConsentDialogTarget.BulkProcess, null);

    private async void DisableAllProcessConsentButton_Click(object sender, RoutedEventArgs e)
    {
        ConsentRevokeDialog.Title = Translator.WinoAccount_ProcessConsentDisableAllTitle;
        ConsentRevokeMessage.Text = Translator.WinoAccount_ProcessConsentDisableAllMessage;
        ConsentRevokeDialog.XamlRoot = XamlRoot;
        if (await ConsentRevokeDialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SetAllProcessConsentsAsync(false);
    }

    private async Task ShowPolicyDialogAsync(ConsentDialogTarget target, WinoConsentMailboxItemViewModel? mailbox)
    {
        _dialogTarget = target;
        _dialogMailbox = mailbox;
        ConsentPolicyDialog.Title = target == ConsentDialogTarget.Transport
            ? Translator.WinoAccount_TransportConsentPolicyTitle
            : Translator.WinoAccount_ProcessConsentPolicyTitle;
        ConsentPolicyNotice.Text = target switch
        {
            ConsentDialogTarget.Transport => Translator.WinoAccount_TransportConsentPolicyNotice,
            ConsentDialogTarget.Process => string.Format(Translator.WinoAccount_ProcessConsentPolicyNotice, mailbox!.Address),
            _ => Translator.WinoAccount_ProcessConsentBulkPolicyNotice,
        };
        ConsentPolicyAcknowledgementCheckBox.IsChecked = false;
        ConsentPolicyDialog.IsPrimaryButtonEnabled = false;
        ConsentPolicyErrorText.Visibility = Visibility.Collapsed;
        ConsentPolicyErrorText.Text = string.Empty;
        ConsentPolicyWebView.Source = target == ConsentDialogTarget.Transport
            ? ViewModel.TransportPolicyUri
            : ViewModel.ProcessPolicyUri;
        ConsentPolicyDialog.XamlRoot = XamlRoot;
        await ConsentPolicyDialog.ShowAsync();
    }

    private void ConsentPolicyAcknowledgementCheckBox_Changed(object sender, RoutedEventArgs e)
        => ConsentPolicyDialog.IsPrimaryButtonEnabled = ConsentPolicyAcknowledgementCheckBox.IsChecked == true;

    private async void ConsentPolicyDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        sender.IsPrimaryButtonEnabled = false;
        try
        {
            var succeeded = _dialogTarget switch
            {
                ConsentDialogTarget.Transport => await ViewModel.SetTransportConsentAsync(true),
                ConsentDialogTarget.Process when _dialogMailbox is not null => await ViewModel.SetProcessConsentAsync(_dialogMailbox, true),
                ConsentDialogTarget.BulkProcess => await EnableAllAsync(),
                _ => false,
            };
            args.Cancel = !succeeded;
            if (!succeeded)
            {
                ConsentPolicyErrorText.Text = Translator.Intelligence_ConsentAcceptanceFailed;
                ConsentPolicyErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            sender.IsPrimaryButtonEnabled = ConsentPolicyAcknowledgementCheckBox.IsChecked == true;
            deferral.Complete();
        }
    }

    private async Task<bool> EnableAllAsync()
    {
        await ViewModel.SetAllProcessConsentsAsync(true);
        return ViewModel.Mailboxes.All(x => x.IsProcessConsentGranted);
    }

    private void Root_Unloaded(object sender, RoutedEventArgs e)
    {
        ConsentPolicyWebView.Source = null;
        ConsentPolicyWebView.Close();
    }

    private enum ConsentDialogTarget
    {
        Transport,
        Process,
        BulkProcess,
    }
}
