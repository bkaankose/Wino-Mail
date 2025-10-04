using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Services;
using Wino.Messaging.Client.Mails;


namespace Wino.Views.ImapSetup;

public sealed partial class TestingImapConnectionPage : Page
{
    private AutoDiscoverySettings autoDiscoverySettings;
    private CustomServerInformation serverInformationToTest;

    public TestingImapConnectionPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // We can only go back to this page from failed connection page.
        // We must go back once again in that case to actual setup dialog.
        if (e.NavigationMode == NavigationMode.Back)
        {
            WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested());
        }
        else
        {
            // Test connection

            // Discovery settings are passed.
            // Create server information out of the discovery settings.
            if (e.Parameter is AutoDiscoverySettings parameterAutoDiscoverySettings)
            {
                autoDiscoverySettings = parameterAutoDiscoverySettings;
                serverInformationToTest = autoDiscoverySettings.ToServerInformation();
            }
            else if (e.Parameter is CustomServerInformation customServerInformation)
            {
                // Only server information is passed.
                serverInformationToTest = customServerInformation;
            }

            // Make sure that certificate dialog must be present in case of SSL handshake fails.
            await PerformTestAsync(allowSSLHandshake: false);
        }
    }

    private async Task PerformTestAsync(bool allowSSLHandshake)
    {
        CertificateDialog.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        TestingConnectionPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        await Task.Delay(1000);

        var testResultData = await SynchronizationManager.Instance.TestImapConnectivityAsync(serverInformationToTest, allowSSLHandshake);

        if (testResultData.IsSuccess)
        {
            // All success. Finish setup with validated server information.
            ReturnWithSuccess();
        }
        else
        {
            // Check if certificate UI is required.
            if (testResultData.IsCertificateUIRequired)
            {
                // Certificate UI is required. Show certificate dialog.

                CertIssuer.Text = testResultData.CertificateIssuer;
                CertValidFrom.Text = testResultData.CertificateValidFromDateString;
                CertValidTo.Text = testResultData.CertificateExpirationDateString;

                TestingConnectionPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                CertificateDialog.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
            else
            {
                // Connection test failed. Show error dialog.

                var protocolLog = testResultData.FailureProtocolLog;

                ReturnWithError(testResultData.FailedReason, protocolLog);
            }
        }
    }

    private void ReturnWithError(string error, string protocolLog = "")
    {
        var failurePackage = new ImapConnectionFailedPackage(error, protocolLog, autoDiscoverySettings);
        WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested(typeof(ImapConnectionFailedPage), failurePackage));
    }

    private void ReturnWithSuccess()
        => WeakReferenceMessenger.Default.Send(new ImapSetupDismissRequested(serverInformationToTest));

    private void DenyClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ReturnWithError(Translator.IMAPSetupDialog_CertificateDenied, string.Empty);

    private async void AllowClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Run the test again, but this time allow SSL handshake.
        // Any authentication error will be shown to the user after this test.

        await PerformTestAsync(allowSSLHandshake: true);
    }
}
