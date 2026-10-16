using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Wino.Core.Domain;
using Wino.Views.Abstract;
using WinRT;

namespace Wino.Views.Settings;

public sealed partial class WinoIntelligencePage : WinoIntelligencePageAbstract
{
    private bool _isApplyingConsentState;

    public WinoIntelligencePage() => InitializeComponent();

    [DynamicWindowsRuntimeCast(typeof(ToggleSwitch))]
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
                await ShowConsentDialogAsync(ConsentPolicyDialog);
            }
            else
            {
                if (await ShowConsentDialogAsync(ConsentRevokeDialog) == ContentDialogResult.Primary)
                    await ViewModel.SetIntelligenceConsentAsync(false);
            }

            toggle.IsOn = ViewModel.IsConsentGranted;
        }
        finally
        {
            _isApplyingConsentState = false;
        }
    }

    /// <summary>
    /// The consent dialogs are declared in this page, so they already share its XamlRoot.
    /// Assigning a different root throws "already associated with a XamlRoot", so only an
    /// unrooted dialog is rooted, and a dialog that cannot be shown leaves the toggle on the
    /// current consent state.
    /// </summary>
    private async Task<ContentDialogResult> ShowConsentDialogAsync(ContentDialog dialog)
    {
        try
        {
            dialog.XamlRoot ??= XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or COMException)
        {
            Log.Warning(ex, "Wino Intelligence consent dialog could not be shown.");
            return ContentDialogResult.None;
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
