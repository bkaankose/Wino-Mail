using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Messaging.Client.Mails;


namespace Wino.Views.ImapSetup
{
    public sealed partial class WelcomeImapSetupPage : Page
    {
        private readonly IAutoDiscoveryService _autoDiscoveryService = App.Current.Services.GetService<IAutoDiscoveryService>();

        public WelcomeImapSetupPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            AutoDiscoveryPanel.Visibility = Visibility.Collapsed;
            MainSetupPanel.Visibility = Visibility.Visible;

            if (e.Parameter is MailAccount accountProperties)
            {
                DisplayNameBox.Text = accountProperties.Name;
            }
            else if (e.Parameter is AccountCreationDialogResult creationDialogResult)
            {
                WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(TestingImapConnectionPage), creationDialogResult));
            }
        }

        private async void SignInClicked(object sender, RoutedEventArgs e)
        {
            MainSetupPanel.Visibility = Visibility.Collapsed;
            AutoDiscoveryPanel.Visibility = Visibility.Visible;

            // Let users see the discovery message for a while...

            await Task.Delay(1000);

            var minimalSettings = new AutoDiscoveryMinimalSettings()
            {
                Password = PasswordBox.Password,
                DisplayName = DisplayNameBox.Text,
                Email = AddressBox.Text,
            };

            var discoverySettings = await _autoDiscoveryService.GetAutoDiscoverySettings(minimalSettings);

            if (discoverySettings == null)
            {
                // Couldn't find settings.

                var failurePackage = new ImapConnectionFailedPackage(Translator.Exception_ImapAutoDiscoveryFailed, string.Empty, discoverySettings);

                WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested(typeof(ImapConnectionFailedPage), failurePackage));
            }
            else
            {
                // Settings are found. Test the connection with the given password.

                discoverySettings.UserMinimalSettings = minimalSettings;

                WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(TestingImapConnectionPage), discoverySettings));
            }
        }

        private void CancelClicked(object sender, RoutedEventArgs e) => WeakReferenceMessenger.Default.Send(new ImapSetupDismissRequested());

        private void AdvancedConfigurationClicked(object sender, RoutedEventArgs e)
        {
            var latestMinimalSettings = new AutoDiscoveryMinimalSettings()
            {
                DisplayName = DisplayNameBox.Text,
                Password = PasswordBox.Password,
                Email = AddressBox.Text
            };


            WeakReferenceMessenger.Default.Send(new ImapSetupNavigationRequested(typeof(AdvancedImapSetupPage), latestMinimalSettings));
        }
    }
}
