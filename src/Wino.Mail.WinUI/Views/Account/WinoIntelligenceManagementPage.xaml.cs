using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class WinoIntelligenceManagementPage : WinoIntelligenceManagementPageAbstract
{
    private bool _isApplyingToggleState;

    public WinoIntelligenceManagementPage()
    {
        InitializeComponent();
    }

    private async void WinoIntelligenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // Binding the stored preference while the page is loading also raises Toggled.
        // Consent is only interactive when the user changes the ready page.
        if (_isApplyingToggleState || !ViewModel.IsPageReady || sender is not ToggleSwitch toggleSwitch)
            return;

        _isApplyingToggleState = true;
        try
        {
            if (toggleSwitch.IsOn && !await EnsureProcessConsentAsync())
            {
                toggleSwitch.IsOn = false;
                return;
            }

            var actualState = await ViewModel.SetSemanticIndexingEnabledAsync(toggleSwitch.IsOn);
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingToggleState = false;
        }
    }

    /// <summary>
    /// Shows the policy dialog when this mailbox has no current process consent.
    /// Returns whether consent is in place afterwards.
    /// </summary>
    private async Task<bool> EnsureProcessConsentAsync()
    {
        if (ViewModel.HasProcessConsent)
            return true;

        ProcessConsentAcknowledgementCheckBox.IsChecked = false;
        ProcessConsentPolicyDialog.IsPrimaryButtonEnabled = false;
        ProcessConsentPolicyErrorText.Visibility = Visibility.Collapsed;
        ProcessConsentPolicyWebView.Source = ViewModel.ProcessPolicyUri;
        ProcessConsentPolicyDialog.XamlRoot = XamlRoot;

        var result = await ProcessConsentPolicyDialog.ShowAsync();
        return result == ContentDialogResult.Primary && ViewModel.HasProcessConsent;
    }

    /// <summary>
    /// Reviewing the policy is the whole consent flow: read it, accept it, and the
    /// account is turned on straight away. It never leaves the page.
    /// </summary>
    private async void ReviewPrivacyPolicyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureProcessConsentAsync())
            return;

        if (WinoIntelligenceToggle.IsOn)
            return;

        // Goes through the toggle so the enable path stays in one place.
        WinoIntelligenceToggle.IsOn = true;
    }

    private void ProcessConsentAcknowledgementCheckBox_Changed(object sender, RoutedEventArgs e)
        => ProcessConsentPolicyDialog.IsPrimaryButtonEnabled = ProcessConsentAcknowledgementCheckBox.IsChecked == true;

    private async void ProcessConsentPolicyDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        sender.IsPrimaryButtonEnabled = false;
        try
        {
            args.Cancel = !await ViewModel.AcceptProcessConsentAsync();
            if (args.Cancel)
            {
                ProcessConsentPolicyErrorText.Text = Translator.Intelligence_ConsentAcceptanceFailed;
                ProcessConsentPolicyErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            sender.IsPrimaryButtonEnabled = ProcessConsentAcknowledgementCheckBox.IsChecked == true;
            deferral.Complete();
        }
    }

    private void PageRoot_Unloaded(object sender, RoutedEventArgs e)
    {
        ProcessConsentPolicyWebView.Source = null;
        ProcessConsentPolicyWebView.Close();
    }
}
