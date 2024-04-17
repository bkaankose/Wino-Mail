using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AppCenter.Analytics;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Messages.Mails;


namespace Wino.Views.ImapSetup
{
    public sealed partial class TestingImapConnectionPage : Page
    {
        private IImapTestService _imapTestService;

        public TestingImapConnectionPage()
        {
            InitializeComponent();

            _imapTestService = App.Current.Services.GetService<IImapTestService>();
        }

        private async Task TryTestConnectionAsync(CustomServerInformation serverInformation)
        {
            await Task.Delay(1000);

            await _imapTestService.TestImapConnectionAsync(serverInformation);

            // All success. Finish setup with validated server information.

            WeakReferenceMessenger.Default.Send(new ImapSetupDismissRequested(serverInformation));
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // We either go back to welcome setup page or advanced config page.
            // Based on if we come from auto discovery or not.

            if (e.Parameter is AutoDiscoverySettings autoDiscoverySettings)
            {
                var serverInformation = autoDiscoverySettings.ToServerInformation();

                try
                {
                    await TryTestConnectionAsync(serverInformation);
                }
                catch (Exception ex)
                {
                    WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested(typeof(WelcomeImapSetupPage),
                                                                                             new AutoDiscoveryConnectionTestFailedPackage(autoDiscoverySettings, ex)));
                }
            }
            else if (e.Parameter is CustomServerInformation customServerInformation)
            {
                try
                {
                    await TryTestConnectionAsync(customServerInformation);
                }
                catch (Exception ex)
                {
                    Analytics.TrackEvent("IMAP Test Failed", new Dictionary<string, string>()
                    {
                        { "Server", customServerInformation.IncomingServer },
                        { "Port", customServerInformation.IncomingServerPort },
                    });

                    WeakReferenceMessenger.Default.Send(new ImapSetupBackNavigationRequested(typeof(AdvancedImapSetupPage),
                                                                                             new AutoDiscoveryConnectionTestFailedPackage(ex)));
                }
            }
        }
    }
}
