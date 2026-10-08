using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class WinoIntelligencePage : WinoIntelligencePageAbstract
{
    private bool _isApplyingConsentState;

    public WinoIntelligencePage() => InitializeComponent();

    private async void IntelligenceConsentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingConsentState || ViewModel.IsConsentBusy ||
            sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.IsConsentGranted)
        {
            return;
        }

        _isApplyingConsentState = true;
        try
        {
            if (toggle.IsOn)
            {
                ConsentPolicyAcknowledgementCheckBox.IsChecked = false;
                ConsentPolicyDialog.IsPrimaryButtonEnabled = false;
                ConsentPolicyErrorText.Visibility = Visibility.Collapsed;
                ConsentPolicyWebView.Source = ViewModel.ConsentPolicyUri;
                ConsentPolicyDialog.XamlRoot = XamlRoot;
                await ConsentPolicyDialog.ShowAsync();
            }
            else
            {
                ConsentRevokeDialog.XamlRoot = XamlRoot;
                if (await ConsentRevokeDialog.ShowAsync() == ContentDialogResult.Primary)
                    await ViewModel.SetIntelligenceConsentAsync(false);
            }

            toggle.IsOn = ViewModel.IsConsentGranted;
        }
        finally
        {
            _isApplyingConsentState = false;
        }
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
            var succeeded = await ViewModel.SetIntelligenceConsentAsync(true);
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

    private void Root_Unloaded(object sender, RoutedEventArgs e)
    {
        ConsentPolicyWebView.Source = null;
        ConsentPolicyWebView.Close();
    }
}
