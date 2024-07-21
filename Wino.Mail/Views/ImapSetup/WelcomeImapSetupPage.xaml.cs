using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Domain;
using Wino.Domain.Exceptions;
using Wino.Domain.Models.AutoDiscovery;
using Wino.Messaging.Client.Mails;
using Wino.Domain.Interfaces;
using Wino.Domain.Entities;



#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
#endif

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

                var failurePackage = new ImapConnectionFailedPackage(new Exception(Translator.Exception_ImapAutoDiscoveryFailed), string.Empty, discoverySettings);

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
