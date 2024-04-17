using System;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Messages.Mails;
using Wino.Extensions;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Wino.Views.ImapSetup
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class WelcomeImapSetupPage : Page
    {
        private AutoDiscoveryConnectionTestFailedPackage failedPackage;

        public WelcomeImapSetupPage()
        {
            InitializeComponent();
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        }

        private void SignInClicked(object sender, RoutedEventArgs e)
        {
            failedPackage = null;

            var minimalSettings = new AutoDiscoveryMinimalSettings()
            {
                Password = PasswordBox.Password,
                DisplayName = DisplayNameBox.Text,
                Email = AddressBox.Text,
            };

            WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(AutoDiscoveryPage), minimalSettings));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string errorMessage)
            {
                ErrorMessageText.Text = errorMessage;

                MainScrollviewer.ScrollToElement(ErrorMessageText);
            }
            else if (e.Parameter is AutoDiscoveryConnectionTestFailedPackage autoDiscoveryConnectionTestFailedPackage)
            {
                failedPackage = autoDiscoveryConnectionTestFailedPackage;
                ErrorMessageText.Text = $"Discovery was successful but connection to the server failed.{Environment.NewLine}{Environment.NewLine}{autoDiscoveryConnectionTestFailedPackage.Error.Message}";

                MainScrollviewer.ScrollToElement(ErrorMessageText);
            }
        }

        private void CancelClicked(object sender, RoutedEventArgs e)
        {
            WeakReferenceMessenger.Default.Send(new ImapSetupDismissRequested());
        }

        private void AdvancedConfigurationClicked(object sender, RoutedEventArgs e)
        {
            var latestMinimalSettings = new AutoDiscoveryMinimalSettings()
            {
                DisplayName = DisplayNameBox.Text,
                Password = PasswordBox.Password,
                Email = AddressBox.Text
            };

            if (failedPackage != null)
            {
                // Go to advanced settings with updated minimal settings.

                failedPackage.Settings.UserMinimalSettings = latestMinimalSettings;

                WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(AdvancedImapSetupPage), failedPackage.Settings));
            }
            else
            {
                // Go to advanced page.

                WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(AdvancedImapSetupPage), latestMinimalSettings));
            }
        }
    }
}
