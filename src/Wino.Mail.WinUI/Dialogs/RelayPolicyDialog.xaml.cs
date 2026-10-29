using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Dialogs;

/// <summary>
/// Shows the privacy policy before relay-based sync is turned on. The dialog stays open until the acceptance call
/// succeeds, so a failed call can be retried without reopening the policy. Presented only by the dialog service.
/// </summary>
public sealed partial class RelayPolicyDialog : ContentDialog
{
    private readonly Func<Task<bool>> _acceptAsync;

    public bool IsAccepted { get; private set; }

    public RelayPolicyDialog(Uri policyUri, Func<Task<bool>> acceptAsync)
    {
        _acceptAsync = acceptAsync;

        InitializeComponent();

        PolicyWebView.Source = policyUri;
    }

    private void AcknowledgementChanged(object sender, RoutedEventArgs e)
        => IsPrimaryButtonEnabled = AcknowledgementCheckBox.IsChecked == true;

    private async void AcceptClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        IsPrimaryButtonEnabled = false;

        try
        {
            IsAccepted = await _acceptAsync();
            args.Cancel = !IsAccepted;
            ErrorText.Visibility = IsAccepted ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            // An async void handler must not let the failure end the process; the dialog stays open for a retry.
            Serilog.Log.Warning(ex, "Recording the relay privacy policy acceptance failed.");
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            IsPrimaryButtonEnabled = AcknowledgementCheckBox.IsChecked == true;
            deferral.Complete();
        }
    }

    private void DialogLoaded(object sender, RoutedEventArgs e)
    {
        PolicyDialogLayout.Fit(PolicyContent, XamlRoot);
        XamlRoot.Changed += XamlRootChanged;
    }

    private void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => PolicyDialogLayout.Fit(PolicyContent, sender);

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (XamlRoot is not null)
            XamlRoot.Changed -= XamlRootChanged;

        PolicyWebView.Source = null;
        PolicyWebView.Close();
    }
}
