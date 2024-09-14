using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Server;


namespace Wino.Views.ImapSetup
{
    public sealed partial class TestingImapConnectionPage : Page
    {
        private IWinoServerConnectionManager _winoServerConnectionManager = App.Current.Services.GetService<IWinoServerConnectionManager>();
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
            CertificateDialog.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
            TestingConnectionPanel.Visibility = Windows.UI.Xaml.Visibility.Visible;

            await Task.Delay(1000);

            var testResultResponse = await _winoServerConnectionManager
                .GetResponseAsync<ImapConnectivityTestResults, ImapConnectivityTestRequested>(new ImapConnectivityTestRequested(serverInformationToTest, allowSSLHandshake));

            if (!testResultResponse.IsSuccess)
            {
                // Wino Server is connection is failed.
                ReturnWithError(testResultResponse.Message);
            }
            else
            {
                var testResultData = testResultResponse.Data;

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

                        TestingConnectionPanel.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                        CertificateDialog.Visibility = Windows.UI.Xaml.Visibility.Visible;
                    }
                    else
                    {
                        // Connection test failed. Show error dialog.

                        var protocolLog = testResultData.FailureProtocolLog;

                        ReturnWithError(testResultData.FailedReason, protocolLog);
                    }
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

        private void DenyClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
            => ReturnWithError(Translator.IMAPSetupDialog_CertificateDenied, string.Empty);

        private async void AllowClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            // Run the test again, but this time allow SSL handshake.
            // Any authentication error will be shown to the user after this test.

            await PerformTestAsync(allowSSLHandshake: true);
        }
    }
}
